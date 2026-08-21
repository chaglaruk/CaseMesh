# Codex Commercial Handoff

Date: 2026-08-21

## Current state

The foundational evidence MVP backend is now substantially implemented and merged.

Completed commercial milestones:

1. generic Matter-root evidence/epistemic domain and deterministic invariants;
2. workplace-dispute extension layer;
3. tenancy-aware PostgreSQL persistence with RLS/FORCE RLS;
4. private immutable S3-compatible original evidence storage;
5. secure ingestion with type detection, malware scanning boundary, native parsing, OCR fallback and exact SourceSpan persistence;
6. structured extraction candidates, conservative entity resolution, incremental Matter Brain merge, contradictions and correction/audit propagation;
7. deterministic professional handover/export in DOCX, CSV, JSON and ZIP with tenant-scoped export audit metadata.

Current `main` after Milestone 7 is expected to contain PR #20 / merge commit `389f78ee724fe4968d1b596b9e44fd3244292af8` or a later documented main commit.

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

The implemented backend chain is now:

`private original evidence -> secure ingestion -> exact SourceSpan -> structured candidates -> canonical Matter Brain -> contradiction/correction state -> professional handover export`

The next critical product gap is no longer the evidence engine. It is turning that engine into a usable commercial product surface.

Proceed in this order unless a newer approved issue changes it:

1. Web MVP: ASP.NET Core API + authenticated tenant/user boundary + Matters + secure upload/processing + timeline/evidence/people/disputed/correction/export UI.
2. Matter-grounded Q&A and retrieval/gap analysis with strict citation/provenance gates.
3. Commercial pilot hardening: privacy deletion/export delivery/quotas/operational support/observability/accessibility and deployment-ready security controls.
4. Meeting preparation using canonical Matter state.
5. CaseMesh Live only after its separate real-world safety/privacy/latency gates.

## Non-negotiable invariants

- Synthetic fixtures only in the public repository; never commit real personal workplace/medical/email evidence.
- Tenant isolation is release-blocking. Preserve composite ownership, PostgreSQL RLS/FORCE RLS and restricted runtime-role checks.
- Original evidence bytes remain private and immutable in place; privacy deletion remains supported through storage-aware workflows.
- Every source-backed displayed statement must resolve through canonical provenance to exact SourceSpan -> document version -> original-object/hash.
- Employer, employee, third-party and AI statements remain distinct.
- Model output is never promoted to established fact merely because a model produced it.
- Contradictions and uncertainty remain visible until explicitly resolved.
- Corrections preserve history and append-only audit semantics.
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
- existing Infrastructure and Audio.Windows automated tests.

Run package vulnerability checks for new dependencies, `git diff --check`, diff/fixture secret and personal-data inspection, and Codex Security/security diff review when available.

## Definition of next success

The next stage succeeds when a real authenticated user can create a private Matter, upload synthetic evidence through the commercial path, see processing state, navigate source-backed timeline/evidence/people/contradictions, correct extracted state, and obtain a private professional export without accessing another tenant's data or bypassing provenance.