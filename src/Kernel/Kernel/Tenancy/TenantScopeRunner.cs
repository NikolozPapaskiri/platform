using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Contracts.Tenancy;
using Platform.Kernel.Telemetry;

namespace Platform.Kernel.Tenancy;

/// <summary>
/// Runs background work explicitly as one named tenant. Code without a tenant (a hosted service,
/// a scheduled job) sees no tenant-owned rows at all; this is the only way for it to act for a
/// tenant, and it makes that choice visible at the call site.
/// </summary>
/// <remarks>
/// Each call gets a fresh DI scope, so the <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// and everything else scoped belongs to that tenant alone and is disposed afterwards.
/// </remarks>
public sealed class TenantScopeRunner(IServiceScopeFactory scopeFactory)
{
    public async Task RunAsTenantAsync(TenantId tenantId, Func<IServiceProvider, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = scopeFactory.CreateAsyncScope();
        Enter(scope.ServiceProvider, tenantId);
        await work(scope.ServiceProvider);
    }

    public async Task<T> RunAsTenantAsync<T>(TenantId tenantId, Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = scopeFactory.CreateAsyncScope();
        Enter(scope.ServiceProvider, tenantId);
        return await work(scope.ServiceProvider);
    }

    private static void Enter(IServiceProvider services, TenantId tenantId)
    {
        var tenant = services.GetRequiredService<TenantContext>();

        // Bound in this async method, so telemetry sees this tenant for the work below and the
        // binding disappears when RunAsTenantAsync returns.
        TenantTelemetry.Bind(tenant);
        tenant.Set(tenantId);
    }
}
