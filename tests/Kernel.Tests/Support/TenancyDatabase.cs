using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Persistence;
using Platform.Kernel.Tenancy;
using Platform.Kernel.Tenancy.Catalog;
using Testcontainers.PostgreSql;

namespace Platform.Kernel.Tests.Support;

/// <summary>
/// A real PostgreSQL database set up exactly like the local one: the owner role runs the migrations,
/// the application role (created by the same init script docker compose uses) is what the code under
/// test connects as. Seeded with two active tenants and one suspended tenant, and outbox rows for A and B.
/// </summary>
public sealed class TenancyDatabase : IAsyncLifetime
{
    public const string TenantAHost = "tenant-a.platform.test";
    public const string TenantBHost = "tenant-b.platform.test";
    public const string SuspendedHost = "suspended.platform.test";
    public const string UnknownHost = "nobody.platform.test";

    private const string Owner = "platform_owner";
    private const string App = "platform_app";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithUsername(Owner)
        .WithPassword(Owner)
        .WithDatabase("platform")
        .Build();

    public TenantId TenantA { get; } = TenantId.New();

    public TenantId TenantB { get; } = TenantId.New();

    public TenantId SuspendedTenant { get; } = TenantId.New();

    /// <summary>Outbox rows seeded for tenant A (bypassing the application, as the owner).</summary>
    public IReadOnlyList<Guid> TenantARows { get; } = [Guid.CreateVersion7(), Guid.CreateVersion7()];

    /// <summary>Outbox rows seeded for tenant B.</summary>
    public IReadOnlyList<Guid> TenantBRows { get; } = [Guid.CreateVersion7(), Guid.CreateVersion7()];

    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(OwnerConnectionString)
    {
        Username = App,
        Password = App,
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var initScript = await File.ReadAllTextAsync(
            RepositoryRoot.Path("docker", "postgres", "init", "01-create-app-role.sql"));
        var init = await _container.ExecScriptAsync(initScript);
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException("Role init script failed: " + init.Stderr);
        }

        await using (var owner = CreateOwnerContext())
        {
            await owner.Database.MigrateAsync();

            owner.Tenants.AddRange(
                new Tenant(TenantA, "tenant-a", TenantStatus.Active, DateTimeOffset.UtcNow),
                new Tenant(TenantB, "tenant-b", TenantStatus.Active, DateTimeOffset.UtcNow),
                new Tenant(SuspendedTenant, "suspended", TenantStatus.Suspended, DateTimeOffset.UtcNow));
            owner.TenantDomains.AddRange(
                new TenantDomain(TenantAHost, TenantA),
                new TenantDomain(TenantBHost, TenantB),
                new TenantDomain(SuspendedHost, SuspendedTenant));
            await owner.SaveChangesAsync();
        }

        // Tenant rows are seeded with plain SQL as the owner, so the seed never depends on the code
        // under test (including the hand-written RLS and interceptor).
        await using var dataSource = NpgsqlDataSource.Create(OwnerConnectionString);
        foreach (var (tenant, id) in TenantARows.Select(id => (TenantA, id)).Concat(TenantBRows.Select(id => (TenantB, id))))
        {
            await using var insert = dataSource.CreateCommand(
                "INSERT INTO outbox_messages (id, tenant_id, type, payload, occurred_at) " +
                "VALUES ($1, $2, 'Seeded', '{}'::jsonb, now())");
            insert.Parameters.Add(new NpgsqlParameter { Value = id });
            insert.Parameters.Add(new NpgsqlParameter { Value = tenant.Value });
            await insert.ExecuteNonQueryAsync();
        }

        // A tenant-owned table for a test-only entity that raises domain events (see WidgetDbContext).
        // Created by the owner, so the application role gets access through default privileges.
        await using var widgets = dataSource.CreateCommand(
            "CREATE TABLE test_widgets (id uuid PRIMARY KEY, tenant_id uuid NOT NULL, name text NOT NULL)");
        await widgets.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Adds a fresh active tenant to the catalog. Tests that write give themselves their own tenant,
    /// so their assertions cannot see rows written by other tests.
    /// </summary>
    public async Task<TenantId> CreateTenantAsync()
    {
        var tenant = TenantId.New();
        await using var owner = CreateOwnerContext();
        owner.Tenants.Add(new Tenant(tenant, "t-" + tenant.Value.ToString("N"), TenantStatus.Active, DateTimeOffset.UtcNow));
        await owner.SaveChangesAsync();
        return tenant;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The application's own service graph (AddKernel), connected as the application role.</summary>
    public ServiceProvider CreateServices(
        int? maxPoolSize = null,
        bool noResetOnClose = false,
        Action<IServiceCollection>? configure = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(AppConnectionString);
        if (maxPoolSize is { } size)
        {
            connectionString.MaxPoolSize = size;
        }

        // Npgsql normally resets session state when a pooled connection is reused. Transaction-mode
        // poolers (PgBouncer, including the one built into Azure Database for PostgreSQL) do not, so
        // tests that must not depend on that reset turn it off.
        connectionString.NoResetOnClose = noResetOnClose;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddKernel(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Owner connection, for setup and inspection only. It is never how the application connects.</summary>
    public NpgsqlDataSource CreateOwnerDataSource() => NpgsqlDataSource.Create(OwnerConnectionString);

    private PlatformDbContext CreateOwnerContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(OwnerConnectionString).Options, new TenantContext());
}

[CollectionDefinition(Name)]
public sealed class TenancyDatabaseCollection : ICollectionFixture<TenancyDatabase>
{
    public const string Name = "tenancy-database";
}
