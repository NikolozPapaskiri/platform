using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Payments;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;

namespace Platform.Kernel.Tests.Outbox;

/// <summary>
/// The outbox stores events as JSON and reads them back in the dispatcher, so every Kernel value type
/// an event may carry must survive that round trip with the registry's own serializer settings.
/// </summary>
public sealed class DomainEventRegistryTests
{
    private const string EventName = "test.order-paid";

    private readonly DomainEventRegistry _registry = new([new DomainEventRegistration(typeof(OrderPaid), EventName)], []);

    [Fact]
    public void EventWithKernelValueTypes_RoundTrips()
    {
        var paid = new OrderPaid(Guid.NewGuid(), TenantId.New(), Money.Of(2500, "GEL"));

        var read = _registry.TryDeserialize(EventName, _registry.Serialize(paid));

        Assert.Equal(paid, read);
    }

    [Fact]
    public void PayloadWithAnInvalidValue_IsUnreadable_NotSilentlyDefaulted()
    {
        // TryDeserialize returning null parks the message for an operator; a default value would not.
        const string payload = """{"orderId":"0190a1b2-0000-7000-8000-000000000001","organizer":"00000000-0000-0000-0000-000000000000","total":{"amountMinor":2500,"currency":"GEL"}}""";

        Assert.Null(_registry.TryDeserialize(EventName, payload));
    }

    [Fact]
    public void EventWhoseConstructorRejectsThePayload_IsUnreadable_NotAnEscapingException()
    {
        // Any exception, not only JsonException: otherwise the dispatcher never parks the message and
        // it is claimed again every time its lease expires.
        var registry = new DomainEventRegistry([new DomainEventRegistration(typeof(Validated), "test.validated")], []);

        Assert.Null(registry.TryDeserialize("test.validated", """{"quantity":0}"""));
        Assert.Equal(new Validated(2), registry.TryDeserialize("test.validated", """{"quantity":2}"""));
    }

    private sealed record OrderPaid(Guid OrderId, TenantId Organizer, Money Total) : IDomainEvent;

    private sealed record Validated : IDomainEvent
    {
        public Validated(int quantity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
            Quantity = quantity;
        }

        public int Quantity { get; }
    }
}
