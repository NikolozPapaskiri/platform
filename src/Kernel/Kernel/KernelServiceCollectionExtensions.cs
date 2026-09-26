using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Kernel.Contracts.Events;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;
using Platform.Kernel.Persistence;
using Platform.Kernel.Tenancy;

namespace Platform.Kernel;

public static class KernelServiceCollectionExtensions
{
    /// <summary>Name of the connection string in configuration (<c>ConnectionStrings:Platform</c>).</summary>
    public const string ConnectionStringName = "Platform";

    /// <summary>
    /// Registers the kernel's services: the PostgreSQL data source, tenancy, the platform DbContext,
    /// and the database readiness check. The connection string is for the application role, which
    /// cannot own tables or bypass row-level security.
    /// </summary>
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

        // One tenant per scope (request or background job). The concrete type is registered so Kernel
        // code can set it; everything else depends on the read-only ITenantContext.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<TenantScopeRunner>();

        // Not pooled (AddDbContextPool): a pooled context would outlive the scope whose tenant it
        // captured. The hand-written TenantSessionInterceptor is registered here once implemented.
        services.AddDbContext<PlatformDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        // Outbox: events are staged by TenantAwareDbContext.SaveChanges and dispatched in-process.
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName));
        services.AddSingleton(sp => new DomainEventRegistry(
            sp.GetServices<DomainEventRegistration>(),
            sp.GetServices<DomainEventHandlerRegistration>()));
        services.AddSingleton<OutboxMessageFactory>();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxDispatcher>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
