# Codex Commercial Handoff

Date: 2026-08-28

## Current state

The foundational evidence MVP and controlled commercial-pilot platform are substantially implemented.

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
11. canonical Matter meeting preparation as a deterministic, tenant-scoped read projection with currentness state, attributed evidence points, chronology, participants, unresolved disputes, factual clarification prompts and exact SourceSpan inspection.

Milestones 8-10 were completed through Issues #22/#23 and merged PRs #25/#26. Milestone 11 was completed through Issue #27 / PR #28 and merged as `a6e31be944217a2fae695ad9790537e770c55bb3`.

Current milestone: Issue #29 / draft PR #30 — canonical CaseMesh Live foundation. Its purpose is to establish the platform-neutral read-only canonical Matter -> Live/Review boundary before further real-time work. ADR 0012 controls that boundary.

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
12. current GitHub issue body for the milestone being implemented
13. merged review history for the immediately preceding milestones

When an older planning document conflicts with this handoff or a newer explicit issue, preserve the product/security invariants but follow the newer implementation state and issue scope.

## Current commercial critical path

The commercial evidence chain is now:

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> Matter-grounded Q&A -> professional handover -> meeting preparation -> canonical Live/Review context`

Proceed in this order unless a newer approved issue changes it:

1. Web MVP: ASP.NET Core API + authenticated tenant/user boundary + Matters + secure upload/processing + timeline/evidence/people/disputed/correction/export UI. **Completed.**
2. Matter-grounded Q&A and retrieval/gap analysis with strict citation/provenance gates. **Completed.**
3. Commercial pilot hardening: privacy deletion/export delivery/quotas/operational support/observability/accessibility and deployment-ready security controls. **Completed.**
4. Meeting preparation using canonical Matter state. **Completed in Milestone 11.**
5. Canonical CaseMesh Live/Review foundation over the existing Matter system of record. **In progress in Milestone 12 / PR #30.**
6. Uploaded meeting/transcript review on that boundary before promoting new real-time capability.
7. Consent-based real-time CaseMesh Live only after its separate real-world safety/privacy/latency gates.

Meeting preparation and the canonical Live foundation do not unblock claims of real Live readiness by themselves. The legacy local `MeetingState` remains a compatibility prototype, not a second commercial source of truth. Future Live work must consume canonical Matter context through the explicit adapter/gate and must not create a parallel evidence store.

## Milestone 12 boundary

The platform-neutral `CaseMesh.Live` layer must preserve these distinctions:

- current versus historical documentary Matter evidence;
- source-less current attributed Matter/user statements versus documentary evidence;
- AI analysis versus documentary evidence;
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` conversation origins;
- a source shown as meeting context versus provenance for what a participant actually said.

Documentary Live context must retain exact SourceSpan -> DocumentVersion -> immutable original-object/hash provenance. Context citations attached to meeting-review items may resolve only to current canonical documentary evidence and never become proof of the spoken wording.

## Non-negotiable invariants

- Synthetic fixtures only in the public repository; never commit real personal workplace/medical/email evidence.
- Tenant isolation is release-blocking. Preserve composite ownership, PostgreSQL RLS/FORCE RLS and restricted runtime-role checks.
- Original evidence bytes remain private and immutable in place; privacy deletion remains supported through storage-aware workflows.
- Every source-backed displayed statement must resolve through canonical provenance to exact SourceSpan -> document version -> original-object/hash.
- Employer, employee, third-party and AI statements remain distinct.
- Model output is never promoted to established fact merely because a model produced it.
- Contradictions and uncertainty remain visible until explicitly resolved.
- Corrections preserve history and append-only audit semantics. A user correction without documentary provenance must not inherit the corrected record's old source span as if it supported the new wording.
- Do not expose raw object-storage keys/credentials in public API/UI/domain surfaces.
- Do not introduce legal merits, liability, win-probability, compensation prediction or autonomous filing.
- Keep legacy WPF/SQLite/CaseMesh Live projects buildable; do not claim real Teams/audio verification without real gates.

## Local-only Live validation

All tasks that require the user's real Windows/Teams/audio devices, Credential Manager, live OpenAI connection, overlay focus testing, latency measurement or a 30+ minute rehearsal are deliberately separated into `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`.

Do not use repository CI or mocks to mark those gates VERIFIED. They remain governed by `docs/GATES.md` and `docs/STATUS.md`.

## Review workflow

Follow `docs/CODE_REVIEW_WORKFLOW.md` and `.coderabbit.yaml`.

Keep implementation PRs draft while changing materially. Stabilize and make required CI/security gates green before the first independent CodeRabbit review. Batch valid findings instead of causing per-commit incremental review churn. Request a second global review only when material post-review changes justify it.

Codex Security diff scans may currently hit the known upstream `scan.target.snapshotDigest: expected a non-empty string` save/finalization defect even after scan analysis completes. Do not interpret that schema/save error itself as a repository vulnerability. Preserve and inspect scan artifacts/results when produced, and follow the installed Codex Security tool's retry/stop rules. Do not claim a successful persisted scan when finalization failed.

## Existing follow-up backlog

- Issue #12: benchmark and batch PostgreSQL Matter writes when representative scale evidence justifies it.
- Issue #13: repository-wide CI/action/container digest pinning.

These are not automatically blockers for product milestones unless measurements/security evidence make them blocking.

## Validation baseline

Every commercial milestone must preserve the complete applicable regression set, including:

- Core/Matter/Workplace tests;
- PostgreSQL real-service integration;
- S3/object-storage real-service integration;
- ingestion real-service integration;
- Matter Brain deterministic and persistence tests;
- professional export tests;
- existing Infrastructure and Audio.Windows automated tests;
- Web contract tests and the real-browser Playwright/axe journey for commercial surfaces.

Run package vulnerability checks for new dependencies, `git diff --check`, diff/fixture secret and personal-data inspection, and Codex Security/security diff review when available.

## Definition of next success

Milestone 12 succeeds when an authenticated user can obtain a deterministic tenant-scoped Review/Live context for their own canonical Matter; current/historical documentary evidence retains exact immutable provenance; source-less attributed statements and AI analysis remain separately labelled and uncited; unresolved contradictions remain visible; meeting transcript/suggestion origins remain distinct; context citations resolve only to current documentary evidence; another tenant cannot access the context; and all applicable CI/security/review gates pass.

Passing Milestone 12 still does not mean CaseMesh Live is meeting-ready. Real Teams/audio/transcription/privacy/latency/endurance status can advance only from the local evidence package in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`.
