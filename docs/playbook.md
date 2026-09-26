# Playbook

The one-page version of how this platform is built. Rules for agents: `AGENTS.md`. Reasons: `docs/adr/`.

## Product

A multi-tenant SaaS for event organizers. Each organizer (tenant) sells tickets from its own
white-label storefront, resolved by host name. Buyers check out as guests by default and pay the
organizer directly on the payment provider's hosted page. No paying client yet; a real pilot event is
targeted after M2. Other verticals (e-commerce, building development) may become packs later on the
same kernel; they are not built now.

## Fixed architecture decisions

| # | Decision | ADR |
|---|---|---|
| 1 | Modular monolith, one deployable. No microservices, no message broker. | 0002 |
| 2 | Kernel + packs. Packs reference `Kernel.Contracts` only; never the Kernel implementation or another pack. | 0002 |
| 3 | Shared database and schema; `TenantId` on every tenant-owned table. EF Core global query filters **and** PostgreSQL RLS, both mandatory. | 0003 |
| 4 | PostgreSQL. | 0003 |
| 5 | Tenant resolved from the request host via a tenant catalog. | 0003 |
| 6 | Payments go directly to each organizer's merchant account through a hosted payment page. The platform never touches card data or holds funds. Per-tenant gateway credentials in Azure Key Vault (M1+). Tickets are issued only after the gateway status call confirms payment. | 0004 |
| 7 | Domain events through a transactional outbox and an in-process background worker, which processes one tenant at a time. | 0002, 0003 |
| 8 | Tenant id on every log, trace, and metric (OpenTelemetry). | 0003 |
| 9 | Two identity populations: organizer staff (OIDC, tenant-scoped roles, M1+) and buyers (guest checkout by default). | Settled; ADR due in M1 |
| 10 | Frontend (M1+): Next.js storefront and organizer dashboard in `/web`; embeddable widget in M4. | Settled; ADR due in M1 |
| 11 | Hosting: Azure Container Apps + Azure Database for PostgreSQL Flexible Server, from M1. | Settled; ADR due in M1 |

Changing any of these needs a new ADR, proposed and approved before the code changes.

## Milestones

| # | Delivers | Nika hand-writes |
|---|---|---|
| M0 | Foundation, agent setup, CI gates, kernel skeleton | Tenant resolution middleware, tenant session interceptor, RLS policies |
| M1 | Event, GA and seated holds, hosted checkout, email ticket with QR; first Azure deployment | Hold concurrency + payment state machine |
| M2 | Offline check-in web app, organizer dashboard, refunds | Offline sync + duplicate-scan conflicts |
| M3 | On-sale queue, per-buyer limits, load test | Rate limiting under load |
| M4 | Widget, custom domains, publishable keys | API and widget versioning |
| M5+ | Self-serve AI agent, dedicated tier, other verticals | Agent tool design |

## M0 deliverables (one PR each)

1. Agent and review setup: `AGENTS.md`, `CLAUDE.md`, subagents, CODEOWNERS, PR template, this playbook, ADRs 0001-0004.
2. Repository foundation: solution and projects, central package management, analyzers, Docker Compose, Dockerfile, health endpoints.
3. CI: build, format, unit, integration, and architecture tests; vulnerable-package and secret scanning.
4. Architecture tests for the boundary and tenancy rules.
5. Tenancy scaffolding plus the hand-write contract (stubs, skipped contract tests, `docs/hand-write/m0-tenancy.md`).
6. Transactional outbox (6a) and OpenTelemetry (6b), split into two PRs so each is reviewable in one sitting.
7. Payments contract (`IPaymentGateway`, `Money`), with a fake gateway in tests only.

**M0 is done when** all seven are merged with CI green on `main`, the skipped `HAND-WRITE` tests are
listed in `docs/hand-write/m0-tenancy.md`, anyone can clone and run everything from `AGENTS.md`, and
there is no M1 feature code.
