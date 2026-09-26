using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Kernel.Outbox;

/// <summary>
/// The in-process background worker (ADR 0002): runs a dispatch pass, waits the poll interval, repeats.
/// A failed pass is logged and retried on the next tick; it never stops the loop. On shutdown the
/// current pass is cancelled, and any message it had claimed is picked up again once its lease expires.
/// </summary>
public sealed partial class OutboxDispatcher(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.DispatchAllTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPassFailed(ex);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch pass failed; retrying after the poll interval.")]
    private partial void LogPassFailed(Exception exception);
}
