using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Telemetry;
using Platform.Kernel.Tenancy;

namespace Platform.Kernel.Tests.Telemetry;

/// <summary>ADR 0003: tenant.id on every span, log, and metric once a tenant is resolved.</summary>
public sealed class TenantTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spans_StartedWhileRunningAsATenant_CarryTheTenantId()
    {
        using var source = new ActivitySource(NewSourceName());
        var exported = new List<Activity>();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddProcessor(new TenantActivityProcessor())
            .AddInMemoryExporter(exported)
            .Build();
        await using var services = TenancyServices();
        var tenant = TenantId.New();

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(tenant, _ =>
        {
            using var inside = source.StartActivity("inside");
            return Task.CompletedTask;
        });
        using (source.StartActivity("after"))
        {
        }

        tracing.ForceFlush();
        Assert.Equal(tenant.ToString(), exported.Single(a => a.DisplayName == "inside").GetTagItem("tenant.id"));
        Assert.Null(exported.Single(a => a.DisplayName == "after").GetTagItem("tenant.id"));
    }

    [Fact]
    public async Task Logs_WrittenWhileRunningAsATenant_CarryTheTenantId()
    {
        var exported = new List<LogRecord>();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new TenantLogProcessor());
            options.AddInMemoryExporter(exported);
        }));
        var logger = loggerFactory.CreateLogger("test");
        await using var services = TenancyServices();
        var tenant = TenantId.New();

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(tenant, _ =>
        {
            logger.LogInformation("inside");
            return Task.CompletedTask;
        });
        logger.LogInformation("after");

        Assert.Contains(
            exported.Single(r => r.Body == "inside").Attributes!,
            attribute => attribute.Key == "tenant.id" && Equals(attribute.Value, tenant.ToString()));
        Assert.DoesNotContain(
            exported.Single(r => r.Body == "after").Attributes ?? [],
            attribute => attribute.Key == "tenant.id");
    }

    [Fact]
    public async Task HttpServerMetrics_CarryTheTenantId_OnceTheTenantIsResolved()
    {
        var tenant = TenantId.New();
        var tagged = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument is { Meter.Name: "Microsoft.AspNetCore.Hosting", Name: "http.server.request.duration" })
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "tenant.id" && tag.Value is string value)
                {
                    tagged.Add(value);
                }
            }
        });
        listener.Start();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        await using var app = builder.Build();
        app.UseMiddleware<TenantTelemetryMiddleware>();
        app.Use((context, next) =>
        {
            // Stands in for tenant resolution, which runs further in than the metrics middleware.
            context.RequestServices.GetRequiredService<TenantContext>().Set(tenant);
            return next(context);
        });
        app.Run(context => context.Response.WriteAsync("ok"));
        await app.StartAsync(Ct);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        await app.StopAsync(Ct);

        Assert.Contains(tenant.ToString(), tagged);
    }

    [Fact]
    public async Task Request_ResolvedInsideANestedHelper_StillTagsLaterSpansAndLogs()
    {
        // Tenant resolution may set the tenant deep inside awaited helpers. Everything the request
        // does afterwards must still carry tenant.id.
        var tenant = TenantId.New();
        using var source = new ActivitySource(NewSourceName());
        var spans = new List<Activity>();
        using var tracing = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            // The parent is ASP.NET Core's request activity, which nothing records in this bare test
            // host; the default parent-based sampler would drop its children.
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new TenantActivityProcessor())
            .AddInMemoryExporter(spans)
            .Build();
        var logs = new List<LogRecord>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new TenantLogProcessor());
            options.AddInMemoryExporter(logs);
        });
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        await using var app = builder.Build();
        app.UseMiddleware<TenantTelemetryMiddleware>();
        app.Use(async (context, next) =>
        {
            await ResolveInNestedHelperAsync(context, tenant);
            await next(context);
        });
        app.Run(context =>
        {
            using var work = source.StartActivity("endpoint work");
            context.RequestServices.GetRequiredService<ILogger<TenantTelemetryTests>>().LogInformation("endpoint log");
            return context.Response.WriteAsync("ok");
        });
        await app.StartAsync(Ct);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        await app.StopAsync(Ct);
        tracing.ForceFlush();

        Assert.Equal(tenant.ToString(), spans.Single(a => a.DisplayName == "endpoint work").GetTagItem("tenant.id"));
        Assert.Contains(
            logs.Single(r => r.Body == "endpoint log").Attributes!,
            attribute => attribute.Key == "tenant.id" && Equals(attribute.Value, tenant.ToString()));
    }

    private static async Task ResolveInNestedHelperAsync(HttpContext context, TenantId tenant)
    {
        await Task.Yield();
        context.RequestServices.GetRequiredService<TenantContext>().Set(tenant);
    }

    private static ServiceProvider TenancyServices() =>
        new ServiceCollection()
            .AddScoped<TenantContext>()
            .AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>())
            .AddSingleton<TenantScopeRunner>()
            .BuildServiceProvider();

    private static string NewSourceName() => "Platform.Tests." + Guid.NewGuid().ToString("N");
}
