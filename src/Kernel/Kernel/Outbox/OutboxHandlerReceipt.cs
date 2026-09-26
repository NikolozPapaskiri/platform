using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Outbox;

/// <summary>
/// Records that one handler finished one message. Saved in the same transaction as the handler's own
/// database work, so on redelivery the dispatcher skips handlers that already succeeded, and a handler
/// that already succeeded cannot apply its database changes twice.
/// </summary>
public sealed class OutboxHandlerReceipt : ITenantOwned
{
    private OutboxHandlerReceipt()
    {
        Handler = string.Empty;
    }

    public OutboxHandlerReceipt(Guid messageId, string handler, DateTimeOffset processedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);

        MessageId = messageId;
        Handler = handler;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }

    /// <summary>The handler's full type name.</summary>
    public string Handler { get; private set; }

    public TenantId TenantId { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}
