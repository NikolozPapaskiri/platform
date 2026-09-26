using Microsoft.Extensions.DependencyInjection;

namespace Platform.Kernel.Contracts.Events;

/// <summary>
/// How a pack declares its events and handlers, from <c>IModule.Register</c>. The Kernel reads these
/// registrations when it builds its event registry; packs never touch the Kernel implementation.
/// </summary>
public static class DomainEventServiceCollectionExtensions
{
    /// <summary>
    /// Declares an event type under a stable name. The name is what the outbox stores, so it must
    /// never change once events of that type exist; renaming the CLR type is fine.
    /// </summary>
    public static IServiceCollection AddDomainEvent<TEvent>(this IServiceCollection services, string name)
        where TEvent : IDomainEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddSingleton(new DomainEventRegistration(typeof(TEvent), name));
        return services;
    }

    /// <summary>
    /// Adds a handler for an event. Resolved in its own DI scope per delivery, running as the event's
    /// tenant. The handler's full type name identifies it in delivery receipts.
    /// </summary>
    public static IServiceCollection AddDomainEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IDomainEvent
        where THandler : class, IDomainEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new DomainEventHandlerRegistration(typeof(TEvent), typeof(THandler)));
        return services;
    }
}

/// <summary>An event type declared with <see cref="DomainEventServiceCollectionExtensions.AddDomainEvent{TEvent}"/>.</summary>
public sealed record DomainEventRegistration(Type EventType, string Name);

/// <summary>A handler declared with <see cref="DomainEventServiceCollectionExtensions.AddDomainEventHandler{TEvent, THandler}"/>.</summary>
public sealed record DomainEventHandlerRegistration(Type EventType, Type HandlerType);
