namespace Platform.Kernel.Tenancy;

/// <summary>
/// Marks a platform catalog entity that carries a tenant id without being tenant-owned (it is read
/// before a tenant is known). Architecture tests require every entity with a <c>TenantId</c> to be
/// either <see cref="Contracts.Tenancy.ITenantOwned"/> or explicitly marked with this attribute, so
/// forgetting <c>ITenantOwned</c> (and with it the query filter) cannot happen silently.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TenantCatalogAttribute : Attribute;
