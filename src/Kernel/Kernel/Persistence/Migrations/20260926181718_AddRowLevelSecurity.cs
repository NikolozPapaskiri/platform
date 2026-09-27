using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Kernel.Persistence.Migrations
{
    /// <summary>
    /// Adds PostgreSQL Row-Level Security (RLS)  for the outbox_messages table.
    ///
    /// RLS guarantees that database queries can access only rows belonging to the
    /// tenant currently stored in the PostgreSQL setting "app.current_tenant_id".
    ///
    /// HAND-WRITE: Nika.
    /// Requirements and contract tests:
    /// docs/hand-write/m0-tenancy.md
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enable PostgreSQL Row-Level Security for the table.
            //
            // Once RLS is enabled, row access is controlled by the policies
            // defined on the table.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages
                ENABLE ROW LEVEL SECURITY;
                """);

            // Force the table owner to obey Row-Level Security as well.
            //
            // Without FORCE ROW LEVEL SECURITY, the table owner normally bypasses
            // RLS policies.
            //
            // Important:
            // PostgreSQL superusers and roles with the BYPASSRLS attribute can still
            // bypass RLS even when FORCE ROW LEVEL SECURITY is enabled.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages
                FORCE ROW LEVEL SECURITY;
                """);

            // Create one tenant-isolation policy for SELECT, INSERT, UPDATE and DELETE.
            //
            // The application stores the current tenant in the PostgreSQL custom setting:
            //   app.current_tenant_id
            //
            // current_setting(..., true):
            //   - returns the tenant id as text when set;
            //   - returns NULL if the setting has never been defined;
            //   - may return an empty string after the value is explicitly cleared or after
            //     a transaction-local value ends on a reused pooled connection.
            //
            // NULLIF(value, '') converts the empty string to NULL before casting to uuid.
            // This is important because ''::uuid would throw an exception.
            //
            // When there is no tenant:
            //   NULLIF(...)::uuid => NULL
            //   tenant_id = NULL => NULL
            //   RLS treats NULL as not allowed
            //
            // Therefore a missing tenant context returns zero rows instead of exposing data
            // or throwing because of an empty UUID.
            //
            // The scalar SELECT allows PostgreSQL to evaluate the tenant setting once per
            // statement instead of repeatedly as part of every row predicate.
            //
            // USING controls which existing rows can be read, updated or deleted.
            // WITH CHECK controls which rows can be inserted and what an updated row may become.
            migrationBuilder.Sql("""
                CREATE POLICY outbox_messages_tenant_isolation
                ON outbox_messages
                FOR ALL
                USING (
                    tenant_id = (
                        SELECT NULLIF(
                            current_setting('app.current_tenant_id', true),
                            ''
                        )::uuid
                    )
                )
                WITH CHECK (
                    tenant_id = (
                        SELECT NULLIF(
                            current_setting('app.current_tenant_id', true),
                            ''
                        )::uuid
                    )
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the tenant-isolation policy first.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS outbox_messages_tenant_isolation
                ON outbox_messages;
                """);

            // Remove the requirement for the table owner to obey RLS.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages
                NO FORCE ROW LEVEL SECURITY;
                """);

            // Finally disable Row-Level Security on the table.
            migrationBuilder.Sql("""
                ALTER TABLE outbox_messages
                DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
