# 0002. Modular monolith: a kernel plus vertical packs

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

The first product is event ticketing, but the same owner intends to host other verticals later
(e-commerce, building development). The team is one part-time developer plus AI agents. There is no
paying client yet, so operational cost and delivery speed matter more than independent scaling.

The predecessor project (`ticketing-platform`, tag `v4-reference`) used RabbitMQ between modules of a
single process. It worked, but the broker added a service to run, a topology to maintain, and failure
modes (publisher confirms, dead-letter queues, redelivery) without buying independent deployment.

## Decision

1. **One deployable.** A modular monolith, no microservices.
2. **Kernel + packs.**
   - `Kernel.Contracts`: the types packs are allowed to see (tenancy, domain events, payments,
     money). It references nothing in the solution.
   - `Kernel`: implementations of those contracts (persistence, tenancy, outbox, telemetry).
   - `Packs/<Vertical>`: one project per vertical. A pack references `Kernel.Contracts` only, never
     the `Kernel` implementation and never another pack.
   - `Host/Platform.Api`: the only composition root; it wires the kernel and packs together.
3. **No message broker.** Domain events go through a transactional outbox and an in-process
   background worker (details in ADR 0003 for tenancy, and the M0 outbox PR).
4. The rules are enforced by architecture tests in CI, not by convention.

## Consequences

- **Good:** one thing to build, deploy, debug, and trace. Transactions stay local. Pack boundaries are
  real (the compiler and tests enforce them), so extracting a pack into a service later is possible
  without a rewrite: the outbox is already the seam.
- **Bad:** packs cannot scale or deploy independently. A pack that needs something from another pack
  must go through a contract in `Kernel.Contracts` or a domain event, which is more ceremony than a
  direct call.
- **Revisit when:** a pack needs a genuinely different scaling profile, or a separate team owns one
  pack. Neither is true today.
- **When this is the wrong choice:** multiple teams deploying independently at high frequency, or
  modules with conflicting runtime requirements. Not this project.
