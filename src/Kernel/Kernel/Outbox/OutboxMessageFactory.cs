using System.Diagnostics;
using Platform.Kernel.Contracts.Events;

namespace Platform.Kernel.Outbox;

/// <summary>Turns a domain event into the outbox row that is saved alongside the change that raised it.</summary>
public sealed class OutboxMessageFactory(DomainEventRegistry registry, TimeProvider timeProvider)
{
    public OutboxMessage Create(IDomainEvent domainEvent) => new(
        Guid.CreateVersion7(),
        registry.NameOf(domainEvent.GetType()),
        registry.Serialize(domainEvent),
        timeProvider.GetUtcNow(),
        Activity.Current?.Id);
}
