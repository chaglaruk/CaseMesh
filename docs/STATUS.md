# Status

Last update: 2026-08-28.

The commercial Matter evidence platform through Milestone 13 is merged. Milestone 13 — **uploaded meeting transcript Review** — merged through Issue #33 / PR #34 as `a1eb151c7ae84228b90d45db7a0d05ef3f3e6b85` after exact-head CI run `33168635240` passed all seven jobs and all inline independent-review threads were resolved. This repository result does **not** upgrade any real Windows/Teams/audio readiness gate.

## Commercial milestone state

| Area | Status | Notes |
|---|---|---|
| Canonical Matter -> Live/Review boundary | MERGED | Milestone 12 merged as `0bb9124a6ba91a7028553c9e4928bb36570d6708`. Follow-up Issue #31 / PR #32 merged as `3fa41a5b93146913e1f1364dead25dd9149073fd`, resolving late correctness/performance/privacy findings. |
| Uploaded meeting transcript Review | MERGED | Issue #33 / PR #34 merged as `a1eb151c7ae84228b90d45db7a0d05ef3f3e6b85`. Persisted tenant-scoped transcript Reviews preserve HR/user/AI origins, exact submitted wording and chronology, current-only new context citations, stale/missing reference history, deterministic context/contradiction analysis, cumulative Review quotas and the Web Review surface. |
| Commercial evidence authority | VERIFIED_FOR_ARCHITECTURE | Canonical PostgreSQL Matter/Matter Brain remains the only commercial evidence system of record. Uploaded transcript Review rows are conversation/review state and do not become canonical evidence merely by being uploaded. |
| Review privacy / tenant isolation | VERIFIED_AUTOMATED | Review tables use tenant/Matter composite ownership and RLS/FORCE RLS; Review HTTP surfaces are private/no-store; cross-tenant and same-tenant wrong-Matter tests fail closed; Matter deletion cascades Review rows. |
| Review context provenance | VERIFIED_AUTOMATED | New context citations attach only to current canonical documentary SourceSpans. Reopened references are classified current/historical/missing and remain contextual rather than provenance that a participant spoke cited wording. |
| Review Web surface | VERIFIED_AUTOMATED | Matter navigation includes `Review`; structured JSON import preserves exact wording and HR_SAID / USER_ACTUALLY_SAID / AI_SUGGESTED, saved sessions reopen, stale navigation requests are scoped out, and authorized exact Matter sources can be inspected. |
| Build / automated tests | VERIFIED_AUTOMATED | Final feature head `e5f096cf5c2eb4659470f71269bac4e74866f8a6`; exact-head CI `33168635240` passed Windows, PostgreSQL, object storage, ingestion/scanner/OCR, QA, Web build and real-browser Playwright/axe jobs. No post-merge `main` CI is claimed here. |
| Legacy Windows Live runtime | AUTOMATED_ONLY | Existing WPF/SQLite/audio projects remain buildable compatibility/runtime prototypes. They are not the commercial evidence authority. |
| Process-specific Teams capture | AUTOMATED_ONLY | Application-loopback architecture exists, but a real Teams process-specific ownership run is still required. |
| Live OpenAI transcription | PARTIAL | Realtime client/two-source ownership compile; real reconnect/order/provider behavior remains local/live work. |
| WPF overlay | PARTIAL | SAY/WATCH/ASK and evidence state compile; focus/runtime validation with Teams foreground remains pending. |
| Windows credential storage | AUTOMATED_ONLY | Implementation compiles; actual Windows Credential Manager behavior remains a local gate. |
| Real latency/endurance | NOT_VERIFIED | Median/p95 and 30+ minute rehearsal remain local/live gates. |

## Milestone 13 invariants now merged

- Transcript wording is attributed conversation material, not documentary fact.
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` remain distinct.
- Uploaded Review persistence does not create a parallel `Assertion`/`SourceSpan` evidence store.
- Submitted transcript wording is preserved exactly; blank/oversized input is rejected.
- Transcript input must be chronological and tied timestamps retain supplied order.
- New context citations may resolve only to current canonical documentary evidence.
- A Matter context citation does not prove that a participant spoke the cited wording.
- Saved Review citation identifiers survive later source unavailability and are re-evaluated as current/historical/missing rather than silently rewritten.
- Exact source text is retrieved on demand through the authenticated tenant/Matter/source-detail boundary and is not duplicated into Review storage.
- Cumulative tenant/Matter Review session and byte quotas are enforced before persistence.
- Review transcript bodies must not enter ordinary telemetry/log labels.
- Review responses are private/no-store.
- Matter privacy deletion must not orphan persisted Review transcript text.
- Deterministic Review prompts preserve uncertainty and do not decide truth or provide legal merits/win/compensation conclusions.
- No real raw audio is persisted by Milestone 13.

## Commercial repository checkpoint

There is no approved next real-time product implementation milestone to start solely from repository CI. The commercial critical path has reached the real-world CaseMesh Live validation boundary.

Consent-based real-time CaseMesh Live must not be promoted until the applicable evidence in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md` and `docs/GATES.md` has been produced on the user's real Windows/Teams/audio environment.

Open Issues #12 and #13 remain deliberately separate backlog work:

- #12 is triggered by representative scale evidence or production-scale beta need; do not optimize PostgreSQL Matter writes from assumption alone.
- #13 is repository-wide supply-chain hardening and is not the next commercial product milestone.

## Local-only Live gates still open

The accumulated Windows tasks remain in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`:

- real Teams process-specific remote/user audio ownership, mute/unmute, headphones/Bluetooth, non-Teams exclusion and reconnect;
- real two-source transcription/reconnect/item ordering;
- representative private local Case Brain files outside Git;
- Windows Credential Manager runtime behavior and real answer-model safety eval;
- overlay focus/usability;
- median/p95 latency and stale-generation checks;
- 30+ minute endurance/recovery;
- confirmation that normal operation leaves no raw audio behind.

See `docs/GATES.md` for meeting-readiness evidence requirements. Do not upgrade any real Windows/live status without corresponding recorded evidence.