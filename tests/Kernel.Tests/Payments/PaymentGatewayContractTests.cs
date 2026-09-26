using Platform.Kernel.Contracts.Payments;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Payments;

/// <summary>
/// What every <see cref="IPaymentGateway"/> adapter must do, whoever the provider is. The fake runs
/// this suite today; the real adapter in M1 derives from it too and runs it against the provider's
/// sandbox, supplying the three hooks below.
/// </summary>
public abstract class PaymentGatewayContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected TenantId Tenant { get; } = TenantId.New();

    protected TenantId OtherTenant { get; } = TenantId.New();

    protected Money Price { get; } = Money.Of(2500, "GEL");

    protected abstract IPaymentGateway Gateway { get; }

    /// <summary>The buyer pays the session in full (sandbox automation, or the fake's control).</summary>
    protected abstract Task CompletePaymentAsync(TenantId tenant, PaymentSession session, Money amount);

    /// <summary>A webhook about the session, correctly signed for the tenant.</summary>
    protected abstract PaymentWebhookRequest SignedWebhook(TenantId tenant, PaymentSession session);

    [Fact]
    public async Task NewSession_RedirectsToTheHostedPage_AndIsPending()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());

        Assert.Equal(Uri.UriSchemeHttps, session.RedirectUrl.Scheme);
        var status = await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct);
        Assert.Equal(PaymentStatus.Pending, status.Status);
    }

    [Fact]
    public async Task CreatingASessionTwiceWithOnePaymentId_ReturnsTheSameSession()
    {
        // A retried request after a timeout must not open a second payment.
        var paymentId = Guid.NewGuid();

        var first = await CreateSessionAsync(paymentId);
        var second = await CreateSessionAsync(paymentId);

        Assert.Equal(first.ProviderSessionId, second.ProviderSessionId);
    }

    [Fact]
    public async Task PaidSession_ReportsSucceeded_WithTheAmountPaid()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());

        await CompletePaymentAsync(Tenant, session, Price);
        var status = await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct);

        Assert.Equal(PaymentStatus.Succeeded, status.Status);
        Assert.Equal(Price, status.Amount);
        Assert.False(string.IsNullOrEmpty(status.ProviderPaymentId));
    }

    [Fact]
    public async Task AnotherTenantsSession_IsNotFound()
    {
        // Each tenant has its own merchant account: one tenant can never see another's payments.
        var session = await CreateSessionAsync(Guid.NewGuid());
        await CompletePaymentAsync(Tenant, session, Price);

        await Assert.ThrowsAsync<PaymentSessionNotFoundException>(() =>
            Gateway.GetStatusAsync(OtherTenant, session.ProviderSessionId, Ct));
    }

    [Fact]
    public async Task RefundingTwiceWithOneRefundId_RefundsOnce()
    {
        var paymentId = await PaidPaymentIdAsync();
        var refund = new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(1000, "GEL"));

        var first = await Gateway.RefundAsync(refund, Ct);
        var second = await Gateway.RefundAsync(refund, Ct);

        Assert.Equal(RefundStatus.Succeeded, first.Status);
        Assert.Equal(first.ProviderRefundId, second.ProviderRefundId);

        // Had the retry refunded again, only 500 would be left, and this 1500 refund would fail.
        var rest = await Gateway.RefundAsync(new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(1500, "GEL")), Ct);
        Assert.Equal(RefundStatus.Succeeded, rest.Status);
    }

    [Fact]
    public async Task RefundingMoreThanIsLeft_Fails()
    {
        var paymentId = await PaidPaymentIdAsync();
        await Gateway.RefundAsync(new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(2000, "GEL")), Ct);

        var tooMuch = await Gateway.RefundAsync(new RefundRequest(Tenant, Guid.NewGuid(), paymentId, Money.Of(501, "GEL")), Ct);

        Assert.Equal(RefundStatus.Failed, tooMuch.Status);
    }

    [Fact]
    public async Task SignedWebhook_NamesTheSessionToCheck()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());

        var webhook = await Gateway.ParseWebhookAsync(Tenant, SignedWebhook(Tenant, session), Ct);

        Assert.Equal(session.ProviderSessionId, webhook.ProviderSessionId);
        Assert.False(string.IsNullOrEmpty(webhook.EventId));
    }

    [Fact]
    public async Task WebhookSignedForAnotherTenant_IsRejected()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());

        await Assert.ThrowsAsync<PaymentWebhookRejectedException>(() =>
            Gateway.ParseWebhookAsync(OtherTenant, SignedWebhook(Tenant, session), Ct));
    }

    [Fact]
    public async Task WebhookWithATamperedBody_IsRejected()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        var genuine = SignedWebhook(Tenant, session);
        var body = genuine.Body.ToArray();
        // Change one character inside a JSON string value: still well-formed, so only the signature
        // check can catch it. (The fake's body ends with the session id, then '"}'.)
        body[^3] ^= 0x01;

        await Assert.ThrowsAsync<PaymentWebhookRejectedException>(() =>
            Gateway.ParseWebhookAsync(Tenant, genuine with { Body = body }, Ct));
    }

    [Fact]
    public async Task UnsignedWebhook_IsRejected()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        var genuine = SignedWebhook(Tenant, session);

        await Assert.ThrowsAsync<PaymentWebhookRejectedException>(() =>
            Gateway.ParseWebhookAsync(Tenant, genuine with { Headers = new Dictionary<string, string>() }, Ct));
    }

    protected Task<PaymentSession> CreateSessionAsync(Guid paymentId) =>
        Gateway.CreateSessionAsync(
            new PaymentSessionRequest(
                Tenant,
                paymentId,
                Price,
                "2 tickets",
                new Uri("https://tickets.organizer.test/checkout/done"),
                new Uri("https://tickets.organizer.test/checkout/cancelled")),
            Ct);

    private async Task<string> PaidPaymentIdAsync()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        await CompletePaymentAsync(Tenant, session, Price);
        return (await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct)).ProviderPaymentId!;
    }
}
