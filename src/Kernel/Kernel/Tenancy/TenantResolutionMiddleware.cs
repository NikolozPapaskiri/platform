using Microsoft.AspNetCore.Http;

namespace Platform.Kernel.Tenancy;

/// <summary>
/// Resolves the tenant for each request from its host name, through the tenant catalog.
/// </summary>
/// <remarks>
/// HAND-WRITE: Nika. What it must do, the concepts involved, and the contract tests it has to pass
/// are in <c>docs/hand-write/m0-tenancy.md</c>. It is deliberately not registered in the request
/// pipeline yet, so the application keeps booting and serving health checks until it is written.
/// </remarks>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        // TODO(nika): implement per docs/hand-write/m0-tenancy.md, then register it in the pipeline.
        _ = _next;
        _ = context;
        throw new NotImplementedException("HAND-WRITE: Nika. See docs/hand-write/m0-tenancy.md.");
    }
}
