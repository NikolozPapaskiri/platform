# 0004. Payments go directly to each organizer through a hosted payment page

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Two broad models exist for a multi-tenant ticketing platform:

| | Platform as merchant of record | Direct to organizer |
|---|---|---|
| Money lands in | The platform's account, paid out later | The organizer's own merchant account |
| Chargeback liability | The platform | The organizer |
| Regulatory weight | Heavy: the platform holds other people's funds | Light |
| Organizer onboarding | Simple | The organizer contracts with a provider |
| Platform fee | Deducted from the float | Billed separately (or split, if the provider supports it) |

Holding funds between sale and event is the biggest financial risk in ticketing: a cancelled event
means mass refunds of money the platform may already have paid out. A one-person, part-time team
should not carry that risk or the licensing it implies.

## Decision

1. Each organizer uses **its own merchant account** with a supported provider. The platform never
   holds organizer funds.
2. Checkout redirects the buyer to the provider's **hosted payment page**. The platform never sees,
   transmits, or stores card data. This keeps the platform's own PCI DSS scope minimal; the exact
   self-assessment questionnaire depends on the integration and is confirmed per provider
   (https://www.pcisecuritystandards.org/).
3. **Per-tenant gateway credentials** live in Azure Key Vault (M1+), referenced by tenant, never
   stored in the database.
4. **Tickets are issued only after `GetStatusAsync` confirms payment** with the provider. The browser
   redirect back to the storefront is never proof of payment, since anyone can craft it.
5. **Webhook handling is idempotent.** Providers retry and duplicate webhooks; a webhook triggers a
   status check, it is not trusted as proof on its own.
6. The contract is `IPaymentGateway` in `Kernel.Contracts` (`CreateSessionAsync`, `GetStatusAsync`,
   `RefundAsync`, `ParseWebhookAsync`) with amounts as `Money` (minor units plus ISO 4217 currency).
   M0 has no real provider adapter; M1 builds the one the pilot organizer uses.

## Consequences

- **Good:** no custody of funds, minimal card-data scope, chargebacks sit with the organizer.
- **Bad:** each organizer must onboard with a provider before selling. The platform's own fee cannot
  be taken from a float; it needs separate billing or a provider that supports split payments.
  Refunds depend on the organizer's provider balance.
- **Revisit when:** the business wants to offer organizers "no provider contract needed" onboarding.
  That means becoming merchant of record (or using a marketplace/connected-accounts product), with a
  settlement ledger and payout holds. It needs a new ADR.
