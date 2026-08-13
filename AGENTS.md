# AGENTS.md — CaseMesh engineering contract

## Mission

Build a trustworthy evidence operating system for workplace disputes on top of a reusable matter-centric evidence core.

The commercial critical path is:

`evidence -> provenance -> structured Matter/Case Brain -> chronology -> dispute map -> correction -> professional handover`

The product brand is **CaseMesh** and the canonical domain is **casemesh.dev**. Read `docs/BRAND_AND_SCOPE.md` before changing product scope.

The existing Windows live-meeting copilot remains a useful later feature track. It is no longer the repository's primary product goal.

Read these before commercial implementation:

- `docs/BRAND_AND_SCOPE.md`
- `docs/RESEARCH_BASELINE_2026-08-13.md`
- `docs/COMMERCIAL_MASTER_PLAN.md`
- `docs/CASE_BRAIN_SPEC.md`
- `docs/PRODUCT_VALIDATION_AND_GTM.md`
- `docs/CODEX_COMMERCIAL_HANDOFF.md`

## Scope contract

- Go-to-market is workplace-dispute first.
- Shared evidence primitives should be matter-centric where practical.
- Do not pre-build unrelated sales, interview, tenant, insurance or generic meeting verticals.
- Live conversation assistance is a later capability built on the Case Brain, not the MVP.
- Do not add employer-side HR analytics without explicit strategic approval.
- The HRCompanion -> CaseMesh repository/project/namespace rename is complete. Do not recreate legacy identifiers except for deliberate backwards-compatible migration constants/tests.

## Product invariants

- Never silently turn an allegation, user statement, employer statement, third-party statement or model inference into an established fact.
- Every source-backed user-facing statement must trace to an exact source span and immutable document version.
- Preserve contradictory assertions until they are explicitly resolved.
- AI analysis/inference is separate from documentary evidence.
- Extraction confidence is not truth confidence.
- User/professional corrections must preserve audit history.
- The vector index is a retrieval accelerator, not the system of record.
- Case evidence and external legal/process authority are separate retrieval domains.
- No real personal workplace case data may be committed to the repository. Use synthetic fixtures only.

## Commercial MVP priority

Build in this order unless a documented architectural decision changes it:

1. Matter-root v2 epistemic domain (`SourceSpan`, `Assertion`, `Event`, `Contradiction`, `AuditEvent`);
2. deterministic invariant tests;
3. employment-specific extensions needed by workplace fixtures;
4. multi-user/tenant persistence;
5. immutable original-file storage and document versions;
6. ingestion and source-span extraction;
7. Case Brain merge/correction flow;
8. web timeline/evidence/disputed UI;
9. professional export;
10. matter-grounded Q&A after provenance gates pass;
11. meeting preparation;
12. Live capabilities only after their separate gates pass.

## Deferred work

Do not prioritize these ahead of the evidence MVP:

- live meeting copilot;
- whole-mailbox Gmail import;
- corporate Outlook ingestion;
- autonomous filing;
- outcome/win-probability scoring;
- compensation prediction;
- automated settlement negotiation;
- dedicated graph database;
- self-hosted foundation models;
- international employment-law support;
- employer-side HR analytics;
- unrelated generic verticals.

## Existing Windows prototype

Preserve current meeting code unless a later approved refactor removes it deliberately.

Still-valid live-track rules:

- `AI_SUGGESTED`, `USER_ACTUALLY_SAID`, and `HR_SAID` remain distinct concepts.
- Never claim real Windows/audio behavior is verified from mocks or compilation.
- Existing meeting/audio gates apply only to the CaseMesh Live track.

The live-meeting feature must not block commercial Case Brain work.

## Architecture boundaries

Preferred commercial direction:

- reusable/domain logic in .NET/C#;
- ASP.NET Core API/services for the commercial backend;
- PostgreSQL as the commercial system of record;
- pgvector/full-text retrieval as accelerators;
- private object storage for originals;
- background workers for ingestion;
- React/Next.js web UI for employee/professional surfaces.

Provider choices are replaceable and must not be embedded in domain logic.

Do not add Kubernetes, Kafka, microservice sprawl, a dedicated graph database or a self-hosted foundation model without evidence that the simpler architecture is insufficient.

## Case Brain rules

The root commercial aggregate is a `Matter`. Employment-specific concepts sit above reusable evidence primitives.

The central evidence object is `Assertion`, not a free-text `Fact`.

A minimum assertion must preserve:

- subject;
- predicate/value;
- who asserted it;
- when the underlying event allegedly occurred;
- when the assertion was made;
- exact source span when documentary;
- origin class;
- assertion class;
- dispute state;
- integrity state;
- extraction confidence;
- verification/correction state.

Do not store `Employee had 12 sickness days` merely because an employer letter says so. Store `Employer asserted 12 sickness days`, cite the exact source, separately store conflicting attendance evidence, and link the conflict through a contradiction record.

## AI rules

Use models for probabilistic tasks, not deterministic controls.

Good AI tasks:

- classification;
- entity candidates;
- assertion/event candidates;
- cross-document ambiguity analysis;
- contradiction candidates;
- professional synthesis;
- evidence-grounded Q&A.

Deterministic/application tasks:

- authorization and tenant checks;
- hashes;
- object ownership;
- duplicate hashes;
- citation/source existence;
- audit history;
- billing entitlements;
- destructive actions;
- procedural date arithmetic when enabled.

Uploaded content is always untrusted data and can never grant itself tool permissions or override application instructions.

## Security/privacy engineering rules

- Never place evidence text in ordinary analytics.
- Avoid evidence/document bodies in normal logs.
- Originals are private by default.
- Cross-tenant access tests are release-blocking.
- Model/OCR providers must be abstracted so data-handling requirements can change without domain rewrites.
- No production use of customer cases for model training by default.
- Do not claim end-to-end encryption if server-side processing requires plaintext.

## Git workflow

- Keep changes cohesive and reviewable.
- Do not rewrite history, force push, reset away user work or silently delete existing functionality.
- Keep the current solution/build green while introducing the new architecture where practical.
- Before declaring a task done: inspect the diff, build, run relevant tests and report exact validation.

## Definition of product success

A feature is valuable when it improves the user's ability to answer:

- What happened?
- Who says that?
- Where is the evidence?
- What conflicts with it?
- What is missing?
- What changed when new evidence arrived?
- What should I prepare for next?
- Can a professional use this Matter without rebuilding it from scratch?

Persuasive AI prose alone is not success.
