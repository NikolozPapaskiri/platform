using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Platform.Kernel.Persistence;

/// <summary>
/// Readiness check: can this instance open a connection and run a trivial query? It uses the same
/// <see cref="NpgsqlDataSource"/> (and therefore the same pool and settings) as the application, so
/// "ready" means the real connection path works, not a separate probe connection.
/// </summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The endpoint only reports the status; the exception goes to the health-check log, so
            // connection details never reach an anonymous caller.
            return new HealthCheckResult(context.Registration.FailureStatus, "PostgreSQL is unreachable.", ex);
        }
    }
}
