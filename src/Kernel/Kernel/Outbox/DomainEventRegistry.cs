using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Contracts.Events;

namespace Platform.Kernel.Outbox;

/// <summary>
/// Every declared event type (by stable name) and its handlers, built once at startup from the
/// packs' <see cref="DomainEventRegistration"/> and <see cref="DomainEventHandlerRegistration"/>.
/// Invalid declarations (duplicate names, a handler for an undeclared event) fail at startup.
/// </summary>
public sealed class DomainEventRegistry
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly MethodInfo _invokeMethod =
        typeof(DomainEventRegistry).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Dictionary<string, Type> _typesByName;
    private readonly Dictionary<Type, string> _namesByType;
    private readonly Dictionary<Type, IReadOnlyList<RegisteredHandler>> _handlersByType;

    public DomainEventRegistry(
        IEnumerable<DomainEventRegistration> events,
        IEnumerable<DomainEventHandlerRegistration> handlers)
    {
        _typesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
        _namesByType = [];
        foreach (var registration in events)
        {
            if (!_typesByName.TryAdd(registration.Name, registration.EventType) ||
                !_namesByType.TryAdd(registration.EventType, registration.Name))
            {
                throw new InvalidOperationException(
                    $"Domain event '{registration.Name}' ({registration.EventType}) is declared twice.");
            }
        }

        _handlersByType = handlers
            .GroupBy(handler => handler.EventType)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RegisteredHandler>)[.. group.Select(CreateHandler)]);
    }

    public string NameOf(Type eventType) =>
        _namesByType.TryGetValue(eventType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Domain event type {eventType} is not declared. Declare it with AddDomainEvent<T>(name).");

    public string Serialize(IDomainEvent domainEvent) =>
        JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), _jsonOptions);

    /// <summary>Returns null when the stored name is unknown or the payload does not deserialize.</summary>
    /// <remarks>
    /// Not only <see cref="JsonException"/>: an event's constructor or a value type may reject the
    /// stored values with any exception (<see cref="ArgumentException"/>, typically). Deserializing is
    /// deterministic, so a retry cannot succeed where this attempt failed; the caller parks the
    /// message instead of retrying it forever.
    /// </remarks>
    public IDomainEvent? TryDeserialize(string name, string payload)
    {
        if (!_typesByName.TryGetValue(name, out var type))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, type, _jsonOptions) as IDomainEvent;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public IReadOnlyList<RegisteredHandler> HandlersFor(Type eventType) =>
        _handlersByType.TryGetValue(eventType, out var handlers) ? handlers : [];

    private RegisteredHandler CreateHandler(DomainEventHandlerRegistration registration)
    {
        if (!_namesByType.ContainsKey(registration.EventType))
        {
            throw new InvalidOperationException(
                $"Handler {registration.HandlerType} handles {registration.EventType}, which is not declared.");
        }

        // Built once here, so dispatching needs no reflection per message.
        var invoke = _invokeMethod.MakeGenericMethod(registration.EventType, registration.HandlerType)
            .CreateDelegate<Func<IServiceProvider, IDomainEvent, DomainEventContext, CancellationToken, Task>>();

        return new RegisteredHandler(registration.HandlerType.FullName!, invoke);
    }

    private static Task InvokeAsync<TEvent, THandler>(
        IServiceProvider services,
        IDomainEvent domainEvent,
        DomainEventContext context,
        CancellationToken cancellationToken)
        where TEvent : IDomainEvent
        where THandler : class, IDomainEventHandler<TEvent> =>
        services.GetRequiredService<THandler>().HandleAsync((TEvent)domainEvent, context, cancellationToken);
}

/// <summary>A handler as the dispatcher sees it: a stable name for receipts, and how to invoke it.</summary>
public sealed record RegisteredHandler(
    string Name,
    Func<IServiceProvider, IDomainEvent, DomainEventContext, CancellationToken, Task> InvokeAsync);
