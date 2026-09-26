using System.Collections.Concurrent;
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
public sealed class FakePaymentGateway : IPaymentGateway
{
    public const string SignatureHeader = "X-Fake-Signature";

    private readonly ConcurrentDictionary<(TenantId Tenant, Guid PaymentId), Session> _sessionsByPayment = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), Session> _sessions = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid RefundId), RefundResult> _refunds = new();
    private readonly Lock _gate = new();

    /// <summary>When true, every call fails as if the provider were unreachable.</summary>
    public bool Unavailable { get; set; }

    public Task<PaymentSession> CreateSessionAsync(PaymentSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfUnavailable();
        if (!request.Amount.IsPositive)
        {
            throw new ArgumentException("A payment must be for a positive amount.", nameof(request));
        }

        var session = _sessionsByPayment.GetOrAdd((request.Tenant, request.PaymentId), _ =>
        {
            var created = new Session("fake_sess_" + Guid.NewGuid().ToString("N"), request.Amount);
            _sessions[(request.Tenant, created.Id)] = created;
            return created;
        });

        return Task.FromResult(new PaymentSession(
            session.Id,
            new Uri($"https://pay.fake.test/{session.Id}"),
            DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    public Task<PaymentStatusResult> GetStatusAsync(TenantId tenant, string providerSessionId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var session = Find(tenant, providerSessionId);
        lock (_gate)
        {
            return Task.FromResult(new PaymentStatusResult(session.Status, session.PaidAmount, session.PaymentId));
        }
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfUnavailable();
        if (!request.Amount.IsPositive)
        {
            throw new ArgumentException("A refund must be for a positive amount.", nameof(request));
        }

        var result = _refunds.GetOrAdd((request.Tenant, request.RefundId), _ =>
        {
            lock (_gate)
            {
                var payment = _sessions.Values.SingleOrDefault(s =>
                    s.PaymentId == request.ProviderPaymentId &&
                    _sessions.ContainsKey((request.Tenant, s.Id)));
                if (payment?.PaidAmount is not { } paid ||
                    request.Amount.Currency != paid.Currency ||
                    payment.Refunded.AmountMinor + request.Amount.AmountMinor > paid.AmountMinor)
                {
                    return new RefundResult(RefundStatus.Failed, null);
                }

                payment.Refunded += request.Amount;
                return new RefundResult(RefundStatus.Succeeded, "fake_re_" + Guid.NewGuid().ToString("N"));
            }
        });

        return Task.FromResult(result);
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

    /// <summary>Test control: the buyer pays on the hosted page.</summary>
    public void CompletePayment(TenantId tenant, string sessionId, Money paid) =>
        Transition(tenant, sessionId, PaymentStatus.Succeeded, paid);

    /// <summary>Test control: the payment ends in any other state.</summary>
    public void SetStatus(TenantId tenant, string sessionId, PaymentStatus status) =>
        Transition(tenant, sessionId, status, paid: null);

    /// <summary>Test control: the webhook the provider would send about a session, signed for the tenant.</summary>
    public static PaymentWebhookRequest SignedWebhook(TenantId tenant, string sessionId, string? eventId = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new WebhookBody(eventId ?? "evt_" + Guid.NewGuid().ToString("N"), sessionId));
        return new PaymentWebhookRequest(
            new Dictionary<string, string> { [SignatureHeader] = Sign(tenant, body) },
            body);
    }

    private void Transition(TenantId tenant, string sessionId, PaymentStatus status, Money? paid)
    {
        var session = Find(tenant, sessionId);
        lock (_gate)
        {
            session.Status = status;
            session.PaidAmount = paid;
            session.PaymentId = paid is null ? null : "fake_pay_" + Guid.NewGuid().ToString("N");
            session.Refunded = Money.Zero(session.Requested.Currency);
        }
    }

    private Session Find(TenantId tenant, string sessionId) =>
        _sessions.TryGetValue((tenant, sessionId), out var session)
            ? session
            : throw new PaymentSessionNotFoundException($"No session {sessionId} for this merchant account.");

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

    private sealed class Session(string id, Money requested)
    {
        public string Id { get; } = id;

        public Money Requested { get; } = requested;

        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

        public Money? PaidAmount { get; set; }

        public string? PaymentId { get; set; }

        public Money Refunded { get; set; } = Money.Zero(requested.Currency);
    }

    private sealed record WebhookBody(string EventId, string SessionId);
}
