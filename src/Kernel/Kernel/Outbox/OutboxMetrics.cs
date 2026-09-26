using System.Diagnostics;
using System.Diagnostics.Metrics;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Outbox;

/// <summary>
/// Outbox counters. A rising parked count, or failures without matching successes, is what an alert
/// should watch.
/// </summary>
/// <remarks>
/// Tagged with <c>tenant.id</c> (ADR 0003). That makes one series per tenant and event type: fine at
/// pilot scale, worth revisiting with thousands of tenants.
/// </remarks>
public sealed class OutboxMetrics
{
    public const string MeterName = "Platform.Kernel.Outbox";

    private readonly Counter<long> _processed;
    private readonly Counter<long> _handlerFailures;
    private readonly Counter<long> _parked;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _processed = meter.CreateCounter<long>(
            "platform.outbox.messages.processed", unit: "{message}", description: "Messages whose handlers all succeeded.");
        _handlerFailures = meter.CreateCounter<long>(
            "platform.outbox.handler.failures", unit: "{failure}", description: "Handler runs that threw.");
        _parked = meter.CreateCounter<long>(
            "platform.outbox.messages.parked", unit: "{message}", description: "Messages given up on; they need an operator.");
    }

    public void Processed(string eventType, TenantId tenant) => _processed.Add(1, Tags(eventType, tenant));

    public void HandlerFailed(string eventType, TenantId tenant) => _handlerFailures.Add(1, Tags(eventType, tenant));

    public void Parked(string eventType, TenantId tenant) => _parked.Add(1, Tags(eventType, tenant));

    private static TagList Tags(string eventType, TenantId tenant) =>
        new() { { "event.type", eventType }, { "tenant.id", tenant.ToString() } };
}
