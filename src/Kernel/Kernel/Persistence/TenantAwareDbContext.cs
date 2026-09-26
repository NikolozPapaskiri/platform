using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Persistence;

/// <summary>
/// Base class for every DbContext that stores tenant-owned data. It is the EF Core half of tenant
/// isolation (PostgreSQL row-level security is the other half, see ADR 0003):
/// <list type="bullet">
/// <item>every <see cref="ITenantOwned"/> entity gets the named <see cref="TenantFilterName"/> query
/// filter, added after the derived context's own configuration so it cannot be forgotten;</item>
/// <item><c>SaveChanges</c> stamps the current tenant on inserts and refuses tenant-owned writes
/// without a resolved tenant or with another tenant's id;</item>
/// <item><c>TenantId</c> is a concurrency token, so every UPDATE and DELETE EF Core issues also
/// matches <c>tenant_id</c>. A detached entity that claims the current tenant but carries another
/// tenant's key therefore affects zero rows (a concurrency exception) instead of that tenant's row;</item>
/// <item>the database rejects an empty tenant id, which is the value the filter uses for "no tenant".</item>
/// </list>
/// <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> bypass <c>SaveChanges</c>: the query filter still
/// scopes their WHERE clause, but only row-level security stops one of them from setting another
/// tenant's id. That is one reason both layers are mandatory.
/// </summary>
public abstract class TenantAwareDbContext(DbContextOptions options, ITenantContext tenantContext)
    : DbContext(options)
{
    /// <summary>
    /// Name of the tenant query filter. EF Core 10 supports several named filters per entity, so a
    /// later soft-delete filter can be switched off without also switching off tenant isolation.
    /// </summary>
    public const string TenantFilterName = "Tenant";

    /// <summary>
    /// Read by the query filter each time a query runs; EF Core treats a member of the context as a
    /// parameter, so every context instance filters by its own tenant. With no tenant resolved it is
    /// the default (empty) id, which no row can have, so the filter fails closed: zero rows.
    /// </summary>
    protected TenantId CurrentTenantId => tenantContext.IsResolved ? tenantContext.TenantId : default;

    /// <summary>Configure entities here. The tenant filters are applied after this runs.</summary>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureModel(modelBuilder);
        ApplyTenantIsolation(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // snake_case tables and columns: raw SQL and RLS policies read naturally in PostgreSQL
        // without quoting every identifier.
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyTenantIsolation(ModelBuilder modelBuilder)
    {
        var tenantOwned = modelBuilder.Model.GetEntityTypes()
            .Where(type => type.BaseType is null && typeof(ITenantOwned).IsAssignableFrom(type.ClrType))
            .ToList();

        foreach (var entityType in tenantOwned)
        {
            // entity => entity.TenantId == this.CurrentTenantId
            var entity = Expression.Parameter(entityType.ClrType, "entity");
            var filter = Expression.Lambda(
                Expression.Equal(
                    Expression.Property(entity, nameof(ITenantOwned.TenantId)),
                    Expression.Property(Expression.Constant(this), nameof(CurrentTenantId))),
                entity);

            var builder = modelBuilder.Entity(entityType.ClrType);
            builder.HasQueryFilter(TenantFilterName, filter);
            builder.HasIndex(nameof(ITenantOwned.TenantId));
            builder.Property(nameof(ITenantOwned.TenantId)).IsConcurrencyToken();

            // The empty id is the "no tenant" sentinel above; no stored row may ever carry it.
            // The column name follows the snake_case convention this context applies.
            builder.ToTable(table => table.HasCheckConstraint(
                "ck_tenant_id_not_empty",
                $"tenant_id <> '{Guid.Empty}'"));
        }
    }

    private void EnforceTenantOnWrites()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (!tenantContext.IsResolved)
            {
                throw new InvalidOperationException(
                    $"Cannot save {entry.Metadata.DisplayName()} ({entry.State}): no tenant is resolved. " +
                    "Tenant-owned data can only be written inside a request for a tenant or through " +
                    "TenantScopeRunner.RunAsTenantAsync.");
            }

            EnforceTenant(entry, tenantContext.TenantId);
        }
    }

    private static void EnforceTenant(EntityEntry<ITenantOwned> entry, TenantId current)
    {
        var tenantId = entry.Property(e => e.TenantId);

        if (entry.State == EntityState.Added && tenantId.CurrentValue == default)
        {
            tenantId.CurrentValue = current;
            return;
        }

        var belongsToCurrent = tenantId.CurrentValue == current
            && (entry.State == EntityState.Added || tenantId.OriginalValue == current);

        if (!belongsToCurrent)
        {
            throw new InvalidOperationException(
                $"Cannot save {entry.Metadata.DisplayName()} ({entry.State}): it belongs to another " +
                $"tenant than the current one ({current}). Rows never move between tenants.");
        }
    }

    private sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(
        tenantId => tenantId.Value,
        value => new TenantId(value));
}
