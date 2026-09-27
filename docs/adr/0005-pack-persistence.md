# 0005. How packs persist data

- **Status:** Proposed (Nika decides before any M1 code)
- **Date:** 2026-09-27

## Context

M1 is the first milestone where a pack (Ticketing) stores its own data: events, holds, orders,
payments. Three existing rules meet here:

- **Boundary rule 1** (ADR 0002): a pack references `Kernel.Contracts` only, so it cannot derive from
  `TenantAwareDbContext`, which lives in `Kernel`.
- **Tenancy** (ADR 0003): every tenant-owned table needs the EF Core query filter, the write checks,
  and row-level security. An architecture test now rejects any plain `DbContext`, so "just add a
  context in the pack" fails the build.
- **The outbox:** a handler's database work and its receipt must commit in **one** transaction, or a
  crash between them repeats the handler. Today only `PlatformDbContext` work gets that guarantee,
  and pack handlers are told to treat every effect as possibly repeated (AGENTS.md section 5).

The question: where do pack entities get mapped, which context do pack handlers write through, and
where do pack migrations live?

## Options

### A. One Kernel-owned context; packs contribute their mapping (recommended)

- `PlatformDbContext` stays the only context. Each pack keeps its EF Core mapping in its own
  assembly, as `IEntityTypeConfiguration<T>` classes in a `Platform.Packs.<Pack>.Persistence`
  namespace (the domain namespace stays free of EF Core, rule 4).
- A pack declares its assembly in `IModule.Register`, through a registration record in
  `Kernel.Contracts` that holds only an `Assembly`. This is the same pattern as `AddDomainEvent`, and
  it adds no EF Core type to `Kernel.Contracts`. `PlatformDbContext` applies the configurations of
  every declared assembly.
- Pack code works through EF Core's `DbContext` base type, which is registered in DI as the scoped
  `PlatformDbContext`. A pack cannot name another pack's entity types, so it cannot query them
  (rule 1 already forbids the reference).
- Migrations move from `Kernel` to a new `src/Host/Platform.Migrations` project, which references
  the Kernel and every pack. `Kernel` cannot hold them any more, because they now describe pack
  tables and `Kernel` never references a pack (rule 2).
- **Outbox:** a pack handler writes through the same scoped context the dispatcher saves the receipt
  with, so its work and its receipt commit together. This already works today.

### B. A persistence base assembly; one context per pack

- Move `TenantAwareDbContext` and the outbox mapping into a new `Kernel.Persistence` project that
  packs may reference. Each pack owns a context deriving from it, with its own migrations and its own
  history table.
- **Outbox:** the dispatcher opens the connection and transaction and hands them to the handler's
  scope. Each pack context joins them (`Database.UseTransaction` on the same `DbConnection`). This is
  plumbing the Kernel must own and test, and every pack must get it right.
- Several migration histories against one database: their order, and row-level security for each
  pack's tables, are coordinated by hand.

### C. One context per pack, no shared transaction

- As in B, but handlers never share the receipt's transaction. Every handler effect is at least once,
  keyed on the message id, forever. This is today's stopgap made permanent.

## Comparison

| | A. One context | B. Context per pack, shared transaction | C. Context per pack, at least once |
|---|---|---|---|
| Handler work atomic with its receipt | Yes, already works | Yes, with Kernel plumbing | No |
| Migration streams | One | One per pack, plus Kernel | One per pack, plus Kernel |
| Row-level security for new tables | One place | Per pack | Per pack |
| Boundary rule changes | Rule 5 (a second project references packs), plus the model registration in Contracts | Rule 1 (packs reference `Kernel.Persistence`) | Rule 1 |
| Pack isolation in the data layer | By compile-time references only (one shared model) | Separate models | Separate models |
| Extracting a pack into a service later | Split the model and migrations first | Easier | Easier |
| Effort for one part-time developer | Lowest | Highest | Medium, and the cost is paid in every handler |

## Proposed decision

**Option A.** ADR 0002 already chose one deployable with local transactions. One context keeps
transactions, migrations and row-level security in one place, and it makes handler receipts atomic
with no new mechanism. B's isolation benefits pay off only when a pack is extracted, which ADR 0002
says is not expected. C makes every future handler harder to write correctly.

## Consequences if accepted

- **Rule 5** changes to "The host and `Platform.Migrations` are the only projects that reference
  every pack; the host is the only composition root." The architecture tests change to match.
- `Kernel.Contracts` gains the model-contribution registration (an `Assembly` in a record, no EF Core
  types).
- The migrations and the design-time factory move to `Platform.Migrations`, and CI's model-parity
  step targets that project. Migration ids do not change, so databases that already applied them are
  unaffected. The move is its own PR, before any pack entity.
- The hand-write row-level security helper, if Nika writes one (step C2 in
  `docs/hand-write/m0-tenancy-steps.md`), is used by every migration that creates a pack table.
- AGENTS.md section 5 drops the "treat every handler effect as possibly repeated" stopgap for work
  done through the shared context. Effects outside the database (an email, a payment call) stay at
  least once and are still keyed on `context.MessageId`.
- **Revisit when:** a pack is about to be extracted into its own service, or two packs need
  conflicting EF Core configuration (for example, different conventions).
