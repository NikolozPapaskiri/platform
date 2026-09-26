namespace Platform.Kernel.Contracts.Tenancy;

/// <summary>
/// Marks an entity whose rows belong to exactly one tenant. The Kernel persistence layer then:
/// <list type="bullet">
/// <item>adds the tenant query filter automatically, so queries only see the current tenant's rows;</item>
/// <item>stamps <see cref="TenantId"/> on insert from the current tenant context;</item>
/// <item>refuses to save the entity when no tenant is resolved, or when it carries another tenant's id.</item>
/// </list>
/// PostgreSQL row-level security enforces the same rule a second time, underneath EF Core.
/// The property is get-only on purpose: code never assigns a tenant, it inherits one.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
