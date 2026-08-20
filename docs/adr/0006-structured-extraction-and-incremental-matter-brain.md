# ADR 0006: Provider-neutral structured extraction and incremental Matter Brain merge

- Status: Accepted
- Date: 2026-08-20

## Context

ADR 0005 produces exact, versioned `SourceSpan` records from verified immutable evidence. CaseMesh now needs to turn explicitly selected spans into structured people, organisations, communications, assertions and events without treating probabilistic output as documentary truth. Reprocessing must preserve old model output and corrections, avoid rebuilding an entire Matter, and retain ADR 0002 tenant isolation.

## Decision

Add the platform-neutral `CaseMesh.MatterBrain` project above `CaseMesh.Core`. Core contains the reusable canonical `Person`, `Organisation`, `EntityAlias` and neutral `Communication` records plus append-only assertion correction behavior. Core has no model-vendor, PostgreSQL, storage or CaseMesh Live dependency.

`IStructuredExtractionProvider` is the commercial model boundary. A request contains only the explicitly selected tenant/Matter source spans and their text digests. Provider output is a typed candidate batch. The merge service validates its schema, enum values, confidence range and exact source allowlist before a candidate can reference canonical state. Selected span count/UTF-8 bytes and candidate count/aggregate bytes are bounded before provider or canonical mutation. Provider/model, extraction/prompt/schema versions, an explicit-clock timestamp, the selected source-span IDs and a SHA-256 digest of the bounded raw structured result are retained.

Model output first becomes an `ExtractionCandidateRecord`. Valid candidates may then create canonical records with `NotReviewed`/`Candidate`/`Unverified` state. Rejected candidates remain queryable with a typed rejection code and cannot create canonical records. Documentary assertions require exactly one selected source span. `AiInference` uses the paired AI origin/assertion classes, has no documentary source, and retains model identity only as inference provenance. Extraction confidence is never used as truth confidence.

Candidate payload metadata is schema-validated typed JSON, bounded to 1 MB and explicitly non-authoritative. Canonical Matter state and all ownership/provenance relationships remain relational; there is no opaque serialized Case Brain aggregate.

## Entity resolution and event semantics

Aliases use deterministic whitespace/case normalization. A unique exact normalized alias can reuse an existing same-Matter entity. Similar names never auto-merge. Probabilistic entity matches create proposals with score and evidence spans; accept, reject and reverse are explicit append-only actions. Accepting creates a current identity redirect without deleting either entity or its aliases. Reversal deterministically restores the prior identity view.

Communications are neutral containers. Events remain candidate epistemic containers and retain alleged ranges rather than inventing a precise date. Assertion/event links coexist, including conflicting assertions about one event.

Rule-assisted contradiction generation compares assertions with the same normalized subject, predicate and alleged time. Different numeric values create a numeric-mismatch candidate; other incompatible values create a direct-conflict candidate. Both assertions remain. The result is not a legal conclusion or a truth score.

## Incremental merge and corrections

Extraction fingerprints use unambiguous canonical JSON containing provider/model and extraction/prompt/schema versions plus the exact selected source IDs. Retrying one fingerprint is idempotent and does not call the provider again. A changed version creates a new immutable run and candidates. Only active dependencies rooted in the reprocessed source spans receive append-only invalidation records; unrelated dependencies remain active. Dependencies may also target downstream `AnalysisNode` records so a source correction invalidates the dependent current analysis view without deleting its history.

`Confirmed`, `Rejected` and `NeedsContext` reviews append an `AuditEvent`. A corrected assertion creates a replacement, marks the old record superseded/rejected, preserves the original source and model history, copies historical event relationships to the replacement, and records replacement dependencies. Rejected/superseded assertions dismiss their unresolved contradiction current view; historical contradiction rows remain. Events supported only by rejected assertions become rejected without deleting their links.

## PostgreSQL persistence and tenancy

Migration `0004_matter_brain.sql` adds relational tables for people and roles, organisations, aliases, communications and participants/sources, extraction runs and selected sources, candidates and sources, canonical dependencies and invalidations, and entity-resolution actions and evidence.

Every row carries `(tenant_id, matter_id)`. Composite foreign keys bind sources, candidates, canonical targets, dependencies and identity actions to the same tenant/Matter. A dependency must use the candidate's extraction run and one of that candidate's cited source spans. Polymorphic canonical references use separate nullable typed foreign-key columns plus consistency checks instead of an unchecked generic UUID. All tables use `RLS` and `FORCE ROW LEVEL SECURITY` with the ADR 0002 transaction-local tenant context. Canonical and model-history rows reject update, delete and truncate except database-driven whole-Matter/tenant privacy cascades. Restricted runtime roles receive only `SELECT` and `INSERT` during upgrade.

`PostgresMatterBrainStore` persists the existing evidence/workplace snapshot and Matter Brain snapshot in one PostgreSQL transaction. Rehydration reruns domain validation and rejects undefined enums, duplicate identities, invalid hashes, missing sources/runs/canonical records, inconsistent dispositions, cross-Matter identities and invalid merge/reversal history.

## Security and privacy

Evidence text is supplied only to the selected provider call and remains inert untrusted data. It is not interpreted as instructions or tool authorization. Ordinary logs contain neither evidence bodies nor raw model results. Provider secrets are not part of Core, candidates, PostgreSQL rows or source control. Synthetic fixtures are used in tests.

## Consequences and deferred work

The initial implementation uses deterministic fake/golden providers in CI; no live model adapter or API secret is required. The application boundary is ready for a future provider after privacy and deployment review.

Legal-authority RAG, statutory deadlines, legal merits/liability/win or compensation scoring, autonomous filing, web/API/auth/billing, mailbox integration, pgvector retrieval and CaseMesh Live are deliberately out of scope. Professional handover/export is the next milestone and consumes this canonical source-linked state without becoming its source of truth.
