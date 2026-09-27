using System.Diagnostics;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Telemetry;

/// <summary>
/// Makes the current tenant visible to telemetry (ADR 0003: <c>tenant.id</c> on spans, logs, and
/// metrics once resolved). Telemetry processors run outside dependency injection, so the scope's
/// <see cref="ITenantContext"/> itself travels in an <see cref="AsyncLocal{T}"/>.
/// </summary>
/// <remarks>
/// The context object is bound at the start of the unit of work, before the tenant is known (by
/// <see cref="TenantTelemetryMiddleware"/> for requests, and by <c>TenantScopeRunner</c> for
/// background work), and telemetry reads the tenant through it. Binding the object rather than the
/// tenant value matters: an <see cref="AsyncLocal{T}"/> value assigned inside an awaited helper is
/// discarded when that helper returns, but a change to an object bound further out is seen by all
/// the work that follows. So it does not matter how deep tenant resolution sets the tenant.
/// </remarks>
public static class TenantTelemetry
{
    /// <summary>The attribute name used on spans, log records, and metrics.</summary>
    public const string TenantIdAttribute = "tenant.id";

    private static readonly AsyncLocal<ITenantContext?> _context = new();

    /// <summary>
    /// The tenant of the work running on this logical call path, if resolved. For telemetry only:
    /// never use it for access decisions, which go through the scope's <see cref="ITenantContext"/>.
    /// </summary>
    public static TenantId? Current => _context.Value is { IsResolved: true } context ? context.TenantId : null;

    /// <summary>Binds the unit of work's tenant context to this call path, before the tenant is known.</summary>
    internal static void Bind(ITenantContext context) => _context.Value = context;

    /// <summary>
    /// Binds a tenant that is already known, for background work that acts for one tenant across
    /// several short tenant scopes (each of which binds its own context inside this one).
    /// </summary>
    internal static void BindKnownTenant(TenantId tenant) => _context.Value = new KnownTenant(tenant);

    /// <summary>Called by <c>TenantContext.Set</c> once the tenant is known.</summary>
    internal static void OnResolved(ITenantContext context, TenantId tenant)
    {
        // Nothing bound further out (for example a test pipeline without the telemetry middleware):
        // bind here, which covers work on this call path from now on. A hand-made scope inside an
        // already bound one stays invisible to telemetry; create tenant scopes with TenantScopeRunner.
        _context.Value ??= context;

        // The current span (for a request, the request span) started before the tenant was known.
        // Only add the tag: a span that already names a tenant is not ours to relabel.
        if (Activity.Current is { } current && current.GetTagItem(TenantIdAttribute) is null)
        {
            current.SetTag(TenantIdAttribute, tenant.ToString());
        }
    }

    private sealed class KnownTenant(TenantId tenant) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => tenant;
    }
}
