namespace Platform.Kernel.Outbox;

/// <summary>Dispatcher tuning, bound from the <c>Outbox</c> configuration section.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Pause between dispatch passes over all tenants.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Most messages processed per tenant per pass, so one busy tenant cannot starve the others.
    /// Messages are claimed one at a time, each just before it is processed.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long one claim lasts. Must exceed the time to run all handlers of one message; if it does
    /// not, another dispatcher may run the message too (safe, because of receipts and lease tokens,
    /// but effects outside the database can then repeat).
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Attempts before a message is parked (failed) for an operator.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>First retry delay; doubles per attempt (with jitter) up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}
