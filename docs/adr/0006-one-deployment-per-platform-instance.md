# 0006. One deployment per platform instance

- **Status:** Accepted
- **Date:** 2026-09-28

## Context

Nika wants to build **separate ticketing platforms for different clients**, each tailored to that
client's requirements:

- A venue chain gets a platform with its few venues as organizers.
- A festival gets a platform with a single organizer.
- A client with special needs gets extra features, or custom code if needed.
- Within one platform, organizers are separate, and the platform's owner can add organizers.

Nika and the client may both operate a platform; the split is not decided yet.

The code so far (ADR 0002, ADR 0003) already makes **one** deployment serve many organizers,
isolated from each other. The question is what happens one level up, where clients differ from each
other.

Terms used from here on:
- **Instance:** one client's platform. It is a deployment of this code with its own settings.
- **Organizer:** a tenant inside an instance, exactly as today.

## Options

| | 1. One deployment per instance | 2. One shared deployment, with instances as a level above organizers |
|---|---|---|
| Per-client features and custom code | Configuration, feature toggles, and packs enabled per instance | Feature flags in shared code for every difference |
| Isolation between clients | Separate database and runtime | Row-level security only |
| Cost | Hosting per instance (several small instances can share one PostgreSQL server, each with its own database) | One bill |
| Upgrades | One release rolled out to every instance, which needs automation | Deploy once |
| Change to existing code | None: tenancy inside an instance stays as built | A second tenancy level, and a rework of ADR 0003's design |

## Decision

1. **Each instance is its own deployment**, with its own database, configuration, domains, and
   secrets. Inside an instance, organizers are tenants, exactly as in ADR 0003.
2. **One codebase and one container image for every instance.** A release is built once and rolled
   out to all instances. Instances never run forked code.
3. **Instances differ only through:**
   - **Configuration:** name, branding, limits, provider settings.
   - **Feature toggles,** per instance, for optional behaviour inside a pack.
   - **Enabled packs.** Every pack is compiled into the image; an instance's configuration says
     which are switched on.
4. **Custom code for one client is a pack** (for example `Packs.<Capability>`), enabled only in
   that client's configuration. It is never a branch on the client's name inside shared code. A
   custom pack should be written as a capability another client could switch on later.
5. **Roles inside an instance:** the instance owner can add, configure, and suspend organizers.
   Organizer staff manage their own organizer only. Buyers are unchanged. Nika can operate every
   instance through the deployment tooling. Whether clients also get self-service access to that
   tooling is left open.

## Consequences

- **Tenancy code:** no change. The row-level security, session interceptor, and host resolution
  being written in M0 isolate organizers inside each instance.
- **Composition (ADR 0002, rule 5):** the host still references every pack. It registers only the
  packs the instance's configuration enables. An architecture or startup check must fail fast on an
  unknown pack name.
- **Persistence (ADR 0005, still Proposed):** under option A, one migration stream creates every
  pack's tables in every instance's database, including disabled packs (their tables stay empty).
  This is simple and keeps every instance's schema identical. If that ever matters, it is a reason
  to revisit option B.
- **Migrations must be safe while instances upgrade one by one:** every schema change is
  backward-compatible with the previous release (expand first, contract in a later release).
- **Infrastructure (M1):** the Azure infrastructure-as-code takes an instance's parameters (name,
  domains, enabled packs, sizes), so a new instance is one command. Each instance has its own
  Key Vault secrets. Instance settings live in the repository, except secrets.
- **Identity ADR (M1):** now covers three populations (instance owner, organizer staff, buyers),
  not two.
- **Operations:** releases roll out across all instances from one pipeline (M1: manual for a single
  instance; automated once there are several). Telemetry carries an instance name as well as
  `tenant.id`.
- **Costs** grow with the number of instances. Small instances share a PostgreSQL server; a client
  that needs full isolation gets its own.
- **Revisit when:** there are many small instances with identical settings. A shared multi-instance
  deployment for that segment (option 2) may then pay off, with dedicated instances kept for large or
  custom clients.
- **When this is the wrong choice:** thousands of self-serve clients with no customization, where
  per-instance hosting cost dominates. That is not this business.
