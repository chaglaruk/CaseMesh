# AGENTS.md — CaseMesh engineering contract

## Mission

Build a trustworthy evidence operating system for workplace disputes on top of a reusable matter-centric evidence core.

The commercial critical path is:

`evidence -> provenance -> structured Matter/Case Brain -> chronology -> dispute map -> correction -> professional handover`

The product brand is **CaseMesh**. Read `docs/BRAND_AND_SCOPE.md` before changing product scope or naming.

The existing Windows live-meeting copilot remains a useful experimental/later feature track. It is no longer the repository's primary product goal.

Read these before commercial implementation:

- `docs/BRAND_AND_SCOPE.md`
- `docs/RESEARCH_BASELINE_2026-08-13.md`
- `docs/COMMERCIAL_MASTER_PLAN.md`
- `docs/CASE_BRAIN_SPEC.md`
- `docs/PRODUCT_VALIDATION_AND_GTM.md`

## Scope contract

- Go-to-market is workplace-dispute first.
- Shared evidence primitives should be matter-centric where practical.
- Do not pre-build unrelated sales, interview, tenant, insurance or generic meeting verticals.
- Live conversation assistance is a later capability built on the Case Brain, not the MVP.
- Do not add employer-side HR analytics without explicit strategic approval.
- Remaining `HRCompanion.*` source identifiers are legacy names; migrate them to `CaseMesh.*` in a dedicated behavior-neutral rename batch.

## Product invariants

- Never silently turn an allegation, user statement, employer statement, third-party statement or model inference into an established fact.
- Every source-backed user-facing statement must trace to an exact source span and immutable document version.
- Preserve contradictory assertions until they are explicitly resolved.
- AI analysis/inference is separate from documentary evidence.
- Extraction confidence is not truth confidence.
- User/professional corrections must preserve audit history.
- The vector index is a retrieval accelerator, not the system of record.
- Case evidence and external legal/process authority are separate retrieval domains.
- No real personal HR case data may be committed to the repository. Use synthetic fixtures only.

## Commercial MVP priority

Build in this order unless a documented architectural decision changes it:

1. behavior-neutral CaseMesh code-identifier migration;
2. commercial architecture scaffolding while keeping the existing solution buildable;
3. v2 Matter-root epistemic domain (`SourceSpan`, `Assertion`, `Event`, `Contradiction`, `AuditEvent`);
4. invariant tests;
5. employment-specific extensions needed by workplace fixtures;
6. multi-user/tenant persistence;
7. immutable original-file storage and document versions;
8. ingestion and source-span extraction;
9. Case Brain merge/correction flow;
10. web timeline/evidence/disputed UI;
11. professional export;
12. matter-grounded Q&A after provenance gates pass;
13. meeting preparation;
14. Live capabilities only after their separate gates pass.

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

Still-valid legacy rules:

- `AI_SUGGESTED`, `USER_ACTUALLY_SAID`, and `HR_SAID` remain distinct concepts.
- Never claim real Windows/audio behavior is verified from mocks or compilation.
- Existing meeting/audio gates remain applicable only to that feature track.

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

The root commercial aggregate should be a `Matter`. Employment-specific concepts sit above reusable evidence primitives.

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

Example:

Do not store `Employee had 12 sickness days` merely because an employer letter says so.

Store `Employer asserted 12 sickness days`, cite the exact source, and separately store any attendance evidence that says otherwise. Link the conflict through a contradiction record.

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
- procedural date arithmetic when such a feature is enabled.

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
- Prefer new commercial projects/namespaces and migration paths over destructive rewrites.
- Keep the current solution/build green while introducing the new architecture where practical.
- Keep mechanical naming changes separate from evidence-model behavior changes.
- Before declaring a task done: inspect diff, build, run relevant tests and report exact validation.

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
