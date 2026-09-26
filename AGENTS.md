# AGENTS.md

Canonical rules for every AI tool working in this repository (Claude Code, Codex, Copilot, others).
Tool-specific notes live in their own files (for example `CLAUDE.md`) and only add to this one.

## 1. What this is

A multi-tenant SaaS **kernel** with vertical **packs**. The first pack is event ticketing sold through
**white-label storefronts**: each organizer (tenant) sells from its own host name, buyers check out as
guests by default, and money goes straight to the organizer's own merchant account. There is no
cross-organizer marketplace in scope.

- **Owner:** Nika (@NikolozPapaskiri). Under 10 hours per week, so scope discipline beats everything.
- **Goals, weighted equally:** ship the product, and Nika learning .NET/C# and Azure deeply.
- **Review:** Nika plus one trusted reviewer. Every change is a pull request. Nothing is pushed to `main`.
- **Current milestone:** M0 (foundation). Milestone map: `docs/playbook.md`.

## 2. Build, run, test

Prerequisites: .NET SDK 10.0.301 (pinned in `global.json`), Docker Desktop.

```bash
dotnet tool restore                                    # local tools (dotnet-ef), pinned in dotnet-tools.json
docker compose up -d --wait                            # PostgreSQL on host port 5433
dotnet ef database update --project src/Kernel/Kernel \
  --connection "Host=localhost;Port=5433;Database=platform;Username=platform_owner;Password=platform_owner"
dotnet build Platform.slnx                             # warnings (including code style) are errors
dotnet test                                            # from the repo root; needs Docker (Testcontainers)
dotnet format Platform.slnx --verify-no-changes        # CI fails on formatting drift
dotnet run --project src/Host/Platform.Api             # http://localhost:5100/health/live and /health/ready
docker build -t platform-api .                         # production image (non-root, port 8080)
```

- Host port **5433**, not 5432: the dev machine runs a native PostgreSQL on 5432.
- **Two database roles** (ADR 0003): `platform_owner` owns the schema and is the only role that runs
  migrations (always with an explicit `--connection`); `platform_app` is what the application connects
  as. `docker/postgres/init/` creates `platform_app` when the volume is first created, so after
  changing that script run `docker compose down -v` (this deletes local data).
  **The role name `platform_app` is a deployment contract:** migrations grant and revoke on it by
  name, so every environment (including Azure in M1) must create a role with exactly that name.
- New migration: `dotnet ef migrations add <Name> --project src/Kernel/Kernel --output-dir Persistence/Migrations`.
  Read the generated SQL (`dotnet ef migrations script --project src/Kernel/Kernel`) before committing.
- Local database credentials live in `docker-compose.yml`, `docker/postgres/init/`, and
  `appsettings.Development.json`. They belong to the throwaway local container only; every other
  environment supplies `ConnectionStrings__Platform` from environment variables or a secret store.
- Tests run on **Microsoft.Testing.Platform** (xUnit v3), selected in `global.json`. Use
  `dotnet test` or `dotnet test --solution Platform.slnx`; the old positional
  `dotnet test Platform.slnx` form is VSTest-only and fails.
- Liveness checks no dependencies (a database outage must not trigger restarts); readiness checks
  PostgreSQL and returns 503 when it is unreachable.
- CI (`.github/workflows/ci.yml`) runs on every PR and push to `main`: restore, vulnerable-package
  scan, Release build, format check, architecture, unit, and integration tests, plus gitleaks over
  the full history. A red CI blocks merge; never skip a step to get green.

## 3. Structure

```
src/Kernel/Kernel.Contracts/   types packs may use (tenancy, domain events, IPaymentGateway, Money);
                               references nothing in the solution
src/Kernel/Kernel/             Tenancy, Persistence, Outbox, Telemetry implementations
src/Packs/<Pack>/              one project per vertical (Ticketing first)
src/Host/Platform.Api/         composition root: wires kernel + packs, health endpoints
tests/Architecture.Tests/      dependency rules, enforced in CI
tests/Kernel.Tests/            unit + integration (Testcontainers PostgreSQL)
tests/<Pack>.Tests/
docs/adr/                      architecture decision records
docs/hand-write/               learning guides for the parts Nika writes himself
docker/postgres/init/          local and test database roles (platform_app)
web/                           reserved for M1; no frontend exists in M0
infra/                         reserved for M1 (Azure infrastructure as code)
```

Root namespace `Platform` (assemblies `Platform.Kernel`, `Platform.Packs.Ticketing`, ...).

## 4. Boundary rules (enforced by `tests/Architecture.Tests`)

1. Packs reference `Kernel.Contracts` only. Never the `Kernel` implementation, never another pack.
2. `Kernel` never references a pack.
3. `Kernel.Contracts` references nothing in the solution.
4. Pack domain code does not reference EF Core or ASP.NET Core. Domain code lives in
   `Platform.Packs.<Pack>.Domain` (and sub-namespaces); that namespace is what the rule checks.
5. Only the host (`Platform.Api`) references everything; it is the only composition root. Every pack
   project is named `Packs.<Name>` and is referenced by the host.

Changing a boundary requires an ADR first.

## 5. Tenancy rules

Decided in `docs/adr/0003-tenant-isolation-ef-plus-rls.md`. The short version:

- Shared database, shared schema. Every tenant-owned table has `TenantId` and implements `ITenantOwned`.
  Its DbContext derives from `TenantAwareDbContext`, which adds the tenant query filter, stamps
  `TenantId` on insert, and rejects writes without a tenant or with another tenant's id. An entity
  that has a `TenantId` but is platform catalog data must be marked `[TenantCatalog]`
  (architecture tests enforce both).
- **Two isolation layers, both mandatory:** EF Core global query filters AND PostgreSQL row-level
  security. Neither is optional because the other exists.
- The tenant is resolved from the **request host** through the tenant catalog (`Tenants`,
  `TenantDomains`). Never from a header, query string, or request body.
- Code with no resolved tenant (background work) sees **zero** tenant-owned rows. Background work that
  needs tenant data runs **explicitly as a named tenant** through `TenantScopeRunner.RunAsTenantAsync`,
  one tenant at a time (the outbox dispatcher walks the tenant catalog).
- The application database role is not a table owner, not a superuser, and has no `BYPASSRLS`.
- `IgnoreQueryFilters()` is allowed only inside Kernel tenancy code, and always with a comment
  explaining why.
- Never hardcode a tenant id or branch on a specific tenant anywhere.
- `tenant.id` is on every log scope, span, and metric once resolved.

## 6. Hand-write boundary (critical)

Nika writes these parts himself, for learning. **Do not implement them**: not as an example, not in
comments, not in chat, not in a PR, unless Nika explicitly asks for a hint.

| Milestone | Nika hand-writes |
|---|---|
| M0 | `TenantResolutionMiddleware`, `TenantSessionInterceptor`, the RLS policy SQL in the `AddRowLevelSecurity` migration |
| M1 | Hold concurrency, the payment state machine |
| M2 | Offline check-in sync, duplicate-scan conflicts |
| M3 | Rate limiting under load |
| M4 | API and widget versioning |
| M5+ | Agent tool design |

Agents may write the tests, interfaces, stubs (`throw new NotImplementedException()` plus
`// TODO(nika)`), and learning guides in `docs/hand-write/`. Hand-write contract tests are the only
tests allowed to be skipped, and only with `Skip = "HAND-WRITE: Nika"`.

When Nika asks for help, give hints in increasing detail: the concept first, then the approach, and
code only if he explicitly asks for code.

## 7. Pull requests

- One PR per deliverable, small enough to review in one sitting.
- Branches `m<milestone>/<number>-<slug>` for milestone work, otherwise `feat/`, `fix/`, `chore/`, `docs/`.
- Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`, `ci:`).
- Before opening a PR: build, format, run all tests, run the reviewer (`.claude/agents/reviewer.md`
  or equivalent), and fix every blocking finding.
- The PR description states what changed, why, how it was tested, and which lines the human reviewer
  should examine closely. Fill in `.github/pull_request_template.md`.
- A new dependency is justified in the PR description: purpose, alternatives considered, license.
  Prefer Microsoft or well-maintained, widely used libraries.

## 8. Never

- Commit secrets, or connection strings containing real credentials.
- Skip or disable tests to make CI pass. `HAND-WRITE` markers are the only allowed skips.
- Use `IgnoreQueryFilters()` outside Kernel tenancy code, or without a comment explaining why.
- Hardcode tenant ids or branch on a specific tenant.
- Add features beyond the current milestone.
- Push to `main`.
- Change a decision in `docs/adr/` silently. Propose a new ADR and ask.

When blocked or unsure about **security, tenancy, or money**: stop and ask with concrete options.
Do not guess.

## 9. Working with Nika

- He is a senior engineer (banking domain, OutSystems lead) building .NET depth. Skip generic
  concepts he owns; explain the non-obvious .NET-specific choices in code and PR descriptions.
- Always state the tradeoff and when a pattern is the wrong choice.
- Be direct. Cite sources for factual claims. No filler. If you made a mistake, say so and fix it.
- Nika chooses per task whether an agent implements or coaches. When unclear, ask once.
