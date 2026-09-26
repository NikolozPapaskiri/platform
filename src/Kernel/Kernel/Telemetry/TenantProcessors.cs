using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Platform.Kernel.Telemetry;

/// <summary>Tags every span started while a tenant is current with <c>tenant.id</c>.</summary>
internal sealed class TenantActivityProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity data)
    {
        if (TenantTelemetry.Current is { } tenant && data.GetTagItem(TenantTelemetry.TenantIdAttribute) is null)
        {
            data.SetTag(TenantTelemetry.TenantIdAttribute, tenant.ToString());
        }
    }
}

/// <summary>Adds <c>tenant.id</c> to every log record written while a tenant is current.</summary>
internal sealed class TenantLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (TenantTelemetry.Current is { } tenant &&
            data.Attributes?.Any(attribute => attribute.Key == TenantTelemetry.TenantIdAttribute) != true)
        {
            data.Attributes = [.. data.Attributes ?? [], new(TenantTelemetry.TenantIdAttribute, tenant.ToString())];
        }
    }
}
