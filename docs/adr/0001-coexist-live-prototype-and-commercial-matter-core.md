# ADR 0001: Coexistence of CaseMesh Live and the commercial Matter core

- Status: Accepted
- Date: 2026-08-13

## Context

The repository contains a buildable Windows-only WPF/SQLite CaseMesh Live prototype. Its meeting orchestration, local retrieval and audio projects support the existing live-assistance research path, but its meeting-focused model is not the intended root for the commercial evidence product.

The commercial architecture needs a generic `Matter` ownership boundary, immutable evidence identities, source spans, attributed assertions, events, contradictions, AI analysis and append-only audit history. The first commercial milestone must introduce that domain without a destructive migration of the prototype or its public interfaces.

## Decision

The generic Matter/evidence domain is added to `CaseMesh.Core` alongside the existing `CaseFact`, meeting and transcript models.

- The current WPF application, Windows audio implementation and SQLite repository remain buildable and unchanged in this milestone.
- New commercial code depends on the generic Matter/evidence core, not on `MeetingState`, `LiveMeetingCoordinator` or other live-meeting orchestration types.
- The existing prototype does not yet persist the new domain. A later commercial persistence milestone will use PostgreSQL as the system of record and private object storage for immutable originals.
- Relational link tables, full-text search and pgvector are the initial commercial graph/retrieval approach. A dedicated graph database is not introduced unless measured traversal requirements justify it.
- A future commercial web/API stack will reference the generic Matter/evidence core. It will not reuse WPF views or make the meeting model the commercial aggregate root.
- CaseMesh Live may later ingest transcripts and candidate assertions through the same Matter provenance and correction rules, but no new live functionality is part of this decision.

## Consequences

The repository temporarily contains two coexisting domain surfaces: the preserved single-user Live prototype and the new commercial Matter evidence model. This deliberate compatibility compromise avoids an unsafe schema rewrite and keeps existing meeting behaviour testable while the commercial domain matures.

There is no PostgreSQL schema, object-storage integration, authentication, tenancy persistence or web UI in this milestone. Those concerns remain later migrations and must not leak infrastructure requirements into the generic core.
