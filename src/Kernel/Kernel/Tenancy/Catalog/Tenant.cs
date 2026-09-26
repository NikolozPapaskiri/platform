using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tenancy.Catalog;

/// <summary>
/// A tenant (organizer) in the platform catalog. Catalog rows are platform data, not tenant data:
/// tenant resolution has to read them before any tenant is known.
/// </summary>
[TenantCatalog]
public sealed class Tenant
{
    private Tenant()
    {
        Slug = string.Empty;
    }

    public Tenant(TenantId id, string slug, TenantStatus status, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        Id = id;
        Slug = slug;
        Status = status;
        CreatedAt = createdAt;
    }

    public TenantId Id { get; private set; }

    /// <summary>Stable, URL-safe short name, unique across the platform.</summary>
    public string Slug { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
