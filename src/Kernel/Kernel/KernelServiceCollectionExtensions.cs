using System.Diagnostics;
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
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString)
            // Trace only commands that belong to some operation (a request, an outbox dispatch).
            // The dispatcher's background polling would otherwise start a new root trace every
            // poll interval: noise locally, and paid-for volume once exported. Trade-off: the
            // dispatcher's catalog walk and claim queries are not traced; Npgsql metrics still
            // show their timing.
            .ConfigureTracing(tracing => tracing.ConfigureCommandFilter(_ => Activity.Current is not null))
            .Build());

        // One tenant per scope (request or background job). The concrete type is registered so Kernel
        // code can set it; everything else depends on the read-only ITenantContext.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<TenantScopeRunner>();

        // M0 stores the current tenant in PostgreSQL session state whenever EF opens
        // a connection. This allows RLS to protect LINQ, SaveChanges and raw SQL,
        // including commands executed outside an explicit transaction.
        //
        // This uses session-scoped set_config(..., false). If a transaction-mode
        // pooler such as PgBouncer is introduced later, this must move to
        // transaction-local tenant state.
        services.AddScoped<TenantSessionInterceptor>();
        services.AddDbContext<PlatformDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
            options.AddInterceptors(
                    sp.GetRequiredService<TenantSessionInterceptor>());
        });

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
