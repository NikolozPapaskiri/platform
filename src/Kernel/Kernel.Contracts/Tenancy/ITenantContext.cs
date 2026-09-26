namespace Platform.Kernel.Contracts.Tenancy;

/// <summary>
/// The tenant the current unit of work (an HTTP request, or a background job running as a named
/// tenant) acts for. Scoped: one instance per request or job, and it never changes tenant once set.
/// </summary>
public interface ITenantContext
{
    /// <summary>True once a tenant has been resolved for this scope.</summary>
    bool IsResolved { get; }

    /// <summary>The resolved tenant. Throws when <see cref="IsResolved"/> is false.</summary>
    /// <exception cref="InvalidOperationException">No tenant is resolved for this scope.</exception>
    TenantId TenantId { get; }
}
