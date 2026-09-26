using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Kernel.Persistence;

namespace Platform.Kernel;

public static class KernelServiceCollectionExtensions
{
    /// <summary>Name of the connection string in configuration (<c>ConnectionStrings:Platform</c>).</summary>
    public const string ConnectionStringName = "Platform";

    /// <summary>Registers the kernel's services: the PostgreSQL data source and its readiness check.</summary>
    public static IServiceCollection AddKernel(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup with a clear message instead of on the first request.
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. See AGENTS.md section 2.");
        }

        // One NpgsqlDataSource per application: it owns the connection pool. Registering it as a
        // singleton is what makes pooling work; creating one per request would defeat it.
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
