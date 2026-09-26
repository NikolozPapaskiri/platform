using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Outbox;

/// <summary>
/// A domain event waiting to be dispatched, written in the same transaction as the state change that
/// raised it (transactional outbox, ADR 0002). Tenant-owned: each tenant's events are isolated like
/// any other tenant data, and the dispatcher processes them one tenant at a time (ADR 0003).
/// </summary>
/// <remarks>
/// Lifecycle: pending, then either processed, or retried with backoff after a handler failure until
/// it succeeds or is parked (failed) after too many attempts. A message whose type or payload cannot
/// be read is parked at once, since retrying cannot fix it. The dispatcher changes this state only
/// with conditional updates guarded by its lease token (see <see cref="OutboxProcessor"/>); the entity
/// itself is written once, when the event is raised.
/// </remarks>
public sealed class OutboxMessage : ITenantOwned
{
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    public OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt, string? traceParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        Id = id;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
        TraceParent = traceParent;
    }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant on insert; never set by callers.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>The event's stable name (see <c>AddDomainEvent</c>), used to deserialize and route it.</summary>
    public string Type { get; private set; }

    /// <summary>The serialized event (JSON).</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    /// W3C trace context of the operation that raised the event, so its dispatch appears in the same
    /// distributed trace even though it runs later, on a background thread.
    /// </summary>
    public string? TraceParent { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Not dispatched before this time (retry backoff). Null means as soon as possible.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// Claimed by a dispatcher until this time. A dispatcher that crashes simply lets the lease expire,
    /// and another one picks the message up.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Identifies the current claim. Every state change after claiming requires this token, so a
    /// dispatcher whose lease expired (and was taken over) can no longer change the message.
    /// </summary>
    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Parked: no further attempts. Needs an operator (or a fix and a requeue).</summary>
    public DateTimeOffset? FailedAt { get; private set; }

    public string? LastError { get; private set; }
}
