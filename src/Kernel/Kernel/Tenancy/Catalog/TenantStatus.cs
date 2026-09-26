namespace Platform.Kernel.Tenancy.Catalog;

public enum TenantStatus
{
    /// <summary>Serving traffic normally.</summary>
    Active = 1,

    /// <summary>Known to the platform but not allowed to serve requests (HTTP 403).</summary>
    Suspended = 2,
}
