using Microsoft.EntityFrameworkCore;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;
using Platform.Kernel.Tenancy.Catalog;

namespace Platform.Kernel.Persistence;

/// <summary>The kernel's own data: the tenant catalog and the outbox.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
    : TenantAwareDbContext(options, tenantContext)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).ValueGeneratedNever();
            tenant.Property(t => t.Slug).HasMaxLength(63);
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<TenantDomain>(domain =>
        {
            domain.HasKey(d => d.Host);
            domain.Property(d => d.Host).HasMaxLength(253); // longest valid DNS name
            domain.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            domain.HasIndex(d => d.TenantId);
        });

        modelBuilder.Entity<OutboxMessage>(message =>
        {
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).ValueGeneratedNever();
            message.Property(m => m.Type).HasMaxLength(200);
            message.Property(m => m.Payload).HasColumnType("jsonb");
        });
    }
}
