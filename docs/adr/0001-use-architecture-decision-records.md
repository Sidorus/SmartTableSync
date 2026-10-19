# ADR-0001: Use Architecture Decision Records

## Status
Accepted

## Date
2026-08-04

## Context
This project's design rationale - why the engine is shaped the way it is, what was tried and
rejected along the way - has accumulated across the [README](../../README.md), inline code
comments, and private working notes, but with no single place that answers "what did we
decide, and why" as a scannable list. That's fine for someone actively building a feature, but
poor for a new contributor, or a portfolio reviewer, trying to understand the reasoning behind
the architecture quickly.

## Decision
Adopt Architecture Decision Records (ADRs) as an index of the project's significant decisions,
one file per decision, numbered sequentially, in `docs/adr/`. Each ADR is short - context,
decision, alternatives considered, consequences - and self-contained rather than depending on
external notes. Most of the ADRs in this initial set document decisions already made and
shipped; new ADRs get added going forward when a new significant decision is made, not
retrofitted after the fact from then on.

## Alternatives Considered
- **Keep relying on the README and code comments only.** Rejected - the README is right for
  "what this does and how to run it," and code comments are right for a specific implementation
  detail, but neither is organized around "what did we decide, and why," which needs its own
  index to be findable at all.
- **One big "decisions" document.** Rejected - a single growing file is harder to link to
  precisely and harder to mark `Superseded` cleanly than one file per decision.

## Consequences
**Positive:** a future reader can scan `docs/adr/README.md`'s index and find any major decision
in seconds; superseding a decision later is a clean, linkable operation (add a new ADR, mark the
old one `Superseded`) instead of editing prose in place and losing the history of what changed.

**Negative:** another place to keep in sync - a decision that changes materially should get a
new ADR (or a status update on the existing one), or this index rots exactly the way it was
meant to prevent.
