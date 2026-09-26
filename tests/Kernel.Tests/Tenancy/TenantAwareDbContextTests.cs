using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;
using Platform.Kernel.Persistence;
using Platform.Kernel.Tenancy;
using Platform.Kernel.Tests.Support;

namespace Platform.Kernel.Tests.Tenancy;

/// <summary>
/// The EF Core half of tenant isolation (query filters and write enforcement). These run today; the
/// PostgreSQL half (row-level security) is covered by <see cref="TenancyContractTests"/>.
/// </summary>
[Collection(TenancyDatabaseCollection.Name)]
public sealed class TenantAwareDbContextTests(TenancyDatabase database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EfQuery_AsTenantA_ReturnsOnlyTenantARows()
    {
        // Contract test 2: tenant A cannot read tenant B's rows through EF.
        await using var services = database.CreateServices();

        var seen = await services.GetRequiredService<TenantScopeRunner>()
            .RunAsTenantAsync(database.TenantA, TenantRows.ReadWithEfAsync);

        Assert.NotEmpty(seen);
        Assert.All(seen, tenant => Assert.Equal(database.TenantA.Value, tenant));
    }

    [Fact]
    public async Task EfQuery_WithoutTenant_ReturnsNoRows()
    {
        // Contract test 7, EF half: code with no tenant sees nothing, not everything.
        await using var services = database.CreateServices();
        await using var scope = services.CreateAsyncScope();

        var seen = await TenantRows.ReadWithEfAsync(scope.ServiceProvider);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task Insert_AsTenant_StampsTheTenantId()
    {
        await using var services = database.CreateServices();
        var id = Guid.CreateVersion7();

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            db.OutboxMessages.Add(new OutboxMessage(id, "Probe", "{}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(database.TenantA.Value, await StoredTenantOfAsync(id));
    }

    [Fact]
    public async Task Insert_WithoutTenant_ThrowsAndWritesNothing()
    {
        await using var services = database.CreateServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = Guid.CreateVersion7();

        db.OutboxMessages.Add(new OutboxMessage(id, "Probe", "{}", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        Assert.Null(await StoredTenantOfAsync(id));
    }

    [Fact]
    public async Task Insert_CarryingAnotherTenantsId_Throws()
    {
        await using var services = database.CreateServices();
        var id = Guid.CreateVersion7();

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            var message = new OutboxMessage(id, "Probe", "{}", DateTimeOffset.UtcNow);
            db.OutboxMessages.Add(message);
            db.Entry(message).Property(m => m.TenantId).CurrentValue = database.TenantB;

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Null(await StoredTenantOfAsync(id));
    }

    [Fact]
    public async Task Update_MovingARowToAnotherTenant_Throws()
    {
        await using var services = database.CreateServices();
        var id = database.TenantARows[0];

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Id == id, Ct);
            db.Entry(message).Property(m => m.TenantId).CurrentValue = database.TenantB;

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(database.TenantA.Value, await StoredTenantOfAsync(id));
    }

    [Fact]
    public async Task DetachedUpdate_OfAnotherTenantsRow_AffectsNothing()
    {
        // A detached entity that claims tenant A but carries tenant B's key: without TenantId as a
        // concurrency token, EF would UPDATE by key alone and pull B's row into tenant A.
        await using var services = database.CreateServices();
        var victim = database.TenantBRows[0];

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            var forged = new OutboxMessage(victim, "Forged", "{}", DateTimeOffset.UtcNow);
            db.OutboxMessages.Update(forged);
            ClaimTenant(db, forged, database.TenantA);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(database.TenantB.Value, await StoredTenantOfAsync(victim));
    }

    [Fact]
    public async Task DetachedRemove_OfAnotherTenantsRow_DeletesNothing()
    {
        await using var services = database.CreateServices();
        var victim = database.TenantBRows[1];

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            var forged = new OutboxMessage(victim, "Forged", "{}", DateTimeOffset.UtcNow);
            db.OutboxMessages.Remove(forged);
            ClaimTenant(db, forged, database.TenantA);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(database.TenantB.Value, await StoredTenantOfAsync(victim));
    }

    [Fact]
    public async Task Database_RejectsTheEmptyTenantId()
    {
        // The empty id is the filter's "no tenant" sentinel; a row carrying it would be visible to
        // tenant-less code. The check constraint stops it even for writes that bypass EF Core.
        await using var owner = database.CreateOwnerDataSource();
        await using var insert = owner.CreateCommand(
            "INSERT INTO outbox_messages (id, tenant_id, type, payload, occurred_at) " +
            "VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000000', 'Probe', '{}'::jsonb, now())");

        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task AppRole_CannotChangeTheCatalog()
    {
        // The catalog has no row-level security, so write access would let the application repoint
        // another tenant's host to itself. It only ever needs to read it.
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var update = app.CreateCommand("UPDATE tenant_domains SET tenant_id = tenant_id");

        var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Catalog_IsReadableWithoutTenant()
    {
        // Tenant resolution reads the catalog before any tenant is known, so it must not be filtered.
        await using var services = database.CreateServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var host = await db.TenantDomains.SingleAsync(d => d.Host == TenancyDatabase.TenantAHost, Ct);

        Assert.Equal(database.TenantA, host.TenantId);
    }

    [Fact]
    public async Task RunAsTenant_UsesAFreshScopePerCall()
    {
        await using var services = database.CreateServices();
        var runner = services.GetRequiredService<TenantScopeRunner>();

        var seenByA = await runner.RunAsTenantAsync(database.TenantA, TenantRows.ReadWithEfAsync);
        var seenByB = await runner.RunAsTenantAsync(database.TenantB, TenantRows.ReadWithEfAsync);

        Assert.All(seenByA, tenant => Assert.Equal(database.TenantA.Value, tenant));
        Assert.All(seenByB, tenant => Assert.Equal(database.TenantB.Value, tenant));
        Assert.NotEmpty(seenByB);
    }

    [Fact]
    public async Task AppRole_OwnsNoTablesIsNotSuperuserAndCannotBypassRls()
    {
        // Contract test 6, role half: row-level security is meaningless if the application's role
        // owns the tables (owners skip RLS unless it is forced), is a superuser, or has BYPASSRLS.
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT r.rolsuper, r.rolbypassrls, " +
            "(SELECT count(*) FROM pg_tables t WHERE t.tableowner = current_user) " +
            "FROM pg_roles r WHERE r.rolname = current_user");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.False(reader.GetBoolean(0), "application role is a superuser");
        Assert.False(reader.GetBoolean(1), "application role has BYPASSRLS");
        Assert.Equal(0L, reader.GetInt64(2));
    }

    private static void ClaimTenant(PlatformDbContext db, OutboxMessage message, TenantId tenant)
    {
        var tenantId = db.Entry(message).Property(m => m.TenantId);
        tenantId.CurrentValue = tenant;
        tenantId.OriginalValue = tenant;
    }

    private async Task<Guid?> StoredTenantOfAsync(Guid id)
    {
        await using var dataSource = database.CreateOwnerDataSource();
        await using var command = dataSource.CreateCommand("SELECT tenant_id FROM outbox_messages WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (Guid?)await command.ExecuteScalarAsync(Ct);
    }
}
