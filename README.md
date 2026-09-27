# Platform

Multi-tenant SaaS kernel with vertical packs. The first pack is event ticketing: organizers sell
tickets from their own white-label storefront, and buyers pay the organizer directly through the
provider's hosted payment page.

Each client gets its own instance: a separate deployment of the same code, with one or more
organizers, tailored by configuration, feature toggles, and enabled packs (ADR 0006).

Status: **M0 (foundation) in progress.** How it is built: `docs/playbook.md`. How to build, run, and
test it: `AGENTS.md` section 2.

Reference system: [ticketing-platform @ v4-reference](https://github.com/NikolozPapaskiri/ticketing-platform/tree/v4-reference),
the earlier learning build this platform supersedes.
