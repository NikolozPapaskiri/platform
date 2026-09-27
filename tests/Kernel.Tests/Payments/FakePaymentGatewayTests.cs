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

    protected override PaymentWebhookRequest SignedWebhookNotAboutASession(TenantId tenant) =>
        FakePaymentGateway.SignedWebhook(tenant, sessionId: null);

    protected override PaymentWebhookRequest Tampered(PaymentWebhookRequest genuine)
    {
        // The fake's body ends with the session id, then '"}'. Changing a character of the id keeps
        // the JSON well-formed.
        var body = genuine.Body.ToArray();
        body[^3] ^= 0x01;
        return genuine with { Body = body };
    }

    [Fact]
    public async Task Outage_ThrowsUnavailable_NeverAStatus()
    {
        // "Could not ask" must never look like "not paid".
        var session = await CreateSessionAsync(Guid.NewGuid());
        _gateway.Unavailable = true;

        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(() =>
            Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct));
    }

    [Fact]
    public async Task SucceededIsFinal_TheFakeRefusesToEndASessionTwice()
    {
        // Keeps tests from modelling a provider that "un-pays" a payment.
        var session = await CreateSessionAsync(Guid.NewGuid());
        _gateway.CompletePayment(Tenant, session.ProviderSessionId, Price);

        Assert.Throws<InvalidOperationException>(() => _gateway.SetStatus(Tenant, session.ProviderSessionId, PaymentStatus.Failed));
        Assert.Throws<InvalidOperationException>(() => _gateway.CompletePayment(Tenant, session.ProviderSessionId, Price));
    }

    [Fact]
    public async Task ReusingAPaymentIdWithAnotherDescription_IsAConflictToo()
    {
        var request = new PaymentSessionRequest(
            Tenant, Guid.NewGuid(), Price, "2 tickets", new Uri("https://t.test/done"), new Uri("https://t.test/cancel"));
        await Gateway.CreateSessionAsync(request, Ct);

        await Assert.ThrowsAsync<PaymentIdempotencyConflictException>(() =>
            Gateway.CreateSessionAsync(request with { Description = "3 tickets" }, Ct));
    }

    [Fact]
    public async Task HugeRefund_IsDeclined_NotAnOverflow()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        _gateway.CompletePayment(Tenant, session.ProviderSessionId, Price);
        var paymentId = (await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct)).ProviderPaymentId!;
        await Gateway.RefundAsync(new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(1, Price.Currency)), Ct);

        var huge = await Gateway.RefundAsync(new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(long.MaxValue, Price.Currency)), Ct);

        Assert.Equal(RefundStatus.Failed, huge.Status);
    }

    [Fact]
    public async Task ConcurrentRetriesWithOnePaymentId_CreateOneSession()
    {
        var paymentId = Guid.NewGuid();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => CreateSessionAsync(paymentId))));

        Assert.Single(sessions.Select(session => session.ProviderSessionId).Distinct());
    }
}
