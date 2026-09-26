using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tenancy;

/// <summary>
/// Scoped holder for the current tenant. Only Kernel code sets it: the tenant resolution middleware
/// for HTTP requests, and <see cref="TenantScopeRunner"/> for background work.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private TenantId? _tenantId;

    public bool IsResolved => _tenantId is not null;

    public TenantId TenantId => _tenantId ?? throw new InvalidOperationException(
        "No tenant is resolved for this scope. Requests get one from tenant resolution; background " +
        "work must run through TenantScopeRunner.RunAsTenantAsync.");

    /// <summary>Resolves the tenant for this scope. A scope serves one tenant, so it cannot switch.</summary>
    internal void Set(TenantId tenantId)
    {
        // default(TenantId) bypasses the constructor's check. It is also the "no tenant" sentinel the
        // query filter uses, so accepting it would let a scope write rows that tenant-less code reads.
        if (tenantId == default)
        {
            throw new ArgumentException("The empty tenant id cannot be resolved as a tenant.", nameof(tenantId));
        }

        if (_tenantId is { } current && current != tenantId)
        {
            throw new InvalidOperationException(
                $"This scope is already resolved to tenant {current}; it cannot switch to {tenantId}.");
        }

        _tenantId = tenantId;
    }
}
