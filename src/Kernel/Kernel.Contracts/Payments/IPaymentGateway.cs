using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Contracts.Payments;

/// <summary>
/// A payment provider, used through its hosted payment page (ADR 0004). Money goes directly to the
/// tenant's own merchant account; the platform never sees card data and never holds funds. Every
/// call names the tenant, and the adapter uses that tenant's credentials (from a secret store, never
/// the database).
/// </summary>
/// <remarks>
/// <para><b>The fulfilment rule.</b> Issue tickets only after <see cref="GetStatusAsync"/> reports
/// <see cref="PaymentStatus.Succeeded"/> <i>and</i> its <see cref="PaymentStatusResult.Amount"/>
/// equals the amount the order expected. Never issue on the buyer's return to the success URL (anyone
/// can open it) or on a webhook's contents (webhooks are hints, see <see cref="ParseWebhookAsync"/>).</para>
/// <para><b>Idempotency.</b> <see cref="PaymentSessionRequest.PaymentId"/> and
/// <see cref="RefundRequest.RefundId"/> are idempotency keys: repeating a call with the same key
/// (after a timeout, say) must not create a second session or a second refund.</para>
/// <para><b>Failures.</b> A definitive answer from the provider is a status. Not getting an answer
/// (network error, timeout, provider outage) throws <see cref="PaymentProviderUnavailableException"/>:
/// "unknown" must never be mistaken for "not paid".</para>
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>
    /// Starts a hosted payment page for one payment. Redirect the buyer to
    /// <see cref="PaymentSession.RedirectUrl"/>; keep <see cref="PaymentSession.ProviderSessionId"/>
    /// to check the status later.
    /// </summary>
    Task<PaymentSession> CreateSessionAsync(PaymentSessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider what happened to a payment. The authoritative answer: the only one that may
    /// trigger fulfilment, under the rule on <see cref="IPaymentGateway"/>.
    /// </summary>
    /// <exception cref="PaymentSessionNotFoundException">
    /// The tenant's merchant account has no such session, including another tenant's session.
    /// </exception>
    Task<PaymentStatusResult> GetStatusAsync(TenantId tenant, string providerSessionId, CancellationToken cancellationToken);

    /// <summary>Refunds all or part of a completed payment.</summary>
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a webhook's signature with the tenant's secret and extracts which payment it concerns.
    /// </summary>
    /// <remarks>
    /// A webhook is a hint to check the payment, never proof of payment: respond by calling
    /// <see cref="GetStatusAsync"/>. Providers retry and duplicate webhooks, so handling must be
    /// idempotent; <see cref="PaymentWebhook.EventId"/> identifies a delivery for deduplication.
    /// </remarks>
    /// <exception cref="PaymentWebhookRejectedException">Missing or invalid signature, or unreadable body.</exception>
    Task<PaymentWebhook> ParseWebhookAsync(TenantId tenant, PaymentWebhookRequest request, CancellationToken cancellationToken);
}

/// <param name="Tenant">Whose merchant account receives the money.</param>
/// <param name="PaymentId">The platform's id for this payment, and the idempotency key for creating it.</param>
/// <param name="Amount">What the buyer pays. Must be positive.</param>
/// <param name="Description">Shown to the buyer on the payment page.</param>
/// <param name="SuccessUrl">Where the buyer returns after paying. Not proof of payment.</param>
/// <param name="CancelUrl">Where the buyer returns after abandoning the payment.</param>
public sealed record PaymentSessionRequest(
    TenantId Tenant,
    Guid PaymentId,
    Money Amount,
    string Description,
    Uri SuccessUrl,
    Uri CancelUrl);

/// <param name="ProviderSessionId">The provider's id for the session; use it with <c>GetStatusAsync</c>.</param>
/// <param name="RedirectUrl">The hosted payment page to send the buyer to.</param>
/// <param name="ExpiresAt">When the provider stops accepting payment on this session, if it says.</param>
public sealed record PaymentSession(string ProviderSessionId, Uri RedirectUrl, DateTimeOffset? ExpiresAt);

public enum PaymentStatus
{
    /// <summary>Not finished: the buyer has not paid yet, or the provider is still processing.</summary>
    Pending = 1,

    /// <summary>Paid. Check the amount before fulfilling.</summary>
    Succeeded = 2,

    /// <summary>The payment was attempted and declined or errored. The buyer may try again.</summary>
    Failed = 3,

    /// <summary>The buyer abandoned or cancelled the payment page.</summary>
    Cancelled = 4,

    /// <summary>The session expired without payment.</summary>
    Expired = 5,
}

/// <param name="Status">The provider's definitive answer.</param>
/// <param name="Amount">The amount actually paid, when <see cref="PaymentStatus.Succeeded"/>.</param>
/// <param name="ProviderPaymentId">The provider's id for the completed payment, needed for refunds.</param>
public sealed record PaymentStatusResult(PaymentStatus Status, Money? Amount, string? ProviderPaymentId);

/// <param name="Tenant">Whose merchant account refunds the money.</param>
/// <param name="RefundId">The platform's id for this refund, and its idempotency key.</param>
/// <param name="ProviderPaymentId">The completed payment being refunded.</param>
/// <param name="Amount">How much to refund; less than the payment for a partial refund. Must be positive.</param>
public sealed record RefundRequest(TenantId Tenant, Guid RefundId, string ProviderPaymentId, Money Amount);

public enum RefundStatus
{
    /// <summary>Accepted; the provider completes it later. Check again before telling the buyer it is done.</summary>
    Pending = 1,

    Succeeded = 2,

    /// <summary>Rejected, for example because the amount exceeds what is left to refund.</summary>
    Failed = 3,
}

/// <param name="Status">The provider's answer.</param>
/// <param name="ProviderRefundId">The provider's id for the refund, when it created one.</param>
public sealed record RefundResult(RefundStatus Status, string? ProviderRefundId);

/// <summary>A webhook exactly as received: signatures are computed over the raw body bytes.</summary>
/// <param name="Headers">Request headers; look them up case-insensitively.</param>
/// <param name="Body">The raw request body, unparsed.</param>
public sealed record PaymentWebhookRequest(IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);

/// <param name="EventId">Identifies this delivery, for deduplicating retried webhooks.</param>
/// <param name="ProviderSessionId">The session to check with <c>GetStatusAsync</c>.</param>
public sealed record PaymentWebhook(string EventId, string ProviderSessionId);

/// <summary>The provider could not be reached or gave no usable answer. The outcome is unknown: retry later.</summary>
public sealed class PaymentProviderUnavailableException : Exception
{
    public PaymentProviderUnavailableException()
    {
    }

    public PaymentProviderUnavailableException(string message)
        : base(message)
    {
    }

    public PaymentProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The tenant's merchant account has no such payment session.</summary>
public sealed class PaymentSessionNotFoundException : Exception
{
    public PaymentSessionNotFoundException()
    {
    }

    public PaymentSessionNotFoundException(string message)
        : base(message)
    {
    }

    public PaymentSessionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A webhook failed verification. Answer it with an error and do nothing else.</summary>
public sealed class PaymentWebhookRejectedException : Exception
{
    public PaymentWebhookRejectedException()
    {
    }

    public PaymentWebhookRejectedException(string message)
        : base(message)
    {
    }

    public PaymentWebhookRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
