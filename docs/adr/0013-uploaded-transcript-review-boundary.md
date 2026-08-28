# ADR 0013 — Persist uploaded transcript Review separately from canonical evidence

Date: 2026-08-28

Status: Accepted

## Context

CaseMesh Milestone 12 established a canonical Matter -> Live/Review boundary over the PostgreSQL Matter system of record. The follow-up hardening in Issue #31 / PR #32 bounded source payloads, preserved provenance/currentness metadata and added a tenant-scoped exact-source detail path.

The next product capability is reviewing a workplace meeting after it happened. A transcript is useful conversation material, but it is epistemically different from documentary evidence. Treating uploaded transcript wording as a canonical `Assertion` or `SourceSpan` merely because the user uploaded it would weaken the central CaseMesh guarantee that documentary provenance is explicit and auditable.

The product also needs saved Review sessions to remain useful when the Matter later changes. A source that was current when attached to a Review may later become historical or disappear from the current projection. The Review must retain that historical context reference without pretending it is still current.

## Decision

### Separate persisted Review records

Uploaded meeting Reviews are persisted in dedicated tenant/Matter-scoped PostgreSQL tables:

- `uploaded_meeting_reviews`;
- `uploaded_meeting_review_items`;
- `uploaded_meeting_review_context_citations`.

These rows are conversation/review state, not canonical evidence truth. They do not create `Assertion`, `Event`, `DocumentVersion` or `SourceSpan` records.

All three tables use composite tenant/Matter ownership, RLS/FORCE RLS and restricted runtime-role grants. Their Matter foreign key uses `ON DELETE CASCADE`, so the supported Matter privacy lifecycle cannot leave transcript text behind.

### Explicit conversation origin

Each transcript item retains one of exactly three origins:

- `HrSaid`;
- `UserActuallySaid`;
- `AiSuggested`.

`AiSuggested` is never rewritten as something the user actually said. Speaker wording remains attributed conversation material even when Matter context is displayed beside it.

### Context citations are contextual, not spoken provenance

At Review creation time, optional context citation IDs are accepted only when they resolve to **current canonical documentary evidence** in the Matter. `UploadedMeetingReviewBuilder` validates that boundary before persistence.

A context citation means only that the source was displayed or associated as Matter context for the transcript item. It does not mean the cited document proves that the participant spoke the transcript wording.

When a saved Review is reopened, its persisted source references are compared with the current canonical Matter projection and reported as:

- `Current`;
- `Historical`;
- `Missing`.

The Review citation table deliberately does **not** hold a foreign key to `source_spans`. The canonical source must exist and be current when a new citation is attached, but the saved citation ID is thereafter historical Review state. If a document version or SourceSpan is later removed, that ID remains in the Review so it can be surfaced as `Missing` rather than silently erased. Matter deletion still removes the Review itself through the Review-to-Matter cascade.

A Review is not destroyed or rewritten merely because the canonical Matter later changes. Exact source text is still retrieved, when available and authorized, through the existing hardened tenant/Matter/source-detail endpoint rather than copied into Review rows.

### Bounded deterministic transcript contract

The first commercial Review import is structured JSON rather than audio/provider transcription. Server-side validation remains authoritative and bounds at least:

- item count;
- text length per item and in aggregate;
- total Review duration;
- non-empty distinct item IDs;
- defined origin enum values;
- valid start/end ordering;
- distinct bounded context citations.

The Web parser additionally rejects transcript arrays whose start timestamps move backwards so the user is not shown an apparently reordered conversation. The server keeps deterministic ordering for direct API clients and preserves input order as the final tie-breaker when timestamps are identical.

Valid transcript wording is preserved exactly after validation. Leading and trailing whitespace, internal spacing and line breaks are retained; the import boundary does not trim, collapse or otherwise normalize accepted transcript text. Invalid blank, oversized, NUL-containing or over-duration input is rejected before PostgreSQL persistence. The Web parser mirrors the server bounds for early feedback but cannot weaken API validation.

### Cumulative pilot quotas

Per-Review bounds are not sufficient protection for persistent transcript storage. Closed-pilot entitlements therefore also cap cumulative Review session count and UTF-8 transcript bytes at both Matter and tenant scope.

`PostgresUploadedMeetingReviewRepository` acquires a tenant-scoped transactional advisory lock, reads the persisted entitlement and current Review usage, and rejects the write before any transcript body is inserted when a session or byte limit would be exceeded. This keeps concurrent Review creation from oversubscribing the configured allowance. Review quota rejection uses the existing typed pilot quota exception and 429 API surface.

### Deterministic first Review analysis

The first Review analysis is deterministic. It may expose:

- current/historical/missing status of attached Matter context;
- unresolved canonical contradictions relevant to cited Matter sources;
- conservative verification prompts such as locating documentary support for an attributed meeting statement or comparing stale context with current evidence.

It does not decide which speaker is truthful, create legal merits conclusions, estimate compensation/win probability or promote transcript/model content into established fact.

### Privacy and caching

Authenticated Review create/list/read/source responses are private and non-cacheable (`Cache-Control: no-store, private`). Ordinary logs and telemetry must not contain transcript bodies or cited evidence bodies.

The Web Review surface resets Matter-scoped transcript, saved-session and exact-source state when the workspace/Matter scope changes and ignores stale async responses from the prior scope.

No raw audio is persisted or processed by this milestone.

## Consequences

### Positive

- Saved meeting Reviews can evolve alongside the Matter without becoming a second evidence authority.
- Speaker identity and AI suggestions remain explicit.
- Context source currentness can change without erasing historical Review state.
- Deleted source material can be represented honestly as missing historical context instead of silently losing the prior citation.
- Persistent transcript growth is constrained by tenant/Matter entitlements before writes occur.
- Matter deletion naturally removes Review transcript rows through the existing privacy lifecycle.
- The first Review product can ship and be tested without making unsupported real-time/audio claims.

### Trade-offs

- Structured JSON import is less convenient than uploading arbitrary recordings; provider transcription is deliberately deferred.
- Review source references must be re-evaluated against current canonical state when reopened.
- Saved citation IDs intentionally outlive individual SourceSpan rows and therefore rely on application creation-time validation plus Matter-scoped Review ownership rather than a live SourceSpan foreign key.
- Transcript text is persisted because reopening the Review is a product requirement, so its privacy boundary must be treated as sensitive tenant data even though it is not documentary evidence.

## Deferred

This ADR does not approve or verify:

- audio/video transcription providers;
- Teams/system/microphone capture;
- real-time transcription;
- live overlay assistance;
- Windows Credential Manager runtime gates;
- real latency/endurance measurements;
- a 30+ minute meeting rehearsal.

Those remain governed by `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`, `docs/GATES.md` and `docs/STATUS.md`.