namespace Platform.Kernel.Contracts.Events;

/// <summary>
/// Reacts to one type of domain event, asynchronously, after the change that raised it committed.
/// </summary>
/// <remarks>
/// Delivery is at least once. The dispatcher records a receipt when a handler succeeds and skips that
/// handler on redelivery. Only the Kernel's own database work commits in the same transaction as that
/// receipt today; how a pack's DbContext joins it is an open M1 decision. Until then, treat every
/// effect of a handler as possibly repeated, and use <see cref="DomainEventContext.MessageId"/> as
/// its idempotency key.
/// </remarks>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}
