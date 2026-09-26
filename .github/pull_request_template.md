## What and why

<!-- What changed, and the problem it solves. Link the milestone item or ADR. -->

## How it was tested

<!-- Commands run and their results. Say what was NOT tested. -->

## Look closely at

<!-- The lines a human reviewer should examine carefully, with file:line references. -->

## Checklist

- [ ] Builds with zero warnings; `dotnet format --verify-no-changes` passes
- [ ] Tests added or updated; all tests pass locally
- [ ] No skipped tests except `HAND-WRITE: Nika` contract tests
- [ ] Tenancy impact considered: new tenant-owned tables implement `ITenantOwned`, no
      `IgnoreQueryFilters()` outside Kernel tenancy code, no hardcoded tenant ids
- [ ] Boundary rules respected (packs reference `Kernel.Contracts` only)
- [ ] Hand-write boundary respected (`AGENTS.md` section 6)
- [ ] New dependencies justified below (purpose, alternatives, license), or none added
- [ ] No secrets or credentialed connection strings committed
- [ ] Reviewer subagent run; blocking findings fixed

## New dependencies

<!-- Package, purpose, alternatives considered, license. Or "None". -->
