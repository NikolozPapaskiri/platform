# M0 hand-write: tenant resolution and row-level security

You write three pieces of M0 yourself. Everything around them (contracts, the base `DbContext`, the
catalog, the database roles, the tests) is already in place. This guide says **what** each piece
must guarantee and **what to study**. It deliberately contains no solution code. When you want help,
ask for a hint: the agent answers concept first, then approach, and only gives code if you
explicitly ask for code.

Why this design exists: `docs/adr/0003-tenant-isolation-ef-plus-rls.md`.

**The ordered walkthrough** (every step, with the APIs, the checks, and the design choice in B
explained): [`m0-tenancy-steps.md`](m0-tenancy-steps.md).

## 1. What you build

| # | Piece | File | Status today |
|---|---|---|---|
| A | Tenant resolution middleware | `src/Kernel/Kernel/Tenancy/TenantResolutionMiddleware.cs` | Stub, throws; **not registered** |
| B | Tenant session interceptor | `src/Kernel/Kernel/Persistence/TenantSessionInterceptor.cs` | Empty marker class; **not registered** |
| C | Row-level security policies | `src/Kernel/Kernel/Persistence/Migrations/*_AddRowLevelSecurity.cs` | Empty `Up` and `Down` |

### A. Tenant resolution middleware

For every request that needs a tenant:

- Read the request's host name and look it up in the tenant catalog (`TenantDomains`, then `Tenants`).
- Known host of an **active** tenant: set the tenant for this request's scope (`TenantContext`), then continue.
- **Unknown** host: respond **404** and stop.
- Known host of a **suspended** tenant: respond **403** and stop.
- Host names are case-insensitive.
- Health endpoints (`/health/live`, `/health/ready`) must keep working with no tenant.
- Then register it in the host's request pipeline (`src/Host/Platform.Api/Program.cs`), after
  `TenantTelemetryMiddleware`, which must stay first so `tenant.id` reaches every span and log.

### B. Tenant session interceptor

Make the current tenant visible to PostgreSQL for every command EF Core sends, so the policies from C
can use it:

- Commands running for a tenant carry that tenant.
- Commands running with **no** tenant carry **no** tenant, even on a pooled connection that served a
  tenant a moment ago.
- It must hold for LINQ queries, `SaveChanges`, and raw SQL through the same `DbContext`.
- Then register it with the `PlatformDbContext` in `AddKernel`
  (`src/Kernel/Kernel/KernelServiceCollectionExtensions.cs`).

Choosing which EF Core interceptor interface(s) to implement, and whether the tenant is set per
connection or per transaction, is part of the exercise. The three options, their tradeoffs, and a
recommendation (behind a "try first" fold) are in step B1 of the walkthrough.

### C. Row-level security migration

For **every tenant-owned table** (today: `outbox_messages` and `outbox_handler_receipts`):

Note that `outbox_handler_receipts` is created by a migration that runs *after*
`AddRowLevelSecurity`, so it does not exist yet when your migration runs. Deciding how it gets its
policy is the same question every new table in M1 will raise; contract 6 fails until it has one.

- Enable row-level security, and make it apply to the table owner too.
- Reads only return the current tenant's rows. With no tenant set: zero rows, not an error.
- Inserts and updates are rejected by the database if the row's `tenant_id` is not the current tenant.
- `Down` fully reverses `Up`.

## 2. The contract: tests to unskip

All in `tests/Kernel.Tests/Tenancy/TenancyContractTests.cs`, each marked
`[Fact(Skip = "HAND-WRITE: Nika")]`. Remove the `Skip` as each piece lands. Every one was checked to
fail today for the right reason (missing behaviour, not broken test plumbing).

| Contract | Test | Needs |
|---|---|---|
| 1. Known host resolves the tenant | `Resolution_KnownHost_ResolvesItsTenant` | A |
| 1. Host match is case-insensitive | `Resolution_HostIsMatchedCaseInsensitively` | A |
| 1. Unknown host returns 404 | `Resolution_UnknownHost_Returns404` | A |
| 1. Suspended tenant returns 403 | `Resolution_SuspendedTenant_Returns403` | A |
| 1. The tenant appears on the request's logs | `Resolution_TenantAppearsOnTheRequestsLogs` | A |
| 3. Tenant A cannot read B's rows through raw SQL | `RawSql_AsTenantA_SeesOnlyTenantARows` | B + C |
| 3. The same for handler receipts | `RawSql_AsTenantA_SeesOnlyTenantAReceipts` | B + C |
| 4. Inserting another tenant's row is rejected by the database | `RawSql_InsertingARowForAnotherTenant_IsRejectedByTheDatabase` | B + C |
| 4. The same for handler receipts | `RawSql_InsertingAReceiptForAnotherTenant_IsRejectedByTheDatabase` | B + C |
| 4. Moving a row to another tenant is rejected by the database | `RawSql_MovingARowToAnotherTenant_IsRejectedByTheDatabase` | B + C |
| 5. Tenant context does not leak across pooled connections | `PooledConnection_DoesNotCarryTheTenantToTheNextUser` | B + C |
| 6. Every tenant-owned table has RLS enabled and forced | `EveryTenantOwnedTable_HasRowLevelSecurityEnabledAndForced` | C |
| 7. No tenant means no tenant-owned rows | `RawSql_WithoutTenant_ReadsNoTenantOwnedRows` | B + C |
| 7. Running as a named tenant reads that tenant's rows | `RawSql_RunningAsANamedTenant_ReadsThatTenantsRows` | B + C |
| The application can still write | `EfInsert_AsTenant_StillWorksUnderRowLevelSecurity` | B + C |

Already guaranteed by the EF Core layer and running today in `TenantAwareDbContextTests`:
contract 2 (EF reads are filtered), the EF half of 7, and the role half of 6 (the application role
owns no tables, is not a superuser, has no `BYPASSRLS`).

After A is registered in `Program.cs`, `HealthEndpointTests` also tells you whether the health
endpoints still work without a tenant.

**Suggested order:** C and B together first, then run the raw SQL tests (3, 4, 5, 6, 7); then A.
A is independent of the database work.

**Done when:** all tests pass with no `HAND-WRITE` skips left in this file, `dotnet build` has zero
warnings, and you can explain each decision out loud (section 3 lists the questions).

## 3. Concepts to study

Answer these for yourself before or while writing the code. They are also the interview questions
this work prepares you for.

**PostgreSQL row-level security**
- What does enabling row-level security on a table change, and who is exempt by default?
- What does forcing it add? Which roles still bypass it even when forced?
- What is the difference between a policy's `USING` and `WITH CHECK` expressions, and which SQL
  commands does each apply to? What happens on `UPDATE`?
- Permissive versus restrictive policies: how do several policies on one table combine?
- Docs: [Row security policies](https://www.postgresql.org/docs/17/ddl-rowsecurity.html),
  [CREATE POLICY](https://www.postgresql.org/docs/17/sql-createpolicy.html),
  [ALTER TABLE](https://www.postgresql.org/docs/17/sql-altertable.html)

**Passing context to PostgreSQL**
- How can a client give the server a per-session or per-transaction value that SQL can read? What are
  custom (dotted) configuration parameters?
- `SET` versus `SET LOCAL` versus `set_config(..., is_local)`: how long does each value live?
- What does reading a parameter that was never set do, and how can that be made safe?
- Docs: [SET](https://www.postgresql.org/docs/17/sql-set.html),
  [Configuration functions](https://www.postgresql.org/docs/17/functions-admin.html#FUNCTIONS-ADMIN-SET),
  [Customized options](https://www.postgresql.org/docs/17/runtime-config-custom.html)

**Connection pooling**
- What physically happens to a connection when your code "closes" it? What state could survive until
  the next user? Does Npgsql reset anything, and when?
- Docs: [Npgsql connection string parameters: pooling](https://www.npgsql.org/doc/connection-string-parameters.html#pooling)

**EF Core interceptors and connection lifetime**
- Which interceptor interfaces exist (connection, command, transaction, save changes), and which
  callbacks fire for a LINQ query, for `SaveChanges`, and for raw SQL?
- When does EF Core open and close the connection, and when does it start a transaction on its own?
- Docs: [Interceptors](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors),
  [Raw SQL in migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing#adding-raw-sql)

**ASP.NET Core middleware**
- How does middleware ordering work, and where must tenant resolution sit relative to routing,
  authentication (later), and the health endpoints?
- Convention-based middleware: constructor versus `InvokeAsync` parameters, and why scoped services
  belong in `InvokeAsync`.
- Docs: [Middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-10.0),
  [Write custom middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/write?view=aspnetcore-10.0),
  [Proxies and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

## 4. Known pitfalls

Phrased as things to check, not solutions.

- **Testing as the wrong role.** Superusers bypass row-level security even when it is forced. The
  local owner role is a superuser, so a policy "works" when you query as the owner proves nothing.
  The tests connect as `platform_app` for exactly this reason. Do the same when experimenting in psql.
- **A missing setting is not "no tenant".** Decide what your policy does when no tenant was ever set
  on the connection. Contract 7 requires zero rows, not an error.
- **Pooled connections remember things.** Anything set for the whole session outlives your
  `DbContext`. Contract 5 uses a pool of one connection with the driver's reset-on-reuse turned
  off, the way a transaction-mode pooler (PgBouncer, including the one Azure offers) behaves. Your
  design must be safe without anyone cleaning up after it.
- **`SET LOCAL` needs a transaction.** Outside an explicit transaction, every statement is its own
  transaction. Check which of your code paths (a query, `SaveChanges`, raw SQL) actually run inside
  one.
- **Types.** Configuration parameters are text; `tenant_id` is `uuid`. Compare them correctly, and
  think about whether the expression is evaluated once per query or once per row.
- **Every tenant-owned table needs its own policy.** Contract 6 enumerates tenant-owned tables from
  the EF model, so a forgotten table fails the build. Think about how M1's new tables will get theirs.
- **Owner versus superuser in Azure.** In Azure Database for PostgreSQL the admin role is not a true
  superuser, so forcing row-level security is what stops the owner seeing everything.
- **Host names.** Case, a port suffix, a trailing dot. Behind a proxy the original host arrives in a
  forwarded header, which must only be trusted from known proxies (relevant from M1, when it is
  deployed behind Azure's ingress).
- **Health endpoints.** Orchestrator probes carry no tenant host. Make sure they never reach tenant
  resolution.
- **A catalog lookup per request.** Correct first. If you cache, explain how a suspension takes
  effect and how quickly.
- **404 versus 403.** An unknown host and a suspended tenant deliberately answer differently. Know
  what each response tells an outsider.
