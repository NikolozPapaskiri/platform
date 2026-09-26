using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Platform.Kernel.Tests.Support;

namespace Platform.Kernel.Tests;

public sealed class HealthEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // Nothing listens on port 1. Depending on the OS the connection is refused at once or after a
    // short retry; Timeout=2 and the check's 5 s budget keep the test well inside its limits.
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";

    [Fact]
    public async Task Live_Returns200_EvenWhenDatabaseIsDown()
    {
        var response = await GetAsync(UnreachableDatabase, "/health/live");

        Assert.Equal(HttpStatusCode.OK, response.Status);
    }

    [Fact]
    public async Task Ready_Returns200_WhenDatabaseIsReachable()
    {
        var response = await GetAsync(postgres.ConnectionString, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.Status);
    }

    [Fact]
    public async Task Ready_Returns503WithoutConnectionDetails_WhenDatabaseIsDown()
    {
        var response = await GetAsync(UnreachableDatabase, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.Status);
        Assert.Equal("Unhealthy", response.Body);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string connectionString, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Platform", connectionString));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
