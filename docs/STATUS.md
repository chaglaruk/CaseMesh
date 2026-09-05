# Status

Last update: 2026-09-05.

The commercial Matter evidence platform through Milestone 13 and its post-merge transcript Review hardening is merged. Milestone 13 — **uploaded meeting transcript Review** — merged through Issue #33 / PR #34 as `a1eb151c7ae84228b90d45db7a0d05ef3f3e6b85`. The dedicated follow-up Issue #37 / PR #38 then merged as `f000a4f26633acea591f081acfc0757e50d5e5b5`, after exact candidate head `6e041faaea021df52f62e0f5013cd99133048a08` passed all seven jobs in CI run `33170725521` and the independent CodeRabbit full review generated no actionable comments. These repository results do **not** upgrade any real Windows/Teams/audio readiness gate.

## Commercial milestone state

| Area | Status | Notes |
|---|---|---|
| Canonical Matter -> Live/Review boundary | MERGED | Milestone 12 merged as `0bb9124a6ba91a7028553c9e4928bb36570d6708`. Follow-up Issue #31 / PR #32 merged as `3fa41a5b93146913e1f1364dead25dd9149073fd`, resolving late correctness/performance/privacy findings. |
| Uploaded meeting transcript Review | MERGED | Issue #33 / PR #34 merged as `a1eb151c7ae84228b90d45db7a0d05ef3f3e6b85`; post-merge hardening Issue #37 / PR #38 merged as `f000a4f26633acea591f081acfc0757e50d5e5b5`. Persisted tenant-scoped transcript Reviews preserve HR/user/AI origins, exact submitted wording and chronology, current-only new context citations, stale/missing reference history, deterministic context/contradiction analysis, cumulative Review quotas and the Web Review surface. |
| Commercial evidence authority | VERIFIED_FOR_ARCHITECTURE | Canonical PostgreSQL Matter/Matter Brain remains the only commercial evidence system of record. Uploaded transcript Review rows are conversation/review state and do not become canonical evidence merely by being uploaded. |
| Review privacy / tenant isolation | VERIFIED_AUTOMATED | Review tables use tenant/Matter composite ownership and RLS/FORCE RLS; Review HTTP surfaces are private/no-store; cross-tenant and same-tenant wrong-Matter tests fail closed; Matter deletion cascades Review rows. |
| Review context provenance | VERIFIED_AUTOMATED | New context citations attach only to current canonical documentary SourceSpans. Reopened references are classified current/historical/missing and remain contextual rather than provenance that a participant spoke cited wording. |
| Review Web surface | VERIFIED_AUTOMATED | Matter navigation includes `Review`; structured JSON import preserves exact wording and HR_SAID / USER_ACTUALLY_SAID / AI_SUGGESTED, rejects NUL input and Reviews spanning more than 24 hours before submission, saved sessions reopen, transcript whitespace/line breaks render with whitespace-preserving safe text rendering, stale navigation requests are scoped out, and authorized exact Matter sources can be inspected. |
| Build / automated tests | VERIFIED_AUTOMATED | Current hardening candidate head `6e041faaea021df52f62e0f5013cd99133048a08`; exact-head CI `33170725521` passed Windows, PostgreSQL, object storage, ingestion/scanner/OCR, QA, Web build and real-browser Playwright/axe jobs. The corresponding squash merge is `f000a4f26633acea591f081acfc0757e50d5e5b5`. No post-merge `main` CI is claimed here. |
| Legacy Windows Live runtime | PARTIAL_LOCAL | The WPF app, manual fallback, overlay, built-in Realtek microphone capture and live model/transcription paths were exercised on Windows 11 on 2026-09-05. These remain compatibility/runtime prototypes and are not the commercial evidence authority. |
| Process-specific Teams capture | PARTIAL | System-fallback and microphone stop/start produced real frames without raw-audio files. No second Teams participant was available, so process-specific remote ownership, mute/exclusion/reconnect and alternate audio endpoints remain open. |
| Live OpenAI transcription | PARTIAL | Real microphone audio produced USER-owned live partial/final events after the provider protocol defect was fixed on branch `fix/live-transcription-session-protocol` at `f189ef4282a182f2dc95216610fe87b0d21c45b7`. Accuracy/latency varied; remote/two-source/reconnect behaviour remains open. |
| WPF overlay | PARTIAL | With Teams foregrounded, the overlay remained visible without stealing focus; SAY/WATCH/ASK and no-evidence state were distinguishable. Evidence-backed locator display remains open. Overlay shutdown was fixed and directly retested on branch `fix/overlay-shutdown-lifecycle` at `fc2485d5e2dec84663d9b59548bc584b79b6ffac`. |
| Windows credential storage | VERIFIED_LOCAL | Save/read/update/read/delete/absence were exercised through Windows Credential Manager with temporary values, then the intended user credential was saved for live evaluation. No secret value was printed or added to Git. |
| Real latency/endurance | PARTIAL | Ten real answer-path samples measured 2.61-second median and 4.29-second p95, but the displayed wording failed the tester's naturalness judgment and live transcription first-useful timing varied up to 13.01 seconds. The mandatory 30+ minute rehearsal remains NOT_ATTEMPTED. |

## Milestone 13 and follow-up invariants now merged

- Transcript wording is attributed conversation material, not documentary fact.
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` remain distinct.
- Uploaded Review persistence does not create a parallel `Assertion`/`SourceSpan` evidence store.
- Submitted transcript wording is preserved exactly; blank/oversized input is rejected.
- Accepted leading/trailing whitespace, repeated spaces, tabs and line breaks are retained and rendered without unsafe raw HTML injection.
- NUL (`\u0000`) transcript characters are rejected deterministically before PostgreSQL persistence.
- Transcript input must be chronological and tied timestamps retain supplied order.
- Web and server validation both reject a Review whose overall duration exceeds 24 hours.
- New context citations may resolve only to current canonical documentary evidence.
- A Matter context citation does not prove that a participant spoke the cited wording.
- Saved Review citation identifiers survive later source unavailability and are re-evaluated as current/historical/missing rather than silently rewritten.
- Exact source text is retrieved on demand through the authenticated tenant/Matter/source-detail boundary and is not duplicated into Review storage.
- Cumulative tenant/Matter Review session and byte quotas are enforced before persistence.
- Review transcript bodies must not enter ordinary telemetry/log labels.
- Review responses are private/no-store.
- Matter privacy deletion must not orphan persisted Review transcript text.
- Deterministic Review prompts preserve uncertainty and do not decide truth or provide legal merits/win/compensation conclusions.
- No real raw audio is persisted by Milestone 13 or its follow-up hardening.

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
