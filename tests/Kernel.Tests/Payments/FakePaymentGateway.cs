using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Kernel.Contracts.Payments;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Payments;

/// <summary>
/// An in-memory payment provider for tests only (there is no real adapter in M0). It behaves the way
/// <see cref="IPaymentGateway"/> requires: per-tenant merchant accounts, idempotent session creation
/// and refunds, signed webhooks, and an outage switch.
/// </summary>
/// <remarks>
/// All state sits behind one lock, so concurrent retries with one idempotency key create exactly one
/// session or refund (a ConcurrentDictionary's GetOrAdd can run its factory twice).
/// </remarks>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public const string SignatureHeader = "X-Fake-Signature";

    private readonly Dictionary<(TenantId Tenant, Guid PaymentId), Session> _sessionsByPayment = [];
    private readonly Dictionary<(TenantId Tenant, string SessionId), Session> _sessions = [];
    private readonly Dictionary<(TenantId Tenant, Guid RefundId), Refund> _refunds = [];
    private readonly Lock _gate = new();

    /// <summary>When true, every call fails as if the provider were unreachable.</summary>
    public bool Unavailable { get; set; }

    public Task<PaymentSession> CreateSessionAsync(PaymentSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(request.Amount, nameof(request));
        ThrowIfUnavailable();

        Session session;
        lock (_gate)
        {
            if (_sessionsByPayment.TryGetValue((request.Tenant, request.PaymentId), out var existing))
            {
                // Like Stripe: any difference in the request, not only the amount, is a conflict.
                session = existing.Request == request
                    ? existing
                    : throw new PaymentIdempotencyConflictException(
                        $"Payment {request.PaymentId} was created with different details.");
            }
            else
            {
                session = new Session("fake_sess_" + Guid.NewGuid().ToString("N"), request);
                _sessionsByPayment[(request.Tenant, request.PaymentId)] = session;
                _sessions[(request.Tenant, session.Id)] = session;
            }
        }

        return Task.FromResult(new PaymentSession(
            session.Id,
            new Uri($"https://pay.fake.test/{session.Id}"),
            DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    public Task<PaymentStatusResult> GetStatusAsync(TenantId tenant, string providerSessionId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        lock (_gate)
        {
            var session = Find(tenant, providerSessionId);
            return Task.FromResult(new PaymentStatusResult(session.Status, session.PaidAmount, session.ProviderPaymentId));
        }
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePositive(request.Amount, nameof(request));
        ThrowIfUnavailable();

        lock (_gate)
        {
            if (_refunds.TryGetValue((request.Tenant, request.RefundId), out var existing))
            {
                return existing.ProviderPaymentId == request.ProviderPaymentId && existing.Amount == request.Amount
                    ? Task.FromResult(existing.Result)
                    : throw new PaymentIdempotencyConflictException(
                        $"Refund {request.RefundId} was requested with different details.");
            }

            // Only this tenant's merchant account is searched: another tenant's payment does not exist here.
            var payment = _sessions
                .Where(entry => entry.Key.Tenant == request.Tenant && entry.Value.ProviderPaymentId == request.ProviderPaymentId)
                .Select(entry => entry.Value)
                .SingleOrDefault();

            RefundResult result;
            if (payment?.PaidAmount is not { } paid ||
                request.Amount.Currency != paid.Currency ||
                request.Amount.AmountMinor > paid.AmountMinor - payment.Refunded.AmountMinor)
            {
                result = new RefundResult(RefundStatus.Failed, null);
            }
            else
            {
                payment.Refunded += request.Amount;
                result = new RefundResult(RefundStatus.Succeeded, "fake_re_" + Guid.NewGuid().ToString("N"));
            }

            // The key keeps its first answer, a declined refund included. (Real providers differ on
            // whether they store answers to invalid requests; the contract does not depend on it.)
            _refunds[(request.Tenant, request.RefundId)] = new Refund(request.ProviderPaymentId, request.Amount, result);
            return Task.FromResult(result);
        }
    }

    public Task<PaymentWebhook> ParseWebhookAsync(TenantId tenant, PaymentWebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var signature = request.Headers
            .FirstOrDefault(header => string.Equals(header.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (signature is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(tenant, request.Body.Span))))
        {
            throw new PaymentWebhookRejectedException("Missing or invalid webhook signature.");
        }

        try
        {
            var body = JsonSerializer.Deserialize<WebhookBody>(request.Body.Span)
                ?? throw new PaymentWebhookRejectedException("Empty webhook body.");
            return Task.FromResult(new PaymentWebhook(body.EventId, body.SessionId));
        }
        catch (JsonException ex)
        {
            throw new PaymentWebhookRejectedException("Unreadable webhook body.", ex);
        }
    }

    /// <summary>Test control: the buyer pays on the hosted page (possibly a different amount).</summary>
    public void CompletePayment(TenantId tenant, string sessionId, Money paid)
    {
        lock (_gate)
        {
            var session = Pending(tenant, sessionId);
            if (paid.Currency != session.Request.Amount.Currency)
            {
                throw new ArgumentException("A hosted page charges in the session's currency.", nameof(paid));
            }

            session.Status = PaymentStatus.Succeeded;
            session.PaidAmount = paid;
            session.ProviderPaymentId = "fake_pay_" + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>Test control: the payment ends unpaid (Failed, Cancelled, or Expired).</summary>
    public void SetStatus(TenantId tenant, string sessionId, PaymentStatus status)
    {
        if (status is PaymentStatus.Succeeded or PaymentStatus.Pending)
        {
            throw new ArgumentException("Use CompletePayment to pay; a session cannot go back to Pending.", nameof(status));
        }

        lock (_gate)
        {
            Pending(tenant, sessionId).Status = status;
        }
    }

    /// <summary>
    /// Test control: the webhook the provider would send, signed for the tenant. A null session id is
    /// an event that is not about a payment session.
    /// </summary>
    public static PaymentWebhookRequest SignedWebhook(TenantId tenant, string? sessionId, string? eventId = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new WebhookBody(eventId ?? "evt_" + Guid.NewGuid().ToString("N"), sessionId));
        return new PaymentWebhookRequest(
            new Dictionary<string, string> { [SignatureHeader] = Sign(tenant, body) },
            body);
    }

    // Only a pending session can end, and it ends once: Succeeded in particular is final, as on
    // IPaymentGateway, so tests cannot model a provider that "un-pays". Callers hold _gate.
    private Session Pending(TenantId tenant, string sessionId)
    {
        var session = Find(tenant, sessionId);
        return session.Status == PaymentStatus.Pending
            ? session
            : throw new InvalidOperationException($"Session {sessionId} already ended as {session.Status}.");
    }

    // Callers hold _gate.
    private Session Find(TenantId tenant, string sessionId) =>
        _sessions.TryGetValue((tenant, sessionId), out var session)
            ? session
            : throw new PaymentSessionNotFoundException($"No session {sessionId} for this merchant account.");

    private static void EnsurePositive(Money amount, string paramName)
    {
        // default(Money) has amount 0, so it fails here too.
        if (!amount.IsPositive)
        {
            throw new ArgumentException($"The amount must be positive, not {amount}.", paramName);
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new PaymentProviderUnavailableException("The fake provider is switched to unavailable.");
        }
    }

    // Each tenant's merchant account has its own webhook secret; here it is derived from the tenant id.
    private static string Sign(TenantId tenant, ReadOnlySpan<byte> body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec_" + tenant), body));

    private sealed class Session(string id, PaymentSessionRequest request)
    {
        public string Id { get; } = id;

        public PaymentSessionRequest Request { get; } = request;

        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

        public Money? PaidAmount { get; set; }

        public string? ProviderPaymentId { get; set; }

        public Money Refunded { get; set; } = Money.Zero(request.Amount.Currency);
    }

    private sealed record Refund(string ProviderPaymentId, Money Amount, RefundResult Result);

    private sealed record WebhookBody(string EventId, string? SessionId);
}
