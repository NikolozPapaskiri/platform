namespace Platform.Kernel.Contracts.Events;

/// <summary>
/// Something that happened in the domain that other parts of the system may react to. Events are
/// immutable records, serialized to JSON in the outbox, so they carry ids and values, never entities.
/// </summary>
public interface IDomainEvent;
