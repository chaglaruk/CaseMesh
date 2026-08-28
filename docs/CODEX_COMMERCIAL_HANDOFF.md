# Codex Commercial Handoff

Date: 2026-08-28

## Current state

The foundational evidence MVP, controlled commercial-pilot platform, canonical meeting preparation, and the first canonical CaseMesh Live/Review boundary are merged.

Completed commercial milestones:

1. generic Matter-root evidence/epistemic domain and deterministic invariants;
2. workplace-dispute extension layer;
3. tenancy-aware PostgreSQL persistence with RLS/FORCE RLS;
4. private immutable S3-compatible original evidence storage;
5. secure ingestion with type detection, malware scanning boundary, native parsing, OCR fallback and exact SourceSpan persistence;
6. structured extraction candidates, conservative entity resolution, incremental Matter Brain merge, contradictions and correction/audit propagation;
7. deterministic professional handover/export in DOCX, CSV, JSON and ZIP with tenant-scoped export audit metadata;
8. authenticated employee Web MVP;
9. provenance-gated Matter Q&A and factual-gap analysis;
10. commercial-pilot hardening: persisted quotas and retention, private verified export delivery, durable privacy deletion, readiness/telemetry, operator diagnostics, accessibility and deployment controls;
11. canonical Matter meeting preparation as a deterministic tenant-scoped read projection;
12. canonical CaseMesh Live/Review foundation over the PostgreSQL Matter system of record.

Milestone 11 merged through Issue #27 / PR #28 as `a6e31be944217a2fae695ad9790537e770c55bb3`.
Milestone 12 merged through Issue #29 / PR #30 as `0bb9124a6ba91a7028553c9e4928bb36570d6708` after exact-head CI and review fixes.

A deliberately requested final Codex re-review completed after PR #30 merged and identified additional correctness/performance findings. Current work is therefore **Issue #31 / draft PR #32 — canonical Live context hardening**. This follow-up is a blocker for beginning the uploaded meeting/transcript Review product milestone.

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
11. accepted ADRs under `docs/adr/`
12. current GitHub issue body for the milestone/follow-up being implemented
13. merged review history for the immediately preceding milestone

When an older planning document conflicts with this handoff or a newer explicit issue, preserve product/security invariants but follow the newer implementation state and issue scope.

## Current commercial critical path

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> Matter-grounded Q&A -> professional handover -> meeting preparation -> canonical Live/Review context`

Proceed in this order unless a newer approved issue changes it:

1. Web MVP. **Completed.**
2. Matter-grounded Q&A and factual-gap analysis. **Completed.**
3. Commercial pilot hardening. **Completed.**
4. Canonical Matter meeting preparation. **Completed in Milestone 11.**
5. Canonical CaseMesh Live/Review foundation. **Completed in Milestone 12; post-merge hardening is Issue #31 / PR #32.**
6. Uploaded meeting/transcript Review on the hardened canonical boundary.
7. Consent-based real-time CaseMesh Live only after separate real-world safety/privacy/latency gates.

Do not begin the uploaded meeting/transcript Review milestone until Issue #31 / PR #32 is merged and `main` is synchronized from that merge.

## Canonical Live boundary

The platform-neutral `CaseMesh.Live` layer preserves these distinctions:

- current versus historical documentary Matter evidence;
- current versus historical source-less attributed Matter/user statements;
- AI analysis versus documentary evidence;
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` conversation origins;
- a Matter source shown as meeting context versus provenance for what a participant actually said.

Documentary Live context resolves to exact `SourceSpan -> DocumentVersion -> immutable original-object/hash` provenance. The default Review context must not inline unbounded aggregate exact source text. It carries bounded source metadata/digests and source IDs; exact source text is retrieved separately through an authenticated tenant/Matter-scoped source-detail route.

Context citations attached to meeting-review items may resolve only to current canonical documentary evidence and never become proof of the spoken wording.

Structured-extraction contradictions must preserve auditable candidate/run/model/prompt/schema/digest provenance. Trusted deterministic correction rules remain labelled as deterministic rather than being mistaken for model output.

## Non-negotiable invariants

- Synthetic fixtures only in the public repository; never commit real personal workplace/medical/email evidence.
- Tenant isolation is release-blocking. Preserve composite ownership, PostgreSQL RLS/FORCE RLS and restricted runtime-role checks.
- Original evidence bytes remain private and immutable in place; privacy deletion remains supported through storage-aware workflows.
- Every source-backed displayed statement must resolve through canonical provenance to exact SourceSpan -> document version -> original-object/hash.
- Employer, employee, third-party and AI statements remain distinct.
- Model output is never promoted to established fact merely because a model produced it.
- Contradictions and uncertainty remain visible until explicitly resolved.
- Corrections preserve history and append-only audit semantics. A user correction without documentary provenance must not inherit the corrected record's old source span.
- Do not expose raw object-storage keys/credentials in public API/UI/domain surfaces.
- Do not introduce legal merits, liability, win-probability, compensation prediction or autonomous filing.
- Keep legacy WPF/SQLite/CaseMesh Live projects buildable; do not claim real Teams/audio verification without real gates.

## Local-only Live validation

All tasks requiring the user's real Windows/Teams/audio devices, Credential Manager, live OpenAI connection, overlay focus testing, latency measurement or a 30+ minute rehearsal remain in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`.

Do not use repository CI or mocks to mark those gates VERIFIED. They remain governed by `docs/GATES.md` and `docs/STATUS.md`.

## Review workflow

Follow `docs/CODE_REVIEW_WORKFLOW.md` and `.coderabbit.yaml`.

Keep implementation PRs draft while changing materially. Stabilize and make required CI/security gates green before the independent CodeRabbit review. Batch valid findings rather than causing per-commit review churn. Request another global review only when material post-review changes justify it.

Late material findings from an optional secondary review must not be ignored merely because the preceding PR already merged. Track them in a follow-up issue/PR, as with Issue #31 / PR #32.

Codex Security diff scans may hit the known upstream `scan.target.snapshotDigest: expected a non-empty string` save/finalization defect. Do not interpret that schema/save error itself as a repository vulnerability, and do not claim a successful persisted scan when finalization failed.

## Existing follow-up backlog

- Issue #12: benchmark and batch PostgreSQL Matter writes when representative scale evidence justifies it.
- Issue #13: repository-wide CI/action/container digest pinning.

These are not automatically blockers for product milestones unless measurements/security evidence make them blocking.

## Validation baseline

Every commercial milestone/follow-up must preserve the complete applicable regression set, including:

- Core/Matter/Workplace tests;
- PostgreSQL real-service integration;
- S3/object-storage real-service integration;
- ingestion real-service integration;
- Matter Brain deterministic and persistence tests;
- professional export tests;
- existing Infrastructure and Audio.Windows automated tests;
- Web contract tests and the real-browser Playwright/axe journey for commercial surfaces.

Run package vulnerability checks for new dependencies, `git diff --check`, diff/fixture secret and personal-data inspection, and security diff review when available.

## Definition of next success

Issue #31 / PR #32 succeeds when all late PR #30 findings are addressed: Live source/AI confidence and extraction metadata are preserved; source-less corrected/rejected statements remain visible historically; correction-generated deterministic contradictions are classified correctly; structured contradiction provenance uses indexed lookups; the default Review context no longer inlines aggregate exact source text; authorized exact source detail remains retrievable fail-closed; and all applicable CI/review gates pass.

After that merge, the next product milestone is uploaded meeting/transcript Review on the hardened canonical boundary. Passing either milestone still does **not** mean CaseMesh Live is meeting-ready; real Teams/audio/transcription/privacy/latency/endurance status can advance only from the local evidence package.
