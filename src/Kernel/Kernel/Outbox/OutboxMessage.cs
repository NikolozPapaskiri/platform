using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Outbox;

/// <summary>
/// A domain event waiting to be dispatched, written in the same transaction as the state change that
/// raised it (transactional outbox, ADR 0002). Tenant-owned: each tenant's events are isolated like
/// any other tenant data, and the dispatcher processes them one tenant at a time (ADR 0003).
/// </summary>
/// <remarks>
/// M0 deliverable 5 adds only this minimal table, so the tenancy contract tests have a real
/// tenant-owned table. Deliverable 6 designs dispatching, including retry state and its indexes.
/// </remarks>
public sealed class OutboxMessage : ITenantOwned
{
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    public OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant on insert; never set by callers.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Event type name, used to route the message to its handlers.</summary>
    public string Type { get; private set; }

    /// <summary>The serialized event (JSON).</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }
}
