using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Contracts.Events;

/// <summary>Delivery details for one handler invocation.</summary>
/// <param name="MessageId">
/// Stable across redeliveries of the same event. Use it as the idempotency key for effects outside
/// the database.
/// </param>
/// <param name="TenantId">The tenant the event belongs to; the handler already runs as this tenant.</param>
/// <param name="OccurredAt">When the change that raised the event was saved.</param>
/// <param name="Attempt">1 on first delivery, higher on retries.</param>
public sealed record DomainEventContext(Guid MessageId, TenantId TenantId, DateTimeOffset OccurredAt, int Attempt);
