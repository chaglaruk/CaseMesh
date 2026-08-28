# Codex Commercial Handoff

Date: 2026-08-22

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

Milestones 8-10 were completed through Issues #22/#23 and merged PRs #25/#26. Milestone 11 is defined by Issue #27 and ADR 0011; its implementation belongs on the dedicated review branch/PR until the normal CI, security and independent-review gates are complete.

The HRCompanion -> CaseMesh rename is complete. Do not create another rename batch.

## Read first

1. `AGENTS.md`
2. `docs/BRAND_AND_SCOPE.md`
3. `docs/RESEARCH_BASELINE_2026-08-13.md`
4. `docs/COMMERCIAL_MASTER_PLAN.md`
5. `docs/CASE_BRAIN_SPEC.md`
6. `docs/CODE_REVIEW_WORKFLOW.md`
7. `docs/MULTI_MILESTONE_EXECUTION.md`
8. accepted ADRs under `docs/adr/`
9. current GitHub issue body for the milestone being implemented
10. merged review history for the immediately preceding milestones

When an older planning document conflicts with this handoff or a newer explicit issue, preserve the product/security invariants but follow the newer implementation state and issue scope.

## Current commercial critical path

The commercial evidence chain is now:

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> Matter-grounded Q&A -> professional handover -> meeting preparation`

Proceed in this order unless a newer approved issue changes it:

1. Web MVP: ASP.NET Core API + authenticated tenant/user boundary + Matters + secure upload/processing + timeline/evidence/people/disputed/correction/export UI. **Completed.**
2. Matter-grounded Q&A and retrieval/gap analysis with strict citation/provenance gates. **Completed.**
3. Commercial pilot hardening: privacy deletion/export delivery/quotas/operational support/observability/accessibility and deployment-ready security controls. **Completed.**
4. Meeting preparation using canonical Matter state. **Implemented in Milestone 11; merge only after its full gates pass.**
5. CaseMesh Live only after its separate real-world safety/privacy/latency gates.

Meeting preparation does not unblock claims of Live readiness by itself. The legacy local `MeetingState` remains a compatibility prototype, not a second commercial source of truth. Any future Live work must consume canonical Matter context through an explicit adapter/gate and must not create a parallel evidence store.

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

Milestone 11 succeeds when an authenticated user can open Prepare for their own Matter, review deterministic source-backed evidence points and chronology, inspect exact immutable source spans, see unresolved contradictions and factual gaps without silent resolution, receive an explicit currentness warning while ingestion is active, and preserve the distinction between documentary evidence, corrected history and unsupported or AI-derived statements without accessing another tenant's data.

After Milestone 11 is merged, the next product gate is CaseMesh Live. Do not advance Live status beyond the evidence recorded in `docs/GATES.md` and `docs/STATUS.md`; real Teams/audio/privacy/latency validation remains evidence-gated rather than inferred from automated tests.
