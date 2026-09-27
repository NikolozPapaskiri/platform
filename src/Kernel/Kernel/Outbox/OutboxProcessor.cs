using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Persistence;
using Platform.Kernel.Telemetry;
using Platform.Kernel.Tenancy;
using Platform.Kernel.Tenancy.Catalog;

namespace Platform.Kernel.Outbox;

/// <summary>
/// One dispatch pass: for each active tenant, running as that tenant, claim its due messages one at a
/// time and run their handlers. Safe to run on several replicas at once.
/// </summary>
/// <remarks>
/// <para><b>Claiming.</b> A message is claimed just before it is processed, by one atomic statement
/// that sets a lease (<c>locked_until</c>) and a fresh lease token, skipping rows another dispatcher
/// is claiming at that moment. No database lock is held while handlers run. A dispatcher that dies
/// mid-message lets the lease expire, and the message is claimed again.</para>
/// <para><b>Ownership.</b> Every later change (processed, failed, parked) is a conditional update
/// that only applies while this claim's token is still on the row and the message is still pending.
/// A dispatcher whose lease expired and was taken over therefore changes nothing.</para>
/// <para><b>Idempotency.</b> Each handler runs in its own DI scope and transaction, together with its
/// receipt. On redelivery, handlers with a receipt are skipped; if one handler fails, the ones before
/// it stay done. Database work the handler does through that scope's <see cref="PlatformDbContext"/>
/// commits with the receipt. How pack DbContexts join that transaction is an open M1 decision; until
/// then, pack handlers must treat all their effects as at-least-once, keyed on the message id.</para>
/// <para><b>Suspended tenants</b> are not dispatched; their messages wait until reactivation.</para>
/// </remarks>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    TenantScopeRunner runner,
    DomainEventRegistry registry,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    OutboxMetrics metrics,
    ILogger<OutboxProcessor> logger)
{
    private OutboxOptions Options => options.Value;

    /// <summary>Runs one pass over every active tenant. Returns the number of messages claimed.</summary>
    public async Task<int> DispatchAllTenantsAsync(CancellationToken cancellationToken)
    {
        var dispatched = 0;
        foreach (var tenant in await ActiveTenantsAsync(cancellationToken))
        {
            dispatched += await ProcessTenantIsolatedAsync(tenant, cancellationToken);
        }

        return dispatched;
    }

    private async Task<int> ProcessTenantIsolatedAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        // Bound here as well, so the failure log below carries tenant.id too.
        TenantTelemetry.BindKnownTenant(tenant);
        try
        {
            return await ProcessTenantAsync(tenant, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // One tenant's failure (say, a bad row) must not stop every other tenant's dispatch.
            LogTenantFailed(ex, tenant.Value);
            return 0;
        }
    }

    /// <summary>Claims and processes up to <see cref="OutboxOptions.BatchSize"/> of the tenant's due messages.</summary>
    public async Task<int> ProcessTenantAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        // The tenant scopes below each last one database call, but the logs between them (retry,
        // parked, unreadable, lease lost) are this tenant's too. Bound in this async method, so the
        // binding ends when it returns and the next tenant starts clean.
        TenantTelemetry.BindKnownTenant(tenant);

        var processed = 0;
        while (processed < Options.BatchSize)
        {
            var leaseToken = Guid.NewGuid();
            var claimed = await runner.RunAsTenantAsync(tenant, services =>
                ClaimNextAsync(services, tenant, leaseToken, cancellationToken));
            if (claimed is not { } messageId)
            {
                break;
            }

            await ProcessMessageAsync(tenant, messageId, leaseToken, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task<List<TenantId>> ActiveTenantsAsync(CancellationToken cancellationToken)
    {
        // The catalog is platform data, readable without a tenant.
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Status == TenantStatus.Active)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<Guid?> ClaimNextAsync(
        IServiceProvider services,
        TenantId tenant,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseUntil = now + Options.LeaseDuration;
        var tenantId = tenant.Value;

        // Raw SQL because EF Core cannot express FOR UPDATE SKIP LOCKED. Raw SQL bypasses the EF query
        // filter, so the tenant predicate is written out here; row-level security applies underneath.
        var claimed = await services.GetRequiredService<PlatformDbContext>().Database.SqlQuery<Guid>($"""
            UPDATE outbox_messages SET locked_until = {leaseUntil}, lease_token = {leaseToken}
            WHERE id = (
                SELECT id FROM outbox_messages
                WHERE tenant_id = {tenantId}
                  AND processed_at IS NULL AND failed_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                  AND (locked_until IS NULL OR locked_until < {now})
                ORDER BY occurred_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }

    private async Task ProcessMessageAsync(TenantId tenant, Guid messageId, Guid leaseToken, CancellationToken cancellationToken)
    {
        // Read after claiming: while this claim holds, only this dispatcher can change the row, so
        // AttemptCount is current.
        var message = await runner.RunAsTenantAsync(tenant, services =>
            services.GetRequiredService<PlatformDbContext>().OutboxMessages
                .AsNoTracking()
                .SingleAsync(m => m.Id == messageId, cancellationToken));

        using var activity = OutboxDiagnostics.StartProcessing(message);

        var domainEvent = registry.TryDeserialize(message.Type, message.Payload, out var unreadable);
        if (domainEvent is null)
        {
            // Retrying cannot fix an unknown type or an unreadable payload: park it for an operator,
            // with the reason, so the operator can tell a bad payload from a deployment bug.
            LogUnreadable(message.Id, message.Type, unreadable);
            if (await ParkAsync(tenant, messageId, leaseToken, Truncate(unreadable ?? "Unreadable."), cancellationToken))
            {
                metrics.Parked(message.Type, tenant);
            }

            return;
        }

        var context = new DomainEventContext(message.Id, tenant, message.OccurredAt, message.AttemptCount + 1);
        foreach (var handler in registry.HandlersFor(domainEvent.GetType()))
        {
            try
            {
                await runner.RunAsTenantAsync(tenant, services =>
                    RunHandlerAsync(services, handler, domainEvent, context, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await RecordFailureAsync(tenant, message, leaseToken, handler, ex, cancellationToken);
                return;
            }
        }

        var markedProcessed = await UpdateIfOwnedAsync(tenant, messageId, leaseToken, (owned, now) =>
            owned.ExecuteUpdateAsync(set => set
                .SetProperty(m => m.ProcessedAt, now)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LeaseToken, (Guid?)null), cancellationToken));
        if (markedProcessed)
        {
            metrics.Processed(message.Type, tenant);
        }
        else
        {
            LogLeaseLost(message.Id);
        }
    }

    private async Task RunHandlerAsync(
        IServiceProvider services,
        RegisteredHandler handler,
        IDomainEvent domainEvent,
        DomainEventContext context,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await ReceiptExistsAsync(db, handler, context, cancellationToken))
        {
            return;
        }

        // Database work the handler does through this scope's PlatformDbContext commits with the receipt.
        await handler.InvokeAsync(services, domainEvent, context, cancellationToken);
        db.OutboxHandlerReceipts.Add(new OutboxHandlerReceipt(context.MessageId, handler.Name, timeProvider.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Either another dispatcher (one whose lease expired while this one worked) finished this
            // handler first, or the handler's own work broke a unique constraint. The constraint name
            // cannot tell them apart: PostgreSQL reports whichever insert failed first, and a
            // concurrent run of an idempotent handler collides on its own table as well as on the
            // receipt. So decide on the receipt itself. Committed by the other dispatcher: done, not
            // failed (its copy of the work is the one that counts). Absent: a real failure.
            await transaction.RollbackAsync(cancellationToken);
            if (await ReceiptExistsAsync(db, handler, context, cancellationToken))
            {
                return;
            }

            throw;
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static Task<bool> ReceiptExistsAsync(
        PlatformDbContext db,
        RegisteredHandler handler,
        DomainEventContext context,
        CancellationToken cancellationToken) =>
        db.OutboxHandlerReceipts.AnyAsync(r => r.MessageId == context.MessageId && r.Handler == handler.Name, cancellationToken);

    private async Task RecordFailureAsync(
        TenantId tenant,
        OutboxMessage message,
        Guid leaseToken,
        RegisteredHandler handler,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var attempt = message.AttemptCount + 1;
        var error = Truncate($"{handler.Name}: {exception.GetType().Name}: {exception.Message}");

        // Metrics and logs count a failure only when this dispatcher still owns the message; a failure
        // seen by a dispatcher whose lease was taken over is not the message's failure.
        if (attempt >= Options.MaxAttempts)
        {
            if (await ParkAsync(tenant, message.Id, leaseToken, error, cancellationToken))
            {
                LogParked(exception, message.Id, message.Type, attempt);
                metrics.HandlerFailed(message.Type, tenant);
                metrics.Parked(message.Type, tenant);
            }

            return;
        }

        var retryAt = timeProvider.GetUtcNow() + RetryDelay(attempt);
        var recorded = await UpdateIfOwnedAsync(tenant, message.Id, leaseToken, (owned, _) =>
            owned.ExecuteUpdateAsync(set => set
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.NextAttemptAt, retryAt)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LeaseToken, (Guid?)null), cancellationToken));
        if (recorded)
        {
            LogRetry(exception, message.Id, message.Type, attempt);
            metrics.HandlerFailed(message.Type, tenant);
        }
        else
        {
            LogLeaseLost(message.Id);
        }
    }

    private async Task<bool> ParkAsync(
        TenantId tenant,
        Guid messageId,
        Guid leaseToken,
        string error,
        CancellationToken cancellationToken)
    {
        var parked = await UpdateIfOwnedAsync(tenant, messageId, leaseToken, (owned, now) =>
            owned.ExecuteUpdateAsync(set => set
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.FailedAt, now)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LeaseToken, (Guid?)null), cancellationToken));
        if (!parked)
        {
            LogLeaseLost(messageId);
        }

        return parked;
    }

    /// <summary>
    /// Applies an update only while this claim still owns the message and it is still pending. Returns
    /// false when the lease was lost (expired and taken over) or the message is already finished.
    /// </summary>
    private async Task<bool> UpdateIfOwnedAsync(
        TenantId tenant,
        Guid messageId,
        Guid leaseToken,
        Func<IQueryable<OutboxMessage>, DateTimeOffset, Task<int>> update)
    {
        var rows = await runner.RunAsTenantAsync(tenant, services =>
        {
            var owned = services.GetRequiredService<PlatformDbContext>().OutboxMessages.Where(m =>
                m.Id == messageId && m.LeaseToken == leaseToken && m.ProcessedAt == null && m.FailedAt == null);
            return update(owned, timeProvider.GetUtcNow());
        });

        return rows == 1;
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var exponential = Options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, Options.MaxRetryDelay.TotalMilliseconds);

        // Jitter spreads out messages that failed together (say, during a dependency outage), so they
        // do not all retry in the same instant and knock it over again.
        return TimeSpan.FromMilliseconds(capped * (0.5 + (Random.Shared.NextDouble() * 0.5)));
    }

    private static string Truncate(string error) => error.Length <= 2000 ? error : error[..2000];

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch for tenant {TenantId} failed; continuing with the other tenants.")]
    private partial void LogTenantFailed(Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} of type {EventType} cannot be read ({Reason}); parking it.")]
    private partial void LogUnreadable(Guid messageId, string eventType, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} of type {EventType} failed on attempt {Attempt}; will retry.")]
    private partial void LogRetry(Exception exception, Guid messageId, string eventType, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} of type {EventType} failed on attempt {Attempt}; parking it after too many attempts.")]
    private partial void LogParked(Exception exception, Guid messageId, string eventType, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId}: this dispatcher's lease expired and was taken over; leaving the message to its new owner.")]
    private partial void LogLeaseLost(Guid messageId);
}
