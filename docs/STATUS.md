# Status

Last update: 2026-08-28.

The commercial Matter evidence platform through Milestone 12 and its post-merge hardening are merged. Milestone 13 is now in progress through Issue #33 / draft PR #34: **uploaded meeting transcript Review** on the hardened canonical boundary. This repository work does **not** upgrade any real Windows/Teams/audio readiness gate.

## Commercial milestone state

| Area | Status | Notes |
|---|---|---|
| Canonical Matter -> Live/Review boundary | MERGED | Milestone 12 merged as `0bb9124a6ba91a7028553c9e4928bb36570d6708`. Follow-up Issue #31 / PR #32 merged as `3fa41a5b93146913e1f1364dead25dd9149073fd`, resolving late correctness/performance/privacy findings. |
| Uploaded meeting transcript Review | IN_PROGRESS | Issue #33 / draft PR #34 adds persisted tenant-scoped transcript Reviews, explicit HR/user/AI origins, current-only context citation creation, saved Review reopening, deterministic context/contradiction analysis and a Web Review surface. |
| Commercial evidence authority | VERIFIED_FOR_ARCHITECTURE | Canonical PostgreSQL Matter/Matter Brain remains the only commercial evidence system of record. Uploaded transcript Review rows are conversation/review state and do not become canonical evidence merely by being uploaded. |
| Review privacy / tenant isolation | IN_PROGRESS_VALIDATION | Review tables use tenant/Matter composite ownership and RLS/FORCE RLS; Review HTTP surfaces are private/no-store; Matter deletion is designed to cascade Review rows. Exact-head CI/review must still pass before merge. |
| Review context provenance | IN_PROGRESS_VALIDATION | New context citations may attach only to current canonical documentary SourceSpans. When a saved Review is reopened, references are classified current/historical/missing. They never become provenance that a participant spoke the cited wording. |
| Review Web surface | IN_PROGRESS | Matter navigation includes `Review`; structured JSON import preserves HR_SAID / USER_ACTUALLY_SAID / AI_SUGGESTED, saved sessions can be reopened and authorized exact Matter sources inspected. |
| Build / automated tests | IN_PROGRESS | Initial Milestone 13 CI exposed test-fixture/migration-expectation issues; those were corrected. Exact-head full CI remains required before independent review. Automated success will prove software behavior only, not real Teams/audio behavior. |
| Legacy Windows Live runtime | AUTOMATED_ONLY | Existing WPF/SQLite/audio projects remain buildable compatibility/runtime prototypes. They are not the commercial evidence authority. |
| Process-specific Teams capture | AUTOMATED_ONLY | Application-loopback architecture exists, but a real Teams process-specific ownership run is still required. |
| Live OpenAI transcription | PARTIAL | Realtime client/two-source ownership compile; real reconnect/order/provider behavior remains local/live work. |
| WPF overlay | PARTIAL | SAY/WATCH/ASK and evidence state compile; focus/runtime validation with Teams foreground remains pending. |
| Windows credential storage | AUTOMATED_ONLY | Implementation compiles; actual Windows Credential Manager behavior remains a local gate. |
| Real latency/endurance | NOT_VERIFIED | Median/p95 and 30+ minute rehearsal remain local/live gates. |

## Milestone 13 invariants

- Transcript wording is attributed conversation material, not documentary fact.
- `HR_SAID`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` remain distinct.
- Uploaded Review persistence does not create a parallel `Assertion`/`SourceSpan` evidence store.
- New context citations may resolve only to current canonical documentary evidence.
- A Matter context citation does not prove that a participant spoke the cited wording.
- Saved Review citations are re-evaluated as current/historical/missing when the Matter changes rather than silently rewritten.
- Exact source text is retrieved on demand through the authenticated tenant/Matter/source-detail boundary and is not duplicated into Review storage.
- Review transcript bodies must not enter ordinary telemetry/log labels.
- Review responses are private/no-store.
- Matter privacy deletion must not orphan persisted Review transcript text.
- Deterministic Review prompts preserve uncertainty and do not decide truth or provide legal merits/win/compensation conclusions.
- No real raw audio is persisted by Milestone 13.

## Current implementation checkpoint

Issue #33: `Milestone 13: uploaded meeting transcript Review`

Draft PR #34: `Add uploaded meeting transcript Review`

Branch: `issue-33-uploaded-transcript-review`

Implemented in the current draft:

- migration `0010_uploaded_meeting_reviews.sql`;
- persisted Review/session/item/context-citation repository;
- bounded `UploadedMeetingReviewBuilder` validation;
- deterministic current/historical/missing context and contradiction analysis;
- authenticated create/list/read Review APIs;
- private/no-store Review responses;
- real-PostgreSQL cross-tenant/current-source/cascade-delete coverage;
- Matter Web `Review` navigation, JSON transcript import, saved-session reopen and source inspection;
- deterministic Web transcript parser tests;
- ADR 0013.

The PR must remain draft until exact-head CI and product/docs work are stable.

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