using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Kernel.Contracts.Tenancy;

namespace Platform.Kernel.Persistence;

/// <summary>
/// Tells PostgreSQL which tenant the current scope acts for, so the row-level security policies
/// (the <c>AddRowLevelSecurity</c> migrations) filter every command underneath EF Core.
/// </summary>
/// <remarks>
/// Runs each time EF Core opens a connection, and always overwrites the session value: the tenant
/// id, or an empty string when there is none. A pooled connection therefore never carries the
/// previous user's tenant, even without a reset between users. Because the value is session-scoped
/// (<c>set_config(..., false)</c>), this is safe only with direct connections: behind a
/// transaction-mode pooler such as PgBouncer, it must move to transaction-local state.
/// </remarks>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    // Must match the name the row-level security policies read.
    private const string TenantSettingName = "app.current_tenant_id";

    private readonly ITenantContext _tenantContext;

    public TenantSessionInterceptor(ITenantContext tenantContext)
    {
        // Keep the context itself.
        // Do NOT read/copy TenantId here because the tenant may not be resolved yet.
        _tenantContext = tenantContext;
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        SetTenant(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetTenantAsync(connection, cancellationToken);
    }

    private void SetTenant(DbConnection connection)
    {
        try
        {
            using var command = CreateSetTenantCommand(connection);
            command.ExecuteNonQuery();
        }
        catch
        {
            // If tenant setup fails, do not leave the connection open with
            // potentially stale tenant session state.
            connection.Close();
            throw;
        }
    }

    private async Task SetTenantAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = CreateSetTenantCommand(connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Same fail-closed rule for async calls.
            await connection.CloseAsync();
            throw;
        }
    }

    private DbCommand CreateSetTenantCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT set_config(
                '{TenantSettingName}',
                @tenant_id,
                false
            );
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant_id";
        parameter.Value = GetTenantValue();
        command.Parameters.Add(parameter);

        return command;
    }

    private string GetTenantValue()
    {
        // Always overwrite the PostgreSQL session value.
        //
        // Resolved tenant:
        //   app.current_tenant_id = "<guid>"
        //
        // No tenant:
        //   app.current_tenant_id = ""
        //
        // Step 1's RLS policy converts "" to NULL with NULLIF and therefore
        // returns zero rows instead of accidentally reusing a previous tenant.
        return _tenantContext.IsResolved
            ? _tenantContext.TenantId.Value.ToString()
            : string.Empty;
    }
}
