using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Tenancy;

namespace Platform.Kernel.Telemetry;

/// <summary>
/// Must be the first middleware, outside tenant resolution and any exception handling. On the way in
/// it binds the request's tenant context for telemetry (see <see cref="TenantTelemetry"/>), so every
/// span and log after resolution carries <c>tenant.id</c> however resolution sets it. On the way out
/// it tags ASP.NET Core's HTTP server metrics, which are recorded after the whole pipeline returns.
/// </summary>
/// <remarks>
/// Metric cardinality: tenant.id multiplies every route and status series by the number of tenants.
/// The OpenTelemetry SDK caps each metric at 2000 series by default and drops the excess silently.
/// Fine at pilot scale; revisit (or raise the cap) well before thousands of tenants.
/// </remarks>
public sealed class TenantTelemetryMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<TenantContext>() is { } tenantContext)
        {
            TenantTelemetry.Bind(tenantContext);
        }

        try
        {
            await _next(context);
        }
        finally
        {
            if (context.RequestServices.GetService<ITenantContext>() is { IsResolved: true } tenant &&
                context.Features.Get<IHttpMetricsTagsFeature>() is { } metricTags &&
                !metricTags.Tags.Any(tag => tag.Key == TenantTelemetry.TenantIdAttribute))
            {
                metricTags.Tags.Add(new(TenantTelemetry.TenantIdAttribute, tenant.TenantId.ToString()));
            }
        }
    }
}
