using Testcontainers.PostgreSql;

namespace Platform.Kernel.Tests.Support;

/// <summary>
/// One throwaway PostgreSQL container per test class that uses it. Real PostgreSQL, never an
/// in-memory provider: tenancy, transactions, and RLS behave differently (or not at all) in fakes.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
