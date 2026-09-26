using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Platform.Kernel;
using Platform.Kernel.Contracts.Modules;
using Platform.Kernel.Telemetry;
using Platform.Packs.Ticketing;

var builder = WebApplication.CreateBuilder(args);

// Every pack is listed here explicitly: adding one is a visible one-line change, not assembly
// scanning that silently picks up whatever is on disk.
IModule[] modules = [new TicketingModule()];

builder.AddPlatformTelemetry();
builder.Services.AddKernel(builder.Configuration);
foreach (var module in modules)
{
    module.Register(builder.Services, builder.Configuration);
}

var app = builder.Build();

// Must stay first: it binds the request's tenant context for telemetry before tenant resolution
// runs further in, and tags the HTTP metrics with tenant.id on the way out.
app.UseMiddleware<TenantTelemetryMiddleware>();

// Liveness: the process is up and can serve HTTP. It deliberately checks no dependencies: if the
// database is down, restarting this process fixes nothing, and a liveness failure would make an
// orchestrator restart it in a loop.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness: the dependencies this instance needs before it should receive traffic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthTags.Ready),
});

app.Run();
