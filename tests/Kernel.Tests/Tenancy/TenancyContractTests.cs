using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Outbox;
using Platform.Kernel.Persistence;
using Platform.Kernel.Tenancy;
using Platform.Kernel.Tests.Support;

namespace Platform.Kernel.Tests.Tenancy;

/// <summary>
/// The contract for Nika's hand-written tenancy code (docs/hand-write/m0-tenancy.md): the tenant
/// resolution middleware, the tenant session interceptor, and the row-level security migration.
/// Every test here is skipped until that code exists; remove the Skip as each part lands.
///
/// Numbers refer to the contract list in the guide. Contract test 2 and the halves of 6 and 7 that
/// the EF Core layer already guarantees run today in <see cref="TenantAwareDbContextTests"/>.
/// </summary>
[Collection(TenancyDatabaseCollection.Name)]
public sealed class TenancyContractTests(TenancyDatabase database)
{
    private const string RlsViolation = PostgresErrorCodes.InsufficientPrivilege; // 42501

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 1. Tenant resolution from the request host ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task Resolution_KnownHost_ResolvesItsTenant()
    {
        var (status, tenant) = await RequestAsync(TenancyDatabase.TenantAHost);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(database.TenantA.ToString(), tenant);
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task Resolution_HostIsMatchedCaseInsensitively()
    {
        // Host names are case-insensitive (RFC 9110 section 4.2.3); browsers usually lower-case them, but not every client does.
        var (status, tenant) = await RequestAsync("Tenant-B.Platform.TEST");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(database.TenantB.ToString(), tenant);
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task Resolution_UnknownHost_Returns404()
    {
        var (status, _) = await RequestAsync(TenancyDatabase.UnknownHost);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task Resolution_SuspendedTenant_Returns403()
    {
        var (status, _) = await RequestAsync(TenancyDatabase.SuspendedHost);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // ---- 3. Row-level security on its own (raw SQL, no EF Core query filter) ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task RawSql_AsTenantA_SeesOnlyTenantARows()
    {
        await using var services = database.CreateServices();

        var seen = await services.GetRequiredService<TenantScopeRunner>()
            .RunAsTenantAsync(database.TenantA, TenantRows.ReadWithRawSqlAsync);

        Assert.NotEmpty(seen);
        Assert.All(seen, tenant => Assert.Equal(database.TenantA.Value, tenant));
    }

    // ---- 4. The database rejects writes of another tenant's id (WITH CHECK) ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task RawSql_InsertingARowForAnotherTenant_IsRejectedByTheDatabase()
    {
        await using var services = database.CreateServices();

        var error = await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, scope =>
            InRolledBackTransactionAsync(scope, db => Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync(
                    $"INSERT INTO outbox_messages (id, tenant_id, type, payload, occurred_at) VALUES ({Guid.CreateVersion7()}, {database.TenantB.Value}, 'Probe', '{{}}'::jsonb, now())",
                    Ct))));

        Assert.Equal(RlsViolation, error.SqlState);
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task RawSql_MovingARowToAnotherTenant_IsRejectedByTheDatabase()
    {
        await using var services = database.CreateServices();
        var row = database.TenantARows[1];

        var error = await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, scope =>
            InRolledBackTransactionAsync(scope, db => Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlAsync(
                    $"UPDATE outbox_messages SET tenant_id = {database.TenantB.Value} WHERE id = {row}",
                    Ct))));

        Assert.Equal(RlsViolation, error.SqlState);
    }

    // ---- 5. Tenant context never leaks through a pooled connection ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task PooledConnection_DoesNotCarryTheTenantToTheNextUser()
    {
        // A pool of one, so every scope below reuses the same physical connection, and no reset
        // between users, like a transaction-mode pooler. Whatever the tenant mechanism is, it must
        // not depend on the driver cleaning up after it.
        await using var services = database.CreateServices(maxPoolSize: 1, noResetOnClose: true);
        var runner = services.GetRequiredService<TenantScopeRunner>();

        var seenByA = await runner.RunAsTenantAsync(database.TenantA, TenantRows.ReadWithRawSqlAsync);
        var seenByB = await runner.RunAsTenantAsync(database.TenantB, TenantRows.ReadWithRawSqlAsync);
        await using var noTenant = services.CreateAsyncScope();
        var seenWithoutTenant = await TenantRows.ReadWithRawSqlAsync(noTenant.ServiceProvider);

        Assert.NotEmpty(seenByA);
        Assert.All(seenByA, tenant => Assert.Equal(database.TenantA.Value, tenant));
        Assert.NotEmpty(seenByB);
        Assert.All(seenByB, tenant => Assert.Equal(database.TenantB.Value, tenant));
        Assert.Empty(seenWithoutTenant);
    }

    // ---- 6. Every tenant-owned table has row-level security enabled and forced ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task EveryTenantOwnedTable_HasRowLevelSecurityEnabledAndForced()
    {
        // The role half of contract test 6 runs today in TenantAwareDbContextTests.
        await using var services = database.CreateServices();
        await using var scope = services.CreateAsyncScope();
        var tables = scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Model.GetEntityTypes()
            .Where(type => typeof(ITenantOwned).IsAssignableFrom(type.ClrType))
            .Select(type => type.GetTableName()!)
            .ToList();
        Assert.NotEmpty(tables);

        await using var owner = database.CreateOwnerDataSource();
        foreach (var table in tables)
        {
            await using var command = owner.CreateCommand(
                "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = $1 AND relkind = 'r'");
            command.Parameters.Add(new NpgsqlParameter { Value = table });
            await using var reader = await command.ExecuteReaderAsync(Ct);

            Assert.True(await reader.ReadAsync(Ct), $"table {table} not found");
            Assert.True(reader.GetBoolean(0), $"row-level security is not enabled on {table}");
            Assert.True(reader.GetBoolean(1), $"row-level security is not forced on {table}");
        }
    }

    // ---- 7. No tenant context means no tenant-owned rows, unless running as a named tenant ----

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task RawSql_WithoutTenant_ReadsNoTenantOwnedRows()
    {
        await using var services = database.CreateServices();
        await using var scope = services.CreateAsyncScope();

        var seen = await TenantRows.ReadWithRawSqlAsync(scope.ServiceProvider);

        Assert.Empty(seen);
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task RawSql_RunningAsANamedTenant_ReadsThatTenantsRows()
    {
        await using var services = database.CreateServices();

        var seen = await services.GetRequiredService<TenantScopeRunner>()
            .RunAsTenantAsync(database.TenantB, TenantRows.ReadWithRawSqlAsync);

        Assert.NotEmpty(seen);
        Assert.All(seen, tenant => Assert.Equal(database.TenantB.Value, tenant));
    }

    [Fact(Skip = "HAND-WRITE: Nika")]
    public async Task EfInsert_AsTenant_StillWorksUnderRowLevelSecurity()
    {
        // Guards against policies so strict that the application itself can no longer write.
        await using var services = database.CreateServices();
        var id = Guid.CreateVersion7();

        await services.GetRequiredService<TenantScopeRunner>().RunAsTenantAsync(database.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<PlatformDbContext>();
            db.OutboxMessages.Add(new OutboxMessage(id, "Probe", "{}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        });

        Assert.Contains(id, await IdsOfAsync(database.TenantA));
    }

    private async Task<(HttpStatusCode Status, string Body)> RequestAsync(string host)
    {
        // A minimal pipeline: the kernel services, the middleware under test, and an endpoint that
        // echoes the resolved tenant. Independent of the host's Program.cs.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:Platform"] = database.AppConnectionString;
        builder.Services.AddKernel(builder.Configuration);

        await using var app = builder.Build();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.Run(context => context.Response.WriteAsync(
            context.RequestServices.GetRequiredService<ITenantContext>().TenantId.ToString()));
        await app.StartAsync(Ct);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri($"http://{host}/"), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// Runs a write that is expected to be rejected inside a transaction that is always rolled back,
    /// so a not-yet-working implementation cannot corrupt the shared seed data for other tests.
    /// </summary>
    private static async Task<T> InRolledBackTransactionAsync<T>(IServiceProvider scope, Func<PlatformDbContext, Task<T>> write)
    {
        var db = scope.GetRequiredService<PlatformDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        try
        {
            return await write(db);
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    private async Task<List<Guid>> IdsOfAsync(TenantId tenant)
    {
        await using var owner = database.CreateOwnerDataSource();
        await using var command = owner.CreateCommand("SELECT id FROM outbox_messages WHERE tenant_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = tenant.Value });
        await using var reader = await command.ExecuteReaderAsync(Ct);

        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
