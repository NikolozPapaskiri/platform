using System.Diagnostics;

namespace Platform.Kernel.Outbox;

/// <summary>Tracing for dispatch. The telemetry setup subscribes to <see cref="ActivitySourceName"/>.</summary>
public static class OutboxDiagnostics
{
    public const string ActivitySourceName = "Platform.Kernel.Outbox";

    private static readonly ActivitySource _source = new(ActivitySourceName);

    /// <summary>
    /// Starts the span for processing one message, as a child of the operation that raised the event
    /// (its stored trace context), so the request and its later side effects form one trace.
    /// </summary>
    public static Activity? StartProcessing(OutboxMessage message)
    {
        ActivityContext.TryParse(message.TraceParent, traceState: null, out var parent);

        var activity = _source.StartActivity($"outbox process {message.Type}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "outbox");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("event.type", message.Type);
        activity?.SetTag("tenant.id", message.TenantId.ToString());
        return activity;
    }
}
