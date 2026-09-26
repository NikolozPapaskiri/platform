using System.Collections.Concurrent;
using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Tests.Outbox.Support;

/// <summary>What each handler saw, shared by the handlers of one test's service provider.</summary>
public sealed class HandlerLog
{
    private readonly ConcurrentQueue<(string Handler, Guid MessageId, TenantId Tenant, TenantId ScopeTenant, int Attempt)> _calls = new();

    /// <summary>How many times the failing handler should throw before it succeeds.</summary>
    public int FailuresBeforeSuccess { get; set; }

    public IReadOnlyList<(string Handler, Guid MessageId, TenantId Tenant, TenantId ScopeTenant, int Attempt)> Calls => [.. _calls];

    public int CountFor(string handler, Guid messageId) =>
        _calls.Count(call => call.Handler == handler && call.MessageId == messageId);

    public void Record(string handler, DomainEventContext context, ITenantContext scope) =>
        _calls.Enqueue((handler, context.MessageId, context.TenantId, scope.TenantId, context.Attempt));
}

/// <summary>Always succeeds.</summary>
public sealed class RecordingHandler(HandlerLog log, ITenantContext tenant) : IDomainEventHandler<WidgetRenamed>
{
    public Task HandleAsync(WidgetRenamed domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        log.Record(nameof(RecordingHandler), context, tenant);
        return Task.CompletedTask;
    }
}

/// <summary>Lets a test hold the first handler call open, to control what happens while it runs.</summary>
public sealed class Gate
{
    private readonly ConcurrentDictionary<int, Step> _steps = new();
    private readonly ConcurrentDictionary<int, bool> _failing = new();
    private int _calls;

    /// <summary>Call 1 always blocks until released.</summary>
    public Gate()
    {
        Block(1);
    }

    public TaskCompletionSource Entered => _steps[1].Entered;

    public TaskCompletionSource Release => _steps[1].Release;

    /// <summary>Makes call <paramref name="call"/> block until its step is released.</summary>
    public Step Block(int call) => _steps.GetOrAdd(call, _ => new Step());

    /// <summary>Makes call <paramref name="call"/> throw once it is released.</summary>
    public void FailOn(int call) => _failing[call] = true;

    internal async Task PassAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (_steps.TryGetValue(call, out var step))
        {
            step.Entered.TrySetResult();
            await step.Release.Task.WaitAsync(cancellationToken);
        }

        if (_failing.ContainsKey(call))
        {
            throw new InvalidOperationException($"Simulated failure on call {call}.");
        }
    }

    public sealed class Step
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Calls block or fail as scripted by the <see cref="Gate"/>; unscripted calls pass straight through.</summary>
public sealed class GatedHandler(HandlerLog log, Gate gate, ITenantContext tenant) : IDomainEventHandler<WidgetRenamed>
{
    public async Task HandleAsync(WidgetRenamed domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        log.Record(nameof(GatedHandler), context, tenant);
        await gate.PassAsync(cancellationToken);
    }
}

/// <summary>Throws until it has failed <see cref="HandlerLog.FailuresBeforeSuccess"/> times.</summary>
public sealed class FlakyHandler(HandlerLog log, ITenantContext tenant) : IDomainEventHandler<WidgetRenamed>
{
    public Task HandleAsync(WidgetRenamed domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        log.Record(nameof(FlakyHandler), context, tenant);
        if (log.CountFor(nameof(FlakyHandler), context.MessageId) <= log.FailuresBeforeSuccess)
        {
            throw new InvalidOperationException("Simulated handler failure.");
        }

        return Task.CompletedTask;
    }
}
