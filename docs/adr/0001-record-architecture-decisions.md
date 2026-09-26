# 0001. Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

The platform is built part-time (under 10 hours a week) by one owner, with AI agents doing much of the
implementation and a second human reviewer. Agents start every session without memory of earlier
reasoning. Decisions that live only in chat or in someone's head get silently reversed.

## Decision

Record every significant architecture decision as a numbered Markdown file in `docs/adr/`, using the
format of Michael Nygard's "Documenting Architecture Decisions"
(https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions): context, decision,
consequences.

- Numbers are sequential and never reused. Files are named `NNNN-short-title.md`.
- An accepted ADR is not edited to change its decision. A new ADR supersedes it, and the old one's
  status becomes `Superseded by NNNN`.
- Agents that disagree with a decision write a `Proposed` ADR and ask. They never change the code
  first.

## Consequences

- Reviewers and agents can check a change against a written decision instead of memory.
- Small cost per decision. Not every choice needs an ADR: only those that are hard to reverse, cross
  module boundaries, or affect tenancy, security, or money.
