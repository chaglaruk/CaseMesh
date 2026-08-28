# Codex Commercial Handoff

Date: 2026-08-28

## Current state

The foundational evidence MVP, controlled commercial-pilot platform, canonical meeting preparation, canonical CaseMesh Live/Review boundary, its post-merge hardening, and uploaded meeting transcript Review are merged.

Completed commercial milestones:

1. generic Matter-root evidence/epistemic domain and deterministic invariants;
2. workplace-dispute extension layer;
3. tenancy-aware PostgreSQL persistence with RLS/FORCE RLS;
4. private immutable S3-compatible original evidence storage;
5. secure ingestion with type detection, malware scanning boundary, native parsing, OCR fallback and exact SourceSpan persistence;
6. structured extraction candidates, conservative entity resolution, incremental Matter Brain merge, contradictions and correction/audit propagation;
7. deterministic professional handover/export in DOCX, CSV, JSON and ZIP;
8. authenticated employee Web MVP;
9. provenance-gated Matter Q&A and factual-gap analysis;
10. commercial-pilot hardening: quotas/retention, private verified export delivery, durable privacy deletion, readiness/telemetry, diagnostics, accessibility and deployment controls;
11. canonical Matter meeting preparation;
12. canonical CaseMesh Live/Review foundation over the PostgreSQL Matter system of record;
13. uploaded meeting transcript Review over that canonical boundary.

Milestone 11 merged through Issue #27 / PR #28 as `a6e31be944217a2fae695ad9790537e770c55bb3`.
Milestone 12 merged through Issue #29 / PR #30 as `0bb9124a6ba91a7028553c9e4928bb36570d6708`.
The deliberate post-merge hardening follow-up Issue #31 / PR #32 merged as `3fa41a5b93146913e1f1364dead25dd9149073fd`.
Milestone 13 merged through Issue #33 / PR #34 as `a1eb151c7ae84228b90d45db7a0d05ef3f3e6b85`; final feature head `e5f096cf5c2eb4659470f71269bac4e74866f8a6` passed exact-head CI run `33168635240` with all seven jobs green and every inline review thread resolved.

There is currently **no approved next commercial repository milestone for consent-based real-time Live**. Real-time product promotion is gated by real-world Windows/Teams/audio/privacy/latency/endurance evidence, not by another repository-only implementation pass.

The HRCompanion -> CaseMesh rename is complete. Do not create another rename batch.

## Read first

1. `AGENTS.md`
2. `docs/BRAND_AND_SCOPE.md`
3. `docs/RESEARCH_BASELINE_2026-08-13.md`
4. `docs/COMMERCIAL_MASTER_PLAN.md`
5. `docs/CASE_BRAIN_SPEC.md`
6. `docs/CODE_REVIEW_WORKFLOW.md`
7. `docs/MULTI_MILESTONE_EXECUTION.md`
8. `docs/GATES.md`
9. `docs/STATUS.md`
10. `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`
11. accepted ADRs, especially ADR 0012 and ADR 0013
12. current GitHub issues/PRs
13. merged review history for the immediately preceding milestone

When an older planning document conflicts with this handoff or a newer explicit issue, preserve product/security invariants but follow the newer implementation state and issue scope.

## Commercial critical path

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> Matter-grounded Q&A -> professional handover -> meeting preparation -> canonical Live/Review context -> uploaded transcript Review -> real-world Live gates -> consent-based real-time Live`

Repository product delivery through **uploaded meeting/transcript Review is completed**. Do not begin the final real-time Live product step merely because automated tests are green.

Before approving a new real-time Live issue/PR, record the applicable evidence from `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md` and `docs/GATES.md`, including real process-specific Teams audio ownership, real transcription/reconnect behavior, credential/runtime checks, overlay usability, representative latency and endurance.

## Milestone 13 architecture now merged

Uploaded meeting transcript Review is persisted conversation/review state, **not** a second evidence system of record.

The merged architecture:

- migration `0010_uploaded_meeting_reviews.sql` creates tenant/Matter-scoped Review/session/item/context-citation tables under RLS/FORCE RLS;
- Matter deletion cascades Review rows so transcript text is not orphaned by the supported privacy lifecycle;
- transcript items preserve `HrSaid`, `UserActuallySaid` and `AiSuggested` separately;
- transcript wording is stored as submitted, while blank and bounded-size validation remains deterministic;
- transcript items must be supplied chronologically and tied timestamps preserve supplied order;
- server-side transcript validation bounds item count, text, aggregate text, duration, IDs, timestamps and context citation count;
- cumulative Matter and tenant Review session/UTF-8 byte quotas are enforced transactionally before Review transcript rows are written;
- at creation, optional context citations may resolve only to current canonical documentary SourceSpans;
- context citations mean Matter context shown beside a transcript item, never documentary provenance that the participant spoke the wording;
- saved Review sessions retain context-reference identifiers even when a source later becomes unavailable; reopened Matter state classifies references as Current, Historical or Missing;
- exact source text is not copied into Review persistence and is retrieved through the existing authenticated tenant/Matter/source-detail boundary;
- deterministic Review analysis may surface relevant unresolved canonical contradictions and conservative verification prompts, but never decides truth;
- authenticated create/list/read Review endpoints use private/no-store response handling;
- null transcript collection elements fail closed as invalid input before projection;
- Matter Web navigation exposes `Review`, structured JSON transcript import, saved Review reopening and exact-source inspection;
- client-side Matter scope changes reset Review state and stale requests are aborted/ignored;
- structured JSON is the first MVP import contract. Audio/video provider transcription remains deferred.

See ADR 0013 for the durable architectural decision.

## Canonical Live/Review invariants

- Canonical PostgreSQL Matter/Matter Brain state remains the only commercial evidence authority.
- Transcript conversation rows never become `Assertion`, `Event` or `SourceSpan` merely because they were uploaded.
- Current versus historical documentary Matter evidence remains explicit.
- Source-less attributed Matter/user statements remain separate from documentary evidence.
- AI analysis remains separate from documentary evidence.
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` remain distinct.
- A Matter source shown as meeting context is not provenance for what a participant actually said.
- New Review context citations are current-documentary-only; reopened historical or missing state is labelled rather than silently rewritten.
- Exact source detail remains authorized, tenant/Matter scoped and private/no-store.
- Structured-extraction contradiction provenance remains auditable; trusted deterministic rules remain labelled deterministic.

## Non-negotiable repository invariants

- Synthetic fixtures only in the public repository; never commit real personal workplace/medical/email evidence.
- Tenant isolation is release-blocking. Preserve composite ownership, PostgreSQL RLS/FORCE RLS and restricted runtime-role checks.
- Original evidence bytes remain private and immutable in place; privacy deletion remains storage-aware.
- Every source-backed displayed evidence statement resolves through canonical SourceSpan -> document version -> original-object/hash provenance.
- Employer, employee, third-party and AI statements remain distinct.
- Model output is never promoted to established fact merely because a model produced it.
- Contradictions and uncertainty remain visible until explicitly resolved.
- Corrections preserve history and append-only audit semantics. Unsupported corrections do not inherit old documentary provenance.
- Do not expose raw object-storage keys/credentials.
- Do not introduce legal merits, liability, win-probability, compensation prediction or autonomous filing.
- Keep legacy WPF/SQLite/Audio projects buildable; do not claim real Teams/audio verification without real gates.
- Transcript/evidence bodies must not enter ordinary logs or telemetry labels.

## Milestone 13 final validation evidence

PR #34 final feature head: `e5f096cf5c2eb4659470f71269bac4e74866f8a6`.

Exact-head CI run `33168635240` passed:

- Windows full solution restore/build/test;
- real PostgreSQL migration/persistence/RLS integration;
- real S3-compatible object storage integration;
- real ingestion service gate with PostgreSQL, MinIO, malware scanner, OCR and PDF rasterization;
- deterministic Q&A/QA evals;
- Web type-check/unit/build;
- real-browser Playwright/axe commercial journey including the Review surface.

Review fixes covered null transcript entries, cumulative Review quotas, saved citation identity after source loss, client-side Matter-scope reset/stale requests, exact transcript wording preservation, chronology validation and tied-timestamp input order. All inline CodeRabbit/Codex review threads were resolved before squash merge.

These automated results prove repository behavior only. They do not prove real Teams/audio/live-model readiness. No post-merge `main` CI is claimed by this handoff.

## Local-only Live validation is now the product gate

Tasks requiring the user's real Windows/Teams/audio devices, Credential Manager, live model connection, overlay focus testing, latency measurement or a 30+ minute rehearsal remain in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`.

Do not use repository CI, structured transcript upload or mocks to mark those gates VERIFIED. They remain governed by `docs/GATES.md` and `docs/STATUS.md`.

A new consent-based real-time Live product milestone should be opened only after enough of those real-world gates are evidenced to define a truthful and safe scope.

## Existing follow-up backlog

- Issue #12: benchmark/batch PostgreSQL Matter writes **only when representative scale evidence justifies it or before production-scale beta**. Do not optimize from assumption alone.
- Issue #13: repository-wide CI/action/container digest pinning. This is repository hardening, not the next commercial product milestone.

Neither backlog item is evidence that real-time Live is ready. They may be scheduled independently when their own trigger/priority justifies it.

## Definition of next success

The next commercial product success is **not another repository-only Live feature**. It is a documented real-world validation checkpoint showing that the relevant local Live gates can be met on the user's actual Windows/Teams/audio environment without weakening Matter provenance, privacy, transcript identity or recovery guarantees. Only then should a consent-based real-time Live implementation milestone be approved.