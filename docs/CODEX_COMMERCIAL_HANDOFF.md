# Codex Commercial Handoff

Date: 2026-08-13

## Current state

The repository, solution, projects and namespaces have already been renamed from HRCompanion to **CaseMesh**. Treat that migration as complete. Do not spend this batch renaming code and do not reintroduce old identifiers except deliberate backwards-compatible migration constants/tests.

## Read first

1. `AGENTS.md`
2. `docs/BRAND_AND_SCOPE.md`
3. `docs/RESEARCH_BASELINE_2026-08-13.md`
4. `docs/COMMERCIAL_MASTER_PLAN.md`
5. `docs/CASE_BRAIN_SPEC.md`
6. `docs/PRODUCT_VALIDATION_AND_GTM.md`
7. existing `docs/ARCHITECTURE.md` and `docs/STATUS.md` only for the preserved CaseMesh Live prototype

## Objective

Implement the first commercial engineering milestone: a correct, generic **Matter-root evidence/epistemic domain** with deterministic invariants and synthetic workplace-dispute tests, while keeping the existing CaseMesh solution and live prototype green.

Do not build the web app, PostgreSQL, authentication, billing or new live-meeting functionality in this batch.

## First implementation batch

Create a new reviewable branch from current `main` and implement only the following.

### 1. Add the generic Matter root

Add a `Matter` aggregate/reference type that is not hard-coded to employment law. It must be suitable as the ownership boundary for evidence entities added in this batch.

Do not replace the legacy meeting-focused `CaseFact` model destructively yet. New commercial types should coexist with the existing prototype until a later migration is proven safe.

### 2. Add v2 evidence-domain types

Minimum generic types:

- `EvidenceOriginClass`
- `AssertionClass`
- `DisputeState`
- `IntegrityState`
- `VerificationState`
- `SourceSpan`
- `Assertion`
- `MatterEvent`
- `AssertionEventLink`
- `Contradiction`
- `AnalysisNode`
- `AuditEvent`

Use `MatterId`/Matter ownership consistently. Employment-specific fields do not belong in these core primitives.

### 3. Encode deterministic invariants

Add validation/domain services so tests prove at least:

- a source-backed assertion requires a valid source span;
- a source span references an immutable document/document-version identity;
- an AI inference cannot masquerade as original documentary evidence;
- a contradiction retains both assertion ids and does not delete either side;
- user correction produces an audit event rather than silent replacement;
- superseded/rejected extraction remains traceable;
- Matter ownership cannot be crossed by links between the new entities;
- extraction confidence is not represented as a truth score.

No LLM/API call is required for these invariants.

### 4. Add synthetic workplace fixtures and tests

Use synthetic data only. Required scenarios:

- employer states 12 sickness days while a separate attendance record indicates 10;
- user correction changes an extracted event date and leaves an audit trail;
- two contradictory assertions remain simultaneously queryable;
- AI inference is separate from evidence records;
- an assertion claiming source backing without a valid source span is rejected;
- a cross-Matter assertion/event/source link is rejected;
- duplicate immutable document versions can share the same content hash without creating two logical originals in later persistence design assumptions.

The fixture is workplace-specific; the underlying core types should remain generic.

### 5. Add an ADR

Create an ADR under `docs/adr/` describing the migration architecture:

- current WPF/SQLite CaseMesh Live prototype remains buildable;
- new commercial domain is introduced alongside it;
- later commercial persistence moves to PostgreSQL/object storage;
- no dedicated graph database is required initially;
- the commercial web/API stack must depend on the generic Matter/evidence core rather than the meeting orchestration model.

## Validation required

Before completing the batch:

```powershell
dotnet restore .\CaseMesh.slnx
dotnet build .\CaseMesh.slnx -c Release --no-restore
dotnet test .\CaseMesh.slnx -c Release --no-build
```

Also:

- inspect the full diff;
- run all new evidence-domain tests;
- confirm existing live/meeting tests remain green;
- report exact test counts and build warnings/errors;
- report any deliberate compatibility compromise.

## Stop conditions

Stop and report instead of improvising if:

- existing code unexpectedly requires destructive model/schema changes;
- new commercial types cannot coexist without breaking the current public interfaces substantially;
- a change would delete or disable the existing live prototype;
- real personal case material appears in the repository;
- implementation requires an unresolved provider, hosting or regulatory decision.

## Acceptance criteria

This batch is complete when the repository has a compile-safe generic Matter evidence model whose invariants are demonstrated by deterministic tests and the existing application still builds/tests successfully.

Do not proceed to PostgreSQL or web UI in the same PR.

## Next batch after acceptance

1. employment-specific extension records required by the first vertical;
2. tenancy-aware PostgreSQL persistence;
3. immutable original/object-storage document model;
4. source-span ingestion pipeline;
5. Matter Brain merge/correction engine;
6. web MVP;
7. professional export;
8. matter-grounded Q&A.
