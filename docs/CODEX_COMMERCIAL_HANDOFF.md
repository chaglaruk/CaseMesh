# Codex Commercial Handoff

Date: 2026-08-13

## Read first

1. `AGENTS.md`
2. `docs/RESEARCH_BASELINE_2026-08-13.md`
3. `docs/COMMERCIAL_MASTER_PLAN.md`
4. `docs/CASE_BRAIN_SPEC.md`
5. `docs/PRODUCT_VALIDATION_AND_GTM.md`
6. existing `docs/ARCHITECTURE.md` and `docs/STATUS.md` for the legacy Windows prototype

## Objective

Evolve the repository from a narrow single-user Windows meeting prototype into a commercial evidence-platform foundation **without destroying working prototype code**.

The first engineering milestone is not a web redesign and not live meeting capture. It is a correct v2 epistemic domain with source provenance and tests.

## First implementation batch

Create a reviewable branch from the latest commercial-strategy baseline and implement only the following.

### 1. Add v2 evidence-domain types

Add new domain types rather than mutating `CaseFact` destructively at first.

Minimum types:

- `EvidenceOriginClass`
- `AssertionClass`
- `DisputeState`
- `IntegrityState`
- `VerificationState`
- `SourceSpan`
- `Assertion`
- `CaseEvent`
- `AssertionEventLink`
- `Contradiction`
- `AnalysisNode`
- `AuditEvent`

Use names/namespaces that make the new model clearly distinct from the legacy meeting-focused model.

### 2. Encode invariants

Add deterministic validation/services so these conditions can be tested:

- source-backed assertion requires a valid source span;
- source span references a document/version identifier;
- AI inference cannot masquerade as original documentary evidence;
- contradiction retains both assertion ids;
- user correction produces an audit event rather than silent replacement;
- superseded/rejected extraction remains traceable;
- no numeric truth score is introduced.

### 3. Add synthetic fixtures and tests

Create synthetic employment-dispute evidence fixtures only.

Required scenarios:

- employer states 12 sickness days while a separate attendance record indicates 10;
- user correction changes an extracted event date;
- duplicate document versions share the same content hash;
- AI inference is presented separately from evidence;
- an assertion with a missing source span is rejected as source-backed;
- contradictory assertions remain queryable simultaneously.

### 4. Produce an architecture note

Add an ADR describing how the current SQLite/WPF prototype will coexist with the future commercial API/PostgreSQL/web stack during migration.

Do not add PostgreSQL, web UI, authentication or cloud deployment in this first batch unless required only for compile-safe interfaces.

## Validation required

Before completing the batch:

- inspect the full diff;
- build the solution in Release;
- run all existing tests;
- run all new evidence-domain tests;
- confirm legacy meeting tests remain green;
- report any deliberate compatibility compromises.

## Stop conditions

Stop and report instead of improvising if:

- existing code has an undocumented dependency that requires destructive schema/model changes;
- the new types cannot coexist without breaking public interfaces substantially;
- a change would delete or disable the existing meeting prototype;
- real personal HR data appears in the repository;
- implementation would require deciding an unresolved provider/compliance question.

## Later batches

After the epistemic core is accepted:

1. tenancy-aware PostgreSQL persistence;
2. immutable object/document version model;
3. source-span ingestion pipeline;
4. Case Brain merge/correction engine;
5. web MVP;
6. professional export;
7. case-grounded Q&A.

Do not skip directly to the later batches because they look more visible or impressive.
