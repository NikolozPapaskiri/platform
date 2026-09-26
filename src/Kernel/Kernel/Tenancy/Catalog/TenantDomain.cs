using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tenancy.Catalog;

/// <summary>
/// Maps a host name (for example <c>tickets.organizer.example</c>) to the tenant it serves. A host
/// belongs to at most one tenant; a tenant may have several hosts.
/// </summary>
/// <remarks>
/// Carries a <c>TenantId</c> but is catalog data, not tenant-owned: it is how a tenant is found in
/// the first place, so it must be readable before any tenant is resolved.
/// </remarks>
[TenantCatalog]
public sealed class TenantDomain
{
    private TenantDomain()
    {
        Host = string.Empty;
    }

    public TenantDomain(string host, TenantId tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        Host = host;
        TenantId = tenantId;
    }

    /// <summary>The host name exactly as stored; lookups decide how requests are normalised.</summary>
    public string Host { get; private set; }

    public TenantId TenantId { get; private set; }
}
