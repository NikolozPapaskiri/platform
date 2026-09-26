using Platform.Kernel.Contracts.Payments;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Payments;

/// <summary>The fake passes the adapter contract, so tests that use it test against realistic behaviour.</summary>
public sealed class FakePaymentGatewayTests : PaymentGatewayContractTests
{
    private readonly FakePaymentGateway _gateway = new();

    protected override IPaymentGateway Gateway => _gateway;

    protected override Task CompletePaymentAsync(TenantId tenant, PaymentSession session, Money amount)
    {
        _gateway.CompletePayment(tenant, session.ProviderSessionId, amount);
        return Task.CompletedTask;
    }

    protected override PaymentWebhookRequest SignedWebhook(TenantId tenant, PaymentSession session) =>
        FakePaymentGateway.SignedWebhook(tenant, session.ProviderSessionId);

    [Fact]
    public async Task Outage_ThrowsUnavailable_NeverAStatus()
    {
        // "Could not ask" must never look like "not paid".
        var session = await CreateSessionAsync(Guid.NewGuid());
        _gateway.Unavailable = true;

        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(() =>
            Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct));
    }
}
