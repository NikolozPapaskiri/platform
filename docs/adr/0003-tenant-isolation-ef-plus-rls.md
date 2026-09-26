# 0003. Tenant isolation: EF Core query filters plus PostgreSQL row-level security

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Organizers are tenants and must never see each other's data. The product is **white-label**: each
organizer sells from its own host name and buyers check out as guests on that organizer's storefront.
There is no cross-organizer marketplace, so no request legitimately reads across tenants. (The
predecessor project had a marketplace, which forced five separate "access planes"; this one does not.)

Isolation options:

| Strategy | Isolation | Cost |
|---|---|---|
| Shared schema + `TenantId` column | One missing predicate leaks data | Cheapest: one schema, one migration, one pool |
| Schema per tenant | Stronger; per-tenant restore is real | Migrations times N; connection and search-path management |
| Database per tenant | Strongest; per-tenant tuning and residency | Highest operating cost |

With no paying client, shared schema is the right cost. Its weakness is that isolation depends on
every query remembering the tenant predicate.

## Decision

1. **Shared database, shared schema.** Every tenant-owned table has a non-null `TenantId`, and its
   entity implements `ITenantOwned`.
2. **Two independent isolation layers, both mandatory:**
   - **EF Core global query filters**, applied automatically by the Kernel base `DbContext` to every
     `ITenantOwned` entity. The same context stamps `TenantId` on insert and refuses a tenant-owned
     write when no tenant is resolved.
   - **PostgreSQL row-level security** on every tenant-owned table, forced so it also applies to the
     table owner, with policies that both filter reads and reject writes of another tenant's
     `TenantId`. The current tenant reaches PostgreSQL through a per-connection or per-transaction
     setting written by an EF Core interceptor, and must never leak to the next user of a pooled
     connection.

   The query filter is the convenience layer; RLS is the safety net that still holds for raw SQL, a
   forgotten filter, or `IgnoreQueryFilters()`.
3. **Database roles.** Migrations run as an owner role. The application connects as a separate role
   that owns no tables, is not a superuser, and has no `BYPASSRLS`.
4. **Tenant resolution from the request host**, through the tenant catalog: `Tenants` (Id, Slug,
   Status, CreatedAt) and `TenantDomains` (Host unique, TenantId). These catalog tables are not
   tenant-owned. Unknown host: 404. Suspended tenant: 403. The tenant never comes from a header,
   query string, or body.
5. **Two planes only.**
   - **Tenant plane:** a request resolved to exactly one tenant.
   - **System plane:** background work with no tenant. It sees **zero** tenant-owned rows. Work that
     needs tenant data runs explicitly as a named tenant, one tenant at a time. The outbox dispatcher
     therefore walks the tenant catalog and processes each tenant's outbox as that tenant.
6. **Observability:** once resolved, `tenant.id` is attached to every log scope, span, and metric.

The M0 implementations of the resolution middleware, the session interceptor, and the RLS policies
are hand-written by Nika (`AGENTS.md` section 6). This ADR records what they must guarantee, not how.

## Consequences

- **Good:** a missing predicate or a raw query no longer leaks data by itself; two layers must fail
  at once. Isolation is testable at the database level (contract tests prove RLS on its own).
- **Bad:** every connection must carry tenant context, which costs a round trip or a statement per
  transaction. System-plane work that spans tenants costs one pass per tenant (fine at pilot scale;
  revisit past a few hundred active tenants). Per-tenant point-in-time restore out of a shared schema
  is hard.
- **Revisit when:** a client contractually requires physical isolation or data residency, per-tenant
  restore is demanded, or one tenant's load justifies its own database. A hybrid (pooled by default,
  siloed for the largest) is the likely end state.
- **If a marketplace is ever added,** it needs a cross-tenant read model and a new ADR; do not weaken
  these rules to serve it.
