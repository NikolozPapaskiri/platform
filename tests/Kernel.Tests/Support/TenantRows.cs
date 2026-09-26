using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Persistence;

namespace Platform.Kernel.Tests.Support;

/// <summary>Reads tenant-owned rows through the application's own DbContext.</summary>
internal static class TenantRows
{
    /// <summary>Through LINQ: goes through the EF Core query filter.</summary>
    public static async Task<List<Guid>> ReadWithEfAsync(IServiceProvider services)
    {
        var tenantIds = await services.GetRequiredService<PlatformDbContext>().OutboxMessages
            .Select(message => message.TenantId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return tenantIds.ConvertAll(tenantId => tenantId.Value);
    }

    /// <summary>
    /// Through raw SQL on the same DbContext: no EF Core query filter applies, so only PostgreSQL
    /// row-level security stands between this query and other tenants' rows.
    /// </summary>
    public static Task<List<Guid>> ReadWithRawSqlAsync(IServiceProvider services) =>
        services.GetRequiredService<PlatformDbContext>().Database
            .SqlQueryRaw<Guid>("SELECT tenant_id AS \"Value\" FROM outbox_messages")
            .ToListAsync(TestContext.Current.CancellationToken);
}
