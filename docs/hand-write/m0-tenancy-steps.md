# M0 hand-write, step by step

The companion to [`m0-tenancy.md`](m0-tenancy.md), which lists what each piece must guarantee,
the concepts, and the pitfalls. This file is the path through it, in order: what to do at each
step, why, which API or SQL feature does it, how to check it worked, and what usually goes wrong.

It names every function, command, and type you need, but it does not write the solution. If a step
stalls, ask for the next hint on that step, or explicitly for the code of that step.

## 0. Before you start

- Branch from `main`, for example `m0/hand-write-tenancy`.
- `dotnet tool restore`, `docker compose up -d --wait`, then `dotnet test`: everything green, with 15
  tests skipped as `HAND-WRITE: Nika`.
- **How the tests use the database.** They start their own PostgreSQL container (Testcontainers),
  apply every migration as `platform_owner`, and connect as `platform_app`. You never have to update
  your local database to run them. The local database is only for experimenting in `psql`.
- **Order: C1, then B, then C2, then A.** The moment C1 lands, `platform_app` sees zero rows and
  cannot insert, because nothing tells PostgreSQL the tenant yet. `OutboxTests` and others go red.
  That is row-level security failing closed, which is what you want, not a bug. B turns them green
  again, so C1 and B belong in the same commit or at least the same PR.

## 1. The design in one picture

1. A request arrives. **A** reads its host name, finds the tenant in the catalog, and calls
   `TenantContext.Set`.
2. EF Core opens a database connection. **B** tells PostgreSQL "this session acts for tenant X".
3. Every statement on that connection is filtered by **C**'s policies, which compare each row's
   `tenant_id` with that value.

EF Core's query filter already does the same in C#. Row-level security is the net underneath: it
catches raw SQL, a forgotten filter, and bugs in the C# layer.

Exactly one value crosses from .NET to PostgreSQL: the tenant id, carried in a **custom
configuration parameter**. Choose its name now, because B writes it and C reads it and they must
match exactly. Custom parameters need a dotted name with a prefix of your own, for example
`app.tenant_id`.

## 2. C1: the `AddRowLevelSecurity` migration, for `outbox_messages`

File: `src/Kernel/Kernel/Persistence/Migrations/20260926181718_AddRowLevelSecurity.cs`. This
migration runs before `outbox_handler_receipts` exists, so it covers `outbox_messages` only (C2
covers the receipts).

### Step C1.1: the four statements

For one table, `Up` does four things:

1. **Enable** row-level security on the table (`ALTER TABLE`, `ENABLE ROW LEVEL SECURITY`). From
   then on, roles without a matching policy see nothing.
2. **Force** it (`FORCE ROW LEVEL SECURITY`), so the table's owner is subject to it too. Locally the
   owner is a superuser and bypasses row-level security anyway. In Azure, the admin is not a true
   superuser, so forcing is what stops the owner seeing everything. Contract 6 checks both flags.
3. **One policy** (`CREATE POLICY`) that applies to all commands (`FOR ALL`), with two expressions:
   - `USING`: which **existing** rows are visible, and which can be updated or deleted;
   - `WITH CHECK`: which **new or changed** rows are allowed. An `INSERT` for another tenant, or an
     `UPDATE` that moves a row to another tenant, fails here with SQLSTATE 42501, which is what
     contract 4 expects.
4. Both expressions say the same thing: this row's `tenant_id` equals the current tenant.

`Down` undoes the steps in reverse order: drop the policy, stop forcing, disable.

Decide whether the policy names a role (`TO platform_app`) or applies to everyone (leave `TO` out,
which means `PUBLIC`). Ask yourself which of the two fails closed if a new role appears later.

### Step C1.2: reading the current tenant safely

This is the step that goes wrong most often. Use `current_setting(name, missing_ok)` with
`missing_ok` set to true. The expression must cope with three situations:

| Situation | What `current_setting(name, true)` returns |
|---|---|
| Never set in this session | `NULL`. With `missing_ok` false it raises an error, which breaks contract 7. |
| Set to a tenant | The id as text. |
| Set, but empty | `''`. This happens when B deliberately sets "no tenant", and also in a pooled session after a transaction-local value ended: PostgreSQL keeps the parameter defined and it reads back as an empty string, not `NULL`. |

The column is `uuid`; the setting is `text`. Casting `''` to `uuid` raises "invalid input syntax
for type uuid", which again breaks contract 7 ("zero rows, not an error"). So turn an empty string
into `NULL` first (look up `NULLIF`), then cast. Comparing `tenant_id` with `NULL` yields `NULL`, and
a policy treats `NULL` as false. The result is zero rows, and no error.

Performance: written plainly, the expression is evaluated once per row. Wrapped in a scalar
subquery, `(SELECT ...)`, the planner computes it once per statement (an "InitPlan"). You can see
the difference with `EXPLAIN` in step C1.5.

### Step C1.3: putting the SQL into the migration

- Raw SQL in an EF Core migration goes through `migrationBuilder.Sql(...)`. One call per statement
  keeps error messages readable.
- Names are snake_case: the table is `outbox_messages` and the column is `tenant_id`. The policy
  name is yours, for example `tenant_isolation`; it has to be unique per table.
- Migrations run as `platform_owner`, which owns the table and so may alter it.
- Leave the `.Designer.cs` file alone. The EF model does not change, which is also why CI's
  "pending model changes" check stays green.

### Step C1.4: read the generated script

`dotnet ef migrations script --project src/Kernel/Kernel` prints the whole migration SQL. Find your
statements in the `AddRowLevelSecurity` section, and check that `Down` mirrors `Up`.

### Step C1.5: try it by hand (optional, but it teaches the most)

1. Apply the migration to the local database with `dotnet ef database update` and the owner
   connection string (see AGENTS.md section 2).
2. As `platform_owner`, insert two rows into `outbox_messages` with two different made-up tenant ids.
   The columns are `id`, `tenant_id`, `type`, `payload` (a `jsonb` value such as `'{}'`), and
   `occurred_at`.
3. Connect as `platform_app` (password `platform_app`, host port 5433) and try each of these:
   - Count the rows with nothing set: expect 0.
   - Set the parameter for the session with `set_config(name, value, false)` to one tenant id and
     count again: expect only that tenant's row.
   - Set it to `''`: expect 0 rows, and no error.
   - Insert a row with the other tenant's id: expect "new row violates row-level security policy".
   - Inside `BEGIN`, set it with `set_config(name, value, true)` (transaction-local), `COMMIT`, then
     read it back with `current_setting`: expect the empty string from step C1.2.
4. Connect as `platform_owner`: it sees everything even though the table is forced, because it is a
   superuser locally. That is why a policy that "works" when you query as the owner proves nothing.

## 3. B: `TenantSessionInterceptor`

File: `src/Kernel/Kernel/Persistence/TenantSessionInterceptor.cs`.

### Step B1: pick the mechanism

This choice is the real exercise. There are three workable designs:

| | How | Good | Bad |
|---|---|---|---|
| 1. Session value on connection open | Each time EF Core opens a connection, run `set_config(name, value, false)`. The value is the tenant id, or `''` when there is no tenant. Always overwrite, never skip. | Simple. Covers LINQ, `SaveChanges`, and raw SQL, because all three open the connection through EF Core. Passes contract 5, since every open overwrites whatever the last user left. | One extra round trip per connection open. **Not safe behind a transaction-mode pooler** such as PgBouncer: consecutive transactions from one client connection can run on different server sessions, so a session value can land in the wrong one. |
| 2. Transaction-local value when a transaction starts | A transaction interceptor runs `set_config(name, value, true)` after `BEGIN`. | Safe behind any pooler. | Statements outside an explicit transaction never see the value, so they read zero rows. Every read would need a transaction. Contract 3 fails as written. |
| 3. Transaction-local value sent with every command | A command interceptor puts `set_config(name, value, true)` in the same batch as each command. Npgsql sends the statements of one batch as one implicit transaction, so the local value covers the command. | Safe behind any pooler. No extra round trip. | Rewrites every command's text and parameters. More ways to get it subtly wrong. |

**Recommendation for M0: design 1.** M0 connects to PostgreSQL directly. Say in your PR that M1
must not put a transaction-mode pooler in front of the application without moving to design 3.
Azure Database for PostgreSQL Flexible Server's built-in PgBouncer runs in transaction mode by
default. Being able to explain why is a strong interview answer. If you pick 2 or 3 instead, the
steps below change; ask.

### Step B2: the class shape (design 1)

- Derive from `DbConnectionInterceptor` (namespace `Microsoft.EntityFrameworkCore.Diagnostics`), a
  base class whose virtual methods do nothing. It replaces the bare `IInterceptor` marker in the stub.
- Override the callback that fires **after** a connection opened, in **both** forms:
  `ConnectionOpened` and `ConnectionOpenedAsync`. EF Core calls the synchronous one for synchronous
  APIs (`SaveChanges()`, `ToList()`) and the asynchronous one for async APIs. Override only one, and
  the other path runs with no tenant; the tests might not notice, production would.
- In the callback you receive the open `DbConnection`. Create a command on it, set its text to a
  `SELECT` of `set_config` with the value as a **parameter**, and execute it. In the async form,
  pass the `CancellationToken` along.
- **Why `set_config` rather than `SET`.** `SET name = value` is a utility statement, and PostgreSQL
  does not accept bind parameters in it. `set_config` is an ordinary function, so it does. Never
  concatenate the tenant id into the SQL text. A GUID cannot contain a quote, but the habit is what
  matters, and a parameterised statement also stays one reusable prepared statement.

### Step B3: where the tenant comes from

The interceptor needs the **current scope's** `ITenantContext`.

- **Recommended:** register the interceptor as a **scoped** service that takes `ITenantContext` in
  its constructor. Then add it to the `PlatformDbContext` options in `AddKernel`, inside the
  `(sp, options) => ...` lambda, with `options.AddInterceptors(...)`. This works because
  `AddDbContext` builds the options once per scope by default.
- The alternative is to read the tenant from the `DbContext` the callback receives in its event data.
  That needs a new accessor on `TenantAwareDbContext`, and it couples the interceptor to one base
  class.
- **Trap:** registered as a singleton, the interceptor would capture the first scope's tenant and
  reuse it for every later request (a captive dependency). This is also why `AddKernel` does not use
  `AddDbContextPool`; read the comment there.
- The value to send: the tenant id as text when `IsResolved`, otherwise `""`.

### Step B4: register it

In `src/Kernel/Kernel/KernelServiceCollectionExtensions.cs`, next to `AddDbContext<PlatformDbContext>`:
register the interceptor, add it in the options lambda, and replace the "registered here once
implemented" comment with one that explains the choice.

### Step B5: timing, to reason about before testing

- The value is captured **when the connection opens**. EF Core opens and closes the connection
  around each operation, unless it is already open or a transaction is running.
- Suppose a connection opened before `TenantContext.Set` is still open afterwards. It carries "no
  tenant", so it fails closed (zero rows) rather than open. Where could that happen? The middleware's
  own catalog lookup uses the same scoped `DbContext` before `Set`. It works because EF Core closed
  the connection after that query.
- When a test sees zero rows, work out which layer filtered them: EF Core's query filter or the
  database policy.

### Step B6: test

Remove the `Skip` from these tests in `tests/Kernel.Tests/Tenancy/TenancyContractTests.cs`:
- `RawSql_AsTenantA_SeesOnlyTenantARows`;
- `RawSql_InsertingARowForAnotherTenant_IsRejectedByTheDatabase`;
- `RawSql_MovingARowToAnotherTenant_IsRejectedByTheDatabase`;
- `PooledConnection_DoesNotCarryTheTenantToTheNextUser`;
- both contract 7 tests;
- `EfInsert_AsTenant_StillWorksUnderRowLevelSecurity`.

```bash
dotnet test --project tests/Kernel.Tests --filter-class Platform.Kernel.Tests.Tenancy.TenancyContractTests
```

Then run the whole suite: `OutboxTests` must be green again.

| Symptom | Likely cause |
|---|---|
| "invalid input syntax for type uuid" | Step C1.2: the empty string is cast without `NULLIF`. |
| Everything returns zero rows | The parameter name differs between B and C, or the interceptor is not registered. |
| Contract 5 fails | Something skips the overwrite when there is no tenant. |
| Works with async calls, fails with sync ones | Only one of the two callbacks is overridden. |

## 4. C2: a follow-up migration for `outbox_handler_receipts`

- **Why a new migration.** The receipts table is created by `AddOutboxDispatching`, which runs
  after `AddRowLevelSecurity`. A migration that has been applied anywhere (CI, and every developer
  database) is never edited, so the receipts get their own migration.
- Create it with:

  ```bash
  dotnet ef migrations add AddReceiptRowLevelSecurity --project src/Kernel/Kernel --output-dir Persistence/Migrations
  ```

  The model has not changed, so `Up` and `Down` come out empty. Fill them in exactly as in C1, for
  `outbox_handler_receipts`.
- **This is the M1 pattern.** Every migration that creates a tenant-owned table ends with its
  row-level security statements. Consider whether a small helper, such as an extension method on
  `MigrationBuilder` that every future migration calls, is worth writing. If you write one, it is
  part of your hand-write.
- Remove the `Skip` from `RawSql_AsTenantA_SeesOnlyTenantAReceipts`,
  `RawSql_InsertingAReceiptForAnotherTenant_IsRejectedByTheDatabase`, and contract 6
  (`EveryTenantOwnedTable_HasRowLevelSecurityEnabledAndForced`). Contract 6 reads the tenant-owned
  tables from the EF model, so it fails for any table you forget.

## 5. A: `TenantResolutionMiddleware`

File: `src/Kernel/Kernel/Tenancy/TenantResolutionMiddleware.cs`.

### Step A1: the class shape

- This is **convention-based** middleware. The constructor takes `RequestDelegate next` and nothing
  scoped, because middleware is constructed once for the application's lifetime.
- Scoped services go in as **extra parameters of `InvokeAsync`**: here `PlatformDbContext` and
  `TenantContext`. ASP.NET Core resolves them from the request's scope. Put them in the constructor
  instead, and one request's `DbContext` would be shared by every request.
- Take the concrete `TenantContext`, not `ITenantContext`: `Set` is `internal` on the concrete type,
  and the middleware is in the Kernel, so it may call it.

### Step A2: read and normalise the host

- `context.Request.Host.Host` is the host without the port.
- Lower-case it with `ToLowerInvariant`. The invariant culture matters: in some cultures "I" does
  not lower-case to "i" (look up the Turkish I problem).
- Strip one trailing dot. `tenant-a.platform.test.` is the fully qualified form of the same name.
- Stored hosts are lower-case, as in the test seed. Consider making `TenantDomain`'s constructor
  normalise too, so a future insert cannot break the rule, and mention it in your PR.
- Compare with plain equality, so the primary key on `host` serves the lookup. `ToLower()` inside
  the LINQ query would translate to `lower(host)` in SQL, which that index cannot use.

### Step A3: look it up

- One query: `TenantDomains` joined to `Tenants` on the tenant id, projecting only the id and the
  status, with `AsNoTracking`.
- The catalog tables are `[TenantCatalog]`: no query filter and no row-level security, so they are
  readable before any tenant is known. Look at how `OutboxProcessor.ActiveTenantsAsync` reads
  `Tenants`.

### Step A4: decide

| Lookup result | Response |
|---|---|
| Nothing found | **404**. Return without calling `next`: not calling it is how middleware ends the request. |
| Tenant is `Suspended` | **403**, and return. |
| Tenant is `Active` | `tenantContext.Set(id)`, then `await next(context)`. |

Think about what each answer tells an outsider. A 404 reveals nothing. A 403 reveals that a
suspended tenant lives at that host, which is acceptable only because hosts are public anyway.

### Step A5: register it

- In `src/Host/Platform.Api/Program.cs`, register it **after** `TenantTelemetryMiddleware`.
  Telemetry binds the tenant context object first; `Set` then tags the request span, and every
  later log record carries `tenant.id`. `Resolution_TenantAppearsOnTheRequestsLogs` checks exactly
  this.
- Keep `/health/*` out of it: orchestrator probes carry no tenant host. There are two ways:
  - Branch the pipeline with `app.UseWhen(...)`, using a predicate built on
    `Request.Path.StartsWithSegments("/health")`. The middleware stays single-purpose.
  - Check the path inside the middleware. Registration stays simpler, but the middleware knows about
    health checks.

  Both pass the tests. Pick one and say why in the PR.

### Step A6: test

- Remove the `Skip` from the five `Resolution_*` tests.
- Run `HealthEndpointTests`: both endpoints must still return 200 with no tenant.

## 6. Finish

- `TenancyContractTests` has no `HAND-WRITE` skips left.
- `dotnet build`, `dotnet format Platform.slnx --verify-no-changes`, and `dotnet test` are all clean.
- Remove the `TODO(nika)` comments and the "not registered" remarks from the three files, and set
  the status column in `m0-tenancy.md` to done.
- Open the PR (AGENTS.md section 7) and ask the reviewer agent to review your hand-written code.
- The gate: answer the questions in `m0-tenancy.md` section 3 out loud, including why you chose
  your design in step B1 and when it stops being safe.
