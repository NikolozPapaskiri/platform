using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenTelemetry.Logs;
using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;
using Platform.Kernel.Persistence;
using Platform.Kernel.Telemetry;
using Platform.Kernel.Tenancy;
using Platform.Kernel.Tests.Outbox.Support;
using Platform.Kernel.Tests.Support;

namespace Platform.Kernel.Tests.Outbox;

/// <summary>
/// The transactional outbox end to end against real PostgreSQL. Each test uses its own fresh tenant,
/// so it only ever sees its own messages.
/// </summary>
[Collection(TenancyDatabaseCollection.Name)]
public sealed class OutboxTests(TenancyDatabase database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HandlerLog _log = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    // ---- Writing: the event is saved with the change, or not at all ----

    [Fact]
    public async Task SavingAnEntity_WritesItsEventToTheOutbox_InTheSameSave()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        var widgetId = Guid.CreateVersion7();
        Widget? saved = null;

        await RunAsAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<WidgetDbContext>();
            saved = new Widget(widgetId, "first");
            db.Widgets.Add(saved);
            await db.SaveChangesAsync(Ct);
        });

        var row = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.Equal(WidgetRenamed.EventName, row.Type);
        Assert.Contains(widgetId.ToString(), row.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await WidgetCountAsync(widgetId));
        Assert.Empty(saved!.DomainEvents);
    }

    [Fact]
    public async Task RolledBackTransaction_LeavesNeitherTheChangeNorTheEvent()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        var widgetId = Guid.CreateVersion7();

        await RunAsAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<WidgetDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Widgets.Add(new Widget(widgetId, "never"));
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        });

        Assert.Empty(await OutboxRowsAsync(tenant));
        Assert.Equal(0, await WidgetCountAsync(widgetId));
    }

    [Fact]
    public async Task FailedSave_LeavesNoEvent()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        var widgetId = Guid.CreateVersion7();
        await CreateWidgetAsync(services, tenant, widgetId);

        await RunAsAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<WidgetDbContext>();
            db.Widgets.Add(new Widget(widgetId, "duplicate key"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

        // Only the first widget's event: the failed save wrote nothing.
        Assert.Single(await OutboxRowsAsync(tenant));
    }

    // ---- Dispatching ----

    [Fact]
    public async Task Dispatch_RunsHandlersAsTheEventsTenant_ThenMarksTheMessageProcessed()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await CreateWidgetAsync(services, tenant);

        var claimed = await Processor(services).ProcessTenantAsync(tenant, Ct);

        Assert.Equal(1, claimed);
        var call = Assert.Single(_log.Calls);
        Assert.Equal(tenant, call.Tenant);
        Assert.Equal(tenant, call.ScopeTenant);
        Assert.Equal(1, call.Attempt);
        var row = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(row.ProcessedAt);
        Assert.Equal(1, await ReceiptCountAsync(row.Id));

        // A processed message is never claimed again.
        Assert.Equal(0, await Processor(services).ProcessTenantAsync(tenant, Ct));
        Assert.Single(_log.Calls);
    }

    [Fact]
    public async Task HandlerFailure_IsRetriedAfterBackoff_WithoutRepeatingHandlersThatSucceeded()
    {
        var tenant = await database.CreateTenantAsync();
        _log.FailuresBeforeSuccess = 1;
        await using var services = CreateServices(withFlakyHandler: true);
        await CreateWidgetAsync(services, tenant);
        var processor = Processor(services);

        await processor.ProcessTenantAsync(tenant, Ct);

        var failed = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.Null(failed.ProcessedAt);
        Assert.Equal(1, failed.AttemptCount);
        Assert.True(failed.NextAttemptAt > _time.GetUtcNow());
        Assert.Contains("Simulated handler failure", failed.LastError, StringComparison.Ordinal);

        // Still inside the backoff window: nothing is claimed.
        Assert.Equal(0, await processor.ProcessTenantAsync(tenant, Ct));

        _time.Advance(TimeSpan.FromHours(1));
        await processor.ProcessTenantAsync(tenant, Ct);

        var done = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(done.ProcessedAt);
        Assert.Equal(1, _log.CountFor(nameof(RecordingHandler), done.Id)); // receipt: not run again
        Assert.Equal(2, _log.CountFor(nameof(FlakyHandler), done.Id));
        Assert.Equal(2, _log.Calls.Last().Attempt);
    }

    [Fact]
    public async Task DuplicateDelivery_IsHandledIdempotently()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await CreateWidgetAsync(services, tenant);
        var processor = Processor(services);
        await processor.ProcessTenantAsync(tenant, Ct);
        var message = Assert.Single(await OutboxRowsAsync(tenant));

        // Simulate the message being delivered again, as after a crash between running the handlers
        // and recording the message as processed.
        await ExecuteAsOwnerAsync("UPDATE outbox_messages SET processed_at = NULL WHERE id = $1", message.Id);
        var claimed = await processor.ProcessTenantAsync(tenant, Ct);

        Assert.Equal(1, claimed);
        Assert.Equal(1, _log.CountFor(nameof(RecordingHandler), message.Id));
        Assert.NotNull(Assert.Single(await OutboxRowsAsync(tenant)).ProcessedAt);
    }

    [Fact]
    public async Task HandlerWorkBreakingAnotherUniqueConstraint_FailsTheAttempt_NotMistakenForADuplicateReceipt()
    {
        // Only a conflict on the receipt's own key means "another dispatcher already did this".
        // Anything else rolled the handler's work back, so the message must not count as processed.
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices(withConflictingHandler: true);
        await CreateWidgetAsync(services, tenant);

        await Processor(services).ProcessTenantAsync(tenant, Ct);

        var failed = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.Null(failed.ProcessedAt);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Contains(nameof(ConflictingWriteHandler), failed.LastError, StringComparison.Ordinal);
        Assert.Equal(0, await ReceiptCountAsync(failed.Id, nameof(ConflictingWriteHandler)));
    }

    [Fact]
    public async Task DispatcherLogs_BetweenTenantScopes_CarryTheTenant()
    {
        // The retry log is written after the scope that recorded the failure has ended.
        var logs = new List<LogRecord>();
        var tenant = await database.CreateTenantAsync();
        _log.FailuresBeforeSuccess = 1;
        await using var services = CreateServices(withFlakyHandler: true, logs: logs);
        await CreateWidgetAsync(services, tenant);

        await Processor(services).ProcessTenantAsync(tenant, Ct);

        var retry = Assert.Single(logs, record => record.Body?.Contains("will retry", StringComparison.Ordinal) == true);
        Assert.Contains(retry.Attributes!, attribute =>
            attribute.Key == TenantTelemetry.TenantIdAttribute && Equals(attribute.Value, tenant.ToString()));
    }

    [Fact]
    public async Task RepeatedFailures_ParkTheMessage()
    {
        var tenant = await database.CreateTenantAsync();
        _log.FailuresBeforeSuccess = int.MaxValue;
        await using var services = CreateServices(withFlakyHandler: true, maxAttempts: 3);
        await CreateWidgetAsync(services, tenant);
        var processor = Processor(services);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await processor.ProcessTenantAsync(tenant, Ct);
            _time.Advance(TimeSpan.FromHours(1));
        }

        var parked = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(parked.FailedAt);
        Assert.Null(parked.ProcessedAt);
        Assert.Equal(3, parked.AttemptCount);
        Assert.Equal(0, await processor.ProcessTenantAsync(tenant, Ct));
    }

    [Fact]
    public async Task UnreadableMessage_IsParkedAtOnce()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await ExecuteAsOwnerAsync(
            "INSERT INTO outbox_messages (id, tenant_id, type, payload, occurred_at) VALUES ($1, $2, 'test.no-such-event', '{}'::jsonb, now())",
            Guid.CreateVersion7(),
            tenant.Value);

        await Processor(services).ProcessTenantAsync(tenant, Ct);

        var parked = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(parked.FailedAt);
        Assert.Equal(1, parked.AttemptCount);
        Assert.Contains("Unknown event type", parked.LastError, StringComparison.Ordinal);
        Assert.Empty(_log.Calls);
    }

    [Fact]
    public async Task ConcurrentDispatchers_HandleEachMessageExactlyOnce()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices(batchSize: 3);
        for (var i = 0; i < 12; i++)
        {
            await CreateWidgetAsync(services, tenant);
        }

        // Two dispatchers race over the same tenant until neither finds work.
        async Task DrainAsync()
        {
            while (await Processor(services).ProcessTenantAsync(tenant, Ct) > 0)
            {
            }
        }

        await Task.WhenAll(DrainAsync(), DrainAsync());

        var rows = await OutboxRowsAsync(tenant);
        Assert.Equal(12, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.ProcessedAt));
        Assert.All(rows, row => Assert.Equal(1, _log.CountFor(nameof(RecordingHandler), row.Id)));
    }

    [Fact]
    public async Task AbandonedClaim_IsPickedUpAfterItsLeaseExpires()
    {
        // A dispatcher claimed the message and then died: the row carries its lease and token.
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await CreateWidgetAsync(services, tenant);
        var message = Assert.Single(await OutboxRowsAsync(tenant));
        await ExecuteAsOwnerAsync(
            "UPDATE outbox_messages SET locked_until = $2, lease_token = gen_random_uuid() WHERE id = $1",
            message.Id,
            _time.GetUtcNow().AddSeconds(30));

        Assert.Equal(0, await Processor(services).ProcessTenantAsync(tenant, Ct));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await Processor(services).ProcessTenantAsync(tenant, Ct));
        Assert.NotNull(Assert.Single(await OutboxRowsAsync(tenant)).ProcessedAt);
    }

    [Fact]
    public async Task DispatcherWhoseLeaseWasTakenOver_CannotChangeTheMessage()
    {
        var tenant = await database.CreateTenantAsync();
        var gate = new Gate();
        await using var services = CreateServices(gate: gate, leaseDuration: TimeSpan.FromSeconds(30));
        await CreateWidgetAsync(services, tenant);

        // Dispatcher 1 claims the message and hangs inside the handler.
        var first = Processor(services).ProcessTenantAsync(tenant, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Its lease expires; dispatcher 2 takes the message over and completes it.
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await Processor(services).ProcessTenantAsync(tenant, Ct));

        // Dispatcher 1 wakes up. Its receipt conflicts with the one already written, and its failure
        // update must not touch the message, which dispatcher 2 owns and has finished.
        gate.Release.SetResult();
        await first;

        var row = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(row.ProcessedAt);
        Assert.Null(row.FailedAt);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(1, await ReceiptCountAsync(row.Id, nameof(GatedHandler)));
    }

    [Fact]
    public async Task StaleDispatcherFailing_DoesNotDisturbTheCurrentOwner()
    {
        // The dangerous interleaving: dispatcher 1 fails *while* dispatcher 2 owns and is still
        // processing the message. Dispatcher 1's failure update must not clear dispatcher 2's lease
        // or count an attempt, or a third dispatcher could pick the message up in parallel.
        var tenant = await database.CreateTenantAsync();
        var gate = new Gate();
        gate.FailOn(1);
        var second = gate.Block(2);
        await using var services = CreateServices(gate: gate, leaseDuration: TimeSpan.FromSeconds(30));
        await CreateWidgetAsync(services, tenant);

        var first = Processor(services).ProcessTenantAsync(tenant, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        var takeover = Processor(services).ProcessTenantAsync(tenant, Ct);
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        gate.Release.SetResult(); // dispatcher 1's handler now throws
        await first;

        var whileOwned = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.Equal(0, whileOwned.AttemptCount);
        Assert.Null(whileOwned.LastError);
        Assert.True(await IsLeasedAsync(whileOwned.Id));

        second.Release.SetResult();
        await takeover;
        Assert.NotNull(Assert.Single(await OutboxRowsAsync(tenant)).ProcessedAt);
    }

    [Fact]
    public async Task StaleDispatcherFinishingFirst_DoesNotCostTheOwnerAnAttempt()
    {
        // The reverse interleaving: the stale dispatcher's receipt commits first, so the owner's receipt
        // insert conflicts. The work is done; the owner must finish the message, not count a failure.
        var tenant = await database.CreateTenantAsync();
        var gate = new Gate();
        var second = gate.Block(2);
        await using var services = CreateServices(gate: gate, leaseDuration: TimeSpan.FromSeconds(30));
        await CreateWidgetAsync(services, tenant);

        var first = Processor(services).ProcessTenantAsync(tenant, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        var takeover = Processor(services).ProcessTenantAsync(tenant, Ct);
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        gate.Release.SetResult(); // the stale dispatcher finishes first and commits its receipt
        await first;
        second.Release.SetResult(); // then the owner finishes and hits the existing receipt
        await takeover;

        var row = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.NotNull(row.ProcessedAt);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LastError);
        Assert.Equal(1, await ReceiptCountAsync(row.Id, nameof(GatedHandler)));
    }

    [Fact]
    public async Task CancellationDuringAHandler_LeavesTheMessageToBeRetried()
    {
        var tenant = await database.CreateTenantAsync();
        var gate = new Gate();
        await using var services = CreateServices(gate: gate, leaseDuration: TimeSpan.FromSeconds(30));
        await CreateWidgetAsync(services, tenant);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = Processor(services).ProcessTenantAsync(tenant, shutdown.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var interrupted = Assert.Single(await OutboxRowsAsync(tenant));
        Assert.Null(interrupted.ProcessedAt);
        Assert.Null(interrupted.FailedAt);
        Assert.Equal(0, interrupted.AttemptCount); // shutdown is not a handler failure

        _time.Advance(TimeSpan.FromMinutes(1));
        await Processor(services).ProcessTenantAsync(tenant, Ct);
        Assert.NotNull(Assert.Single(await OutboxRowsAsync(tenant)).ProcessedAt);
    }

    [Fact]
    public async Task SuspendedTenant_IsNotDispatched()
    {
        var suspended = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await CreateWidgetAsync(services, suspended);
        await ExecuteAsOwnerAsync("UPDATE tenants SET status = 'Suspended' WHERE id = $1", suspended.Value);

        await Processor(services).DispatchAllTenantsAsync(Ct);

        Assert.Null(Assert.Single(await OutboxRowsAsync(suspended)).ProcessedAt);
        Assert.DoesNotContain(_log.Calls, call => call.Tenant == suspended);
    }

    [Fact]
    public async Task DispatchAllTenants_ReachesEveryActiveTenant()
    {
        var first = await database.CreateTenantAsync();
        var second = await database.CreateTenantAsync();
        await using var services = CreateServices();
        await CreateWidgetAsync(services, first);
        await CreateWidgetAsync(services, second);

        await Processor(services).DispatchAllTenantsAsync(Ct);

        Assert.NotNull(Assert.Single(await OutboxRowsAsync(first)).ProcessedAt);
        Assert.NotNull(Assert.Single(await OutboxRowsAsync(second)).ProcessedAt);
    }

    [Fact]
    public async Task Dispatch_JoinsTheTraceOfTheOperationThatRaisedTheEvent()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = CreateServices();
        using var requestSource = new ActivitySource("Platform.Tests.Request." + Guid.NewGuid().ToString("N"));
        var dispatchSpans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == requestSource.Name || source.Name == OutboxDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == OutboxDiagnostics.ActivitySourceName &&
                    Equals(activity.GetTagItem("tenant.id"), tenant.ToString()))
                {
                    dispatchSpans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId requestTrace;
        ActivitySpanId requestSpan;
        using (var request = requestSource.StartActivity("request raising the event"))
        {
            requestTrace = request!.TraceId;
            requestSpan = request.SpanId;
            await CreateWidgetAsync(services, tenant);
        }

        await Processor(services).ProcessTenantAsync(tenant, Ct);

        var dispatch = Assert.Single(dispatchSpans);
        Assert.Equal(requestTrace, dispatch.TraceId);
        Assert.Equal(requestSpan, dispatch.ParentSpanId);
    }

    [Fact]
    public async Task SavingAnUndeclaredEvent_FailsBeforeAnythingIsWritten()
    {
        var tenant = await database.CreateTenantAsync();
        await using var services = database.CreateServices(configure: s =>
            s.AddDbContext<WidgetDbContext>((sp, o) => o
                .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>())));
        var widgetId = Guid.CreateVersion7();

        await RunAsAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<WidgetDbContext>();
            db.Widgets.Add(new Widget(widgetId, "undeclared event"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(0, await WidgetCountAsync(widgetId));
        Assert.Empty(await OutboxRowsAsync(tenant));
    }

    private ServiceProvider CreateServices(
        bool withFlakyHandler = false,
        int maxAttempts = 10,
        int batchSize = 20,
        Gate? gate = null,
        TimeSpan? leaseDuration = null,
        bool withConflictingHandler = false,
        List<LogRecord>? logs = null) =>
        database.CreateServices(configure: services =>
        {
            if (logs is not null)
            {
                services.AddLogging(logging => logging.AddOpenTelemetry(options =>
                {
                    options.AddProcessor(new TenantLogProcessor());
                    options.AddInMemoryExporter(logs);
                }));
            }

            if (withConflictingHandler)
            {
                services.AddDomainEventHandler<WidgetRenamed, ConflictingWriteHandler>();
            }

            services.AddSingleton(_log);
            services.AddSingleton<TimeProvider>(_time);
            // Like PlatformDbContext, the test context must tell PostgreSQL the tenant, or row-level
            // security rejects the outbox rows it writes.
            services.AddDbContext<WidgetDbContext>((sp, options) => options
                .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
                .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));
            services.AddDomainEvent<WidgetRenamed>(WidgetRenamed.EventName);
            services.AddDomainEventHandler<WidgetRenamed, RecordingHandler>();
            if (withFlakyHandler)
            {
                services.AddDomainEventHandler<WidgetRenamed, FlakyHandler>();
            }

            if (gate is not null)
            {
                services.AddSingleton(gate);
                services.AddDomainEventHandler<WidgetRenamed, GatedHandler>();
            }

            services.Configure<OutboxOptions>(options =>
            {
                options.MaxAttempts = maxAttempts;
                options.BatchSize = batchSize;
                options.BaseRetryDelay = TimeSpan.FromSeconds(10);
                options.LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(1);
            });
        });

    private static OutboxProcessor Processor(IServiceProvider services) => services.GetRequiredService<OutboxProcessor>();

    private static Task RunAsAsync(IServiceProvider services, TenantId tenant, Func<IServiceProvider, Task> work) =>
        services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(tenant, work);

    private static Task CreateWidgetAsync(IServiceProvider services, TenantId tenant, Guid? id = null) =>
        RunAsAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<WidgetDbContext>();
            db.Widgets.Add(new Widget(id ?? Guid.CreateVersion7(), "widget"));
            await db.SaveChangesAsync(Ct);
        });

    private async Task<List<OutboxRow>> OutboxRowsAsync(TenantId tenant)
    {
        await using var owner = database.CreateOwnerDataSource();
        await using var query = owner.CreateCommand(
            "SELECT id, type, payload::text, processed_at, failed_at, attempt_count, next_attempt_at, last_error " +
            "FROM outbox_messages WHERE tenant_id = $1 ORDER BY occurred_at");
        query.Parameters.Add(new NpgsqlParameter { Value = tenant.Value });
        await using var reader = await query.ExecuteReaderAsync(Ct);

        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    private async Task<long> ReceiptCountAsync(Guid messageId) =>
        (long)(await ScalarAsOwnerAsync("SELECT count(*) FROM outbox_handler_receipts WHERE message_id = $1", messageId))!;

    private async Task<long> ReceiptCountAsync(Guid messageId, string handlerClassName) =>
        (long)(await ScalarAsOwnerAsync(
            "SELECT count(*) FROM outbox_handler_receipts WHERE message_id = $1 AND handler LIKE '%.' || $2",
            messageId,
            handlerClassName))!;

    private async Task<bool> IsLeasedAsync(Guid messageId) =>
        (bool)(await ScalarAsOwnerAsync(
            "SELECT locked_until IS NOT NULL AND lease_token IS NOT NULL FROM outbox_messages WHERE id = $1",
            messageId))!;

    private async Task<long> WidgetCountAsync(Guid widgetId) =>
        (long)(await ScalarAsOwnerAsync("SELECT count(*) FROM test_widgets WHERE id = $1", widgetId))!;

    private async Task<object?> ScalarAsOwnerAsync(string sql, params object[] parameters)
    {
        await using var owner = database.CreateOwnerDataSource();
        await using var command = owner.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteScalarAsync(Ct);
    }

    private async Task ExecuteAsOwnerAsync(string sql, params object[] parameters) =>
        await ScalarAsOwnerAsync(sql, parameters);

    private sealed record OutboxRow(
        Guid Id,
        string Type,
        string Payload,
        DateTimeOffset? ProcessedAt,
        DateTimeOffset? FailedAt,
        int AttemptCount,
        DateTimeOffset? NextAttemptAt,
        string? LastError);
}
