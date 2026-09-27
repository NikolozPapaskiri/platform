using Platform.Kernel.Contracts.Payments;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Payments;

/// <summary>
/// What every <see cref="IPaymentGateway"/> adapter must do, whoever the provider is. The fake runs
/// this suite today; the real adapter in M1 derives from it too and runs it against the provider's
/// sandbox, supplying the hooks below (and its own tenants and price, if the sandbox needs them).
/// </summary>
public abstract class PaymentGatewayContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A tenant with a merchant account. Sandbox adapters override it with a configured one.</summary>
    protected virtual TenantId Tenant { get; } = TenantId.New();

    /// <summary>A second tenant with its own, separate merchant account.</summary>
    protected virtual TenantId OtherTenant { get; } = TenantId.New();

    /// <summary>What the test payments cost. At least 2 minor units, so it can be split.</summary>
    protected virtual Money Price { get; } = Money.Of(2500, "GEL");

    protected abstract IPaymentGateway Gateway { get; }

    /// <summary>
    /// The buyer pays the session in full (sandbox automation of the hosted page, or the fake's
    /// control). A real hosted page charges the session's amount, so adapters may ignore
    /// <paramref name="amount"/>; the suite always passes <see cref="Price"/>.
    /// </summary>
    protected abstract Task CompletePaymentAsync(TenantId tenant, PaymentSession session, Money amount);

    /// <summary>A webhook about the session, correctly signed for the tenant.</summary>
    protected abstract PaymentWebhookRequest SignedWebhook(TenantId tenant, PaymentSession session);

    /// <summary>
    /// A correctly signed webhook about something other than a payment session (for Stripe, say, a
    /// <c>charge.refunded</c> event). Adapters get this case wrong most often.
    /// </summary>
    protected abstract PaymentWebhookRequest SignedWebhookNotAboutASession(TenantId tenant);

    /// <summary>
    /// The genuine webhook with its body changed so it is still well-formed: only the signature check
    /// may be what rejects it. The change depends on the provider's body format.
    /// </summary>
    protected abstract PaymentWebhookRequest Tampered(PaymentWebhookRequest genuine);

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
    public async Task CreatingASessionAgainWithOnePaymentIdButAnotherAmount_Throws()
    {
        // Silently returning the first session would charge the buyer the old amount.
        var paymentId = Guid.NewGuid();
        await CreateSessionAsync(paymentId);

        await Assert.ThrowsAsync<PaymentIdempotencyConflictException>(() =>
            CreateSessionAsync(paymentId, Price + Minor(1)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(null)]
    public async Task SessionForANonPositiveAmount_IsRejected(long? amountMinor)
    {
        var amount = amountMinor is { } minor ? Minor(minor) : default;

        await Assert.ThrowsAsync<ArgumentException>(() => CreateSessionAsync(Guid.NewGuid(), amount));
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
    public async Task RefundedPayment_StillReportsSucceeded()
    {
        // Succeeded is final ("was paid"). This is why fulfilment must be idempotent per payment: a
        // reconciliation run after a full refund still sees Succeeded and must not issue again.
        var (session, paymentId) = await PaidSessionAsync();
        Assert.True(IsAccepted((await Gateway.RefundAsync(Refund(paymentId, Price), Ct)).Status));

        var status = await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct);

        Assert.Equal(PaymentStatus.Succeeded, status.Status);
        Assert.Equal(Price, status.Amount);
    }

    [Fact]
    public async Task AnotherTenantsSession_IsNotFound()
    {
        // Each tenant has its own merchant account: one tenant can never see another's payments.
        var (session, _) = await PaidSessionAsync();

        await Assert.ThrowsAsync<PaymentSessionNotFoundException>(() =>
            Gateway.GetStatusAsync(OtherTenant, session.ProviderSessionId, Ct));
    }

    [Fact]
    public async Task RefundingTwiceWithOneRefundId_RefundsOnce()
    {
        var (_, paymentId) = await PaidSessionAsync();
        var half = Minor(Price.AmountMinor / 2);
        var refund = Refund(paymentId, half);

        var first = await Gateway.RefundAsync(refund, Ct);
        var second = await Gateway.RefundAsync(refund, Ct);

        Assert.True(IsAccepted(first.Status));
        Assert.Equal(first.ProviderRefundId, second.ProviderRefundId);

        // Had the retry refunded again, nothing would be left, and refunding the rest would fail.
        var rest = await Gateway.RefundAsync(Refund(paymentId, Price - half), Ct);
        Assert.True(IsAccepted(rest.Status));
    }

    [Fact]
    public async Task RefundingAgainWithOneRefundIdButAnotherAmount_Throws()
    {
        var (_, paymentId) = await PaidSessionAsync();
        var refundId = Guid.NewGuid();
        await Gateway.RefundAsync(new RefundRequest(Tenant, refundId, paymentId, Minor(1)), Ct);

        await Assert.ThrowsAsync<PaymentIdempotencyConflictException>(() =>
            Gateway.RefundAsync(new RefundRequest(Tenant, refundId, paymentId, Minor(2)), Ct));
    }

    [Fact]
    public async Task RefundingMoreThanIsLeft_Fails()
    {
        var (_, paymentId) = await PaidSessionAsync();
        await Gateway.RefundAsync(Refund(paymentId, Price - Minor(1)), Ct);

        var tooMuch = await Gateway.RefundAsync(Refund(paymentId, Minor(2)), Ct);

        Assert.Equal(RefundStatus.Failed, tooMuch.Status);
    }

    [Fact]
    public async Task RefundingAnotherTenantsPayment_Fails_AndRefundsNothing()
    {
        var (_, paymentId) = await PaidSessionAsync();

        var stolen = await Gateway.RefundAsync(new RefundRequest(OtherTenant, Guid.NewGuid(), paymentId, Price), Ct);

        Assert.Equal(RefundStatus.Failed, stolen.Status);
        // The owner can still refund the full amount, so nothing was taken from the payment.
        Assert.True(IsAccepted((await Gateway.RefundAsync(Refund(paymentId, Price), Ct)).Status));
    }

    [Fact]
    public async Task RefundInAnotherCurrency_Fails()
    {
        var (_, paymentId) = await PaidSessionAsync();
        var otherCurrency = Money.Of(1, Price.Currency == "EUR" ? "USD" : "EUR");

        var refund = await Gateway.RefundAsync(Refund(paymentId, otherCurrency), Ct);

        Assert.Equal(RefundStatus.Failed, refund.Status);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(null)]
    public async Task RefundOfANonPositiveAmount_IsRejected_BeforeTheProviderIsCalled(long? amountMinor)
    {
        // The payment does not exist: had the provider been called, the answer would be Failed, not
        // an exception. (It also saves paying a sandbox session for each case.)
        var amount = amountMinor is { } minor ? Minor(minor) : default;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Gateway.RefundAsync(Refund("no_such_payment_" + Guid.NewGuid().ToString("N"), amount), Ct));
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
    public async Task SignedWebhookNotAboutASession_HasNoSessionToCheck()
    {
        var webhook = await Gateway.ParseWebhookAsync(Tenant, SignedWebhookNotAboutASession(Tenant), Ct);

        Assert.Null(webhook.ProviderSessionId);
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

        await Assert.ThrowsAsync<PaymentWebhookRejectedException>(() =>
            Gateway.ParseWebhookAsync(Tenant, Tampered(SignedWebhook(Tenant, session)), Ct));
    }

    [Fact]
    public async Task UnsignedWebhook_IsRejected()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        var genuine = SignedWebhook(Tenant, session);

        await Assert.ThrowsAsync<PaymentWebhookRejectedException>(() =>
            Gateway.ParseWebhookAsync(Tenant, genuine with { Headers = new Dictionary<string, string>() }, Ct));
    }

    protected Task<PaymentSession> CreateSessionAsync(Guid paymentId, Money? amount = null) =>
        Gateway.CreateSessionAsync(
            new PaymentSessionRequest(
                Tenant,
                paymentId,
                amount ?? Price,
                "2 tickets",
                new Uri("https://tickets.organizer.test/checkout/done"),
                new Uri("https://tickets.organizer.test/checkout/cancelled")),
            Ct);

    private Money Minor(long amountMinor) => Money.Of(amountMinor, Price.Currency);

    private RefundRequest Refund(string providerPaymentId, Money amount) =>
        new(Tenant, Guid.NewGuid(), providerPaymentId, amount);

    // Sandboxes may accept a refund and finish it later; both mean the provider took it.
    private static bool IsAccepted(RefundStatus status) => status is RefundStatus.Succeeded or RefundStatus.Pending;

    private async Task<(PaymentSession Session, string ProviderPaymentId)> PaidSessionAsync()
    {
        var session = await CreateSessionAsync(Guid.NewGuid());
        await CompletePaymentAsync(Tenant, session, Price);
        var status = await Gateway.GetStatusAsync(Tenant, session.ProviderSessionId, Ct);
        return (session, status.ProviderPaymentId!);
    }
}
