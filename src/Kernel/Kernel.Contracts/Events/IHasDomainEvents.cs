namespace Platform.Kernel.Contracts.Events;

/// <summary>
/// An entity that records domain events as its state changes. When the entity is saved, the Kernel
/// persistence layer moves its events into the outbox in the same transaction as the state change,
/// so a change is never saved without its events, and an event never exists without its change.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Called by the persistence layer once the events are staged in the outbox.</summary>
    void ClearDomainEvents();
}
