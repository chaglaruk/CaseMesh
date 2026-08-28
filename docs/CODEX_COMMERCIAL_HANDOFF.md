# Codex Commercial Handoff

Date: 2026-08-28

## Current state

The foundational evidence MVP, controlled commercial-pilot platform, canonical meeting preparation, canonical CaseMesh Live/Review boundary and its post-merge hardening are merged.

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
12. canonical CaseMesh Live/Review foundation over the PostgreSQL Matter system of record.

Milestone 11 merged through Issue #27 / PR #28 as `a6e31be944217a2fae695ad9790537e770c55bb3`.
Milestone 12 merged through Issue #29 / PR #30 as `0bb9124a6ba91a7028553c9e4928bb36570d6708`.
The deliberate post-merge hardening follow-up Issue #31 / PR #32 merged as `3fa41a5b93146913e1f1364dead25dd9149073fd` after exact-head CI, resolved CodeRabbit findings and a clean final Codex review.

Current product work is **Issue #33 / draft PR #34 — Milestone 13: uploaded meeting transcript Review** on branch `issue-33-uploaded-transcript-review`.

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
12. current GitHub issue/PR body
13. merged review history for the immediately preceding milestone

When an older planning document conflicts with this handoff or a newer explicit issue, preserve product/security invariants but follow the newer implementation state and issue scope.

## Current commercial critical path

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> Matter-grounded Q&A -> professional handover -> meeting preparation -> canonical Live/Review context -> uploaded transcript Review`

Proceed in this order unless a newer approved issue changes it:

1. Web MVP. **Completed.**
2. Matter-grounded Q&A and factual-gap analysis. **Completed.**
3. Commercial pilot hardening. **Completed.**
4. Canonical Matter meeting preparation. **Completed.**
5. Canonical CaseMesh Live/Review foundation and hardening. **Completed.**
6. Uploaded meeting/transcript Review. **Current Milestone 13 / Issue #33 / PR #34.**
7. Consent-based real-time CaseMesh Live only after separate real-world safety/privacy/latency gates.

Do not begin real-time Live product work merely because Milestone 13 software tests pass.

## Milestone 13 architecture

Uploaded meeting transcript Review is persisted conversation/review state, **not** a second evidence system of record.

Current draft architecture:

- migration `0010_uploaded_meeting_reviews.sql` creates tenant/Matter-scoped Review/session/item/context-citation tables under RLS/FORCE RLS;
- Matter deletion cascades Review rows so transcript text is not orphaned by the supported privacy lifecycle;
- transcript items preserve `HrSaid`, `UserActuallySaid` and `AiSuggested` separately;
- server-side transcript validation bounds item count, text, aggregate text, duration, IDs, timestamps and context citation count;
- at creation, optional context citations may resolve only to current canonical documentary SourceSpans;
- context citations mean Matter context shown beside a transcript item, never documentary provenance that the participant spoke the wording;
- saved Review sessions retain their original context references; when reopened, current canonical Matter state classifies those references as Current, Historical or Missing;
- exact source text is not copied into Review persistence and is retrieved through the existing authenticated tenant/Matter/source-detail boundary;
- deterministic Review analysis may surface relevant unresolved canonical contradictions and conservative verification prompts, but never decides truth;
- authenticated create/list/read Review endpoints use private/no-store response handling;
- Matter Web navigation exposes `Review`, structured JSON transcript import, saved Review reopening and exact-source inspection;
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
- New Review context citations are current-documentary-only; reopened historical state is labelled rather than silently rewritten.
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

## Milestone 13 validation requirements

Before PR #34 may leave draft / merge:

- full Windows solution restore/build/test green;
- real PostgreSQL migration/persistence/RLS tests green;
- existing object-storage and ingestion real-service gates green;
- QA evals green;
- Web type-check/unit/build green;
- real-browser Playwright/axe commercial journey green, including applicable Review surface coverage;
- cross-tenant access to an **existing** saved Review fails closed;
- new context citations reject historical/unknown source IDs;
- persisted Review survives normal reopen but disappears with Matter privacy deletion;
- transcript origins remain distinct and no context citation becomes spoken provenance;
- package/diff/fixture secret/personal-data/security checks show no release-blocking issue;
- independent review findings are resolved before squash merge.

Keep PR #34 draft while implementation or CI is materially changing. Stabilize exact head before the independent CodeRabbit review; batch valid fixes and avoid incremental review churn.

## Local-only Live validation

All tasks requiring the user's real Windows/Teams/audio devices, Credential Manager, live OpenAI connection, overlay focus testing, latency measurement or a 30+ minute rehearsal remain in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`.

Do not use repository CI, structured transcript upload or mocks to mark those gates VERIFIED. They remain governed by `docs/GATES.md` and `docs/STATUS.md`.

## Existing follow-up backlog

- Issue #12: benchmark/batch PostgreSQL Matter writes when representative scale evidence justifies it.
- Issue #13: repository-wide CI/action/container digest pinning.

These are not automatically blockers for Milestone 13 unless new evidence makes them blocking.

## Definition of next success

Milestone 13 succeeds when an authenticated synthetic user can create and reopen a private uploaded transcript Review for one Matter, preserve speaker/AI identity, inspect authorized Matter context without turning it into spoken provenance, see stale context and unresolved contradictions conservatively, and another tenant cannot infer or access the saved Review. Matter deletion must not leave Review transcript text behind, all applicable CI/review gates must be green, and no real-time Live readiness claim may be made from these results.