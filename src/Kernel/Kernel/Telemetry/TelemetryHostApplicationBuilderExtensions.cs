using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Platform.Kernel.Outbox;

namespace Platform.Kernel.Telemetry;

public static class TelemetryHostApplicationBuilderExtensions
{
    /// <summary>
    /// OpenTelemetry traces, metrics, and logs for the platform, with <c>tenant.id</c> on each once a
    /// tenant is resolved (ADR 0003). Exporters are chosen by configuration, never by code:
    /// <list type="bullet">
    /// <item><c>Telemetry:ConsoleExporter = true</c> prints traces and metrics (Development default);</item>
    /// <item>setting the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> sends everything over OTLP, for
    /// example to the Aspire dashboard, Jaeger, or Azure Monitor through a collector.</item>
    /// </list>
    /// With neither, telemetry is collected but not exported, at negligible cost.
    /// </summary>
    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var configuration = builder.Configuration;
        var serviceName = configuration["Telemetry:ServiceName"] ?? builder.Environment.ApplicationName;
        var console = configuration.GetValue<bool>("Telemetry:ConsoleExporter");
        var otlp = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // Logs already reach the console through the default console logger, so the console exporter is
        // only added for traces and metrics.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            logging.AddProcessor(new TenantLogProcessor());
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddProcessor(new TenantActivityProcessor());
                tracing.AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"));
                tracing.AddHttpClientInstrumentation();
                tracing.AddNpgsql();
                tracing.AddSource(OutboxDiagnostics.ActivitySourceName);
                if (console)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddNpgsqlInstrumentation();
                metrics.AddMeter(OutboxMetrics.MeterName);
                if (console)
                {
                    metrics.AddConsoleExporter();
                }
            });

        if (otlp)
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
