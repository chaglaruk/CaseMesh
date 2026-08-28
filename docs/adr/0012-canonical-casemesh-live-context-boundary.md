# ADR 0012: Canonical CaseMesh Live context boundary

## Status

Accepted for the CaseMesh Live foundation.

## Context

The commercial Matter evidence chain is now canonical and tenant scoped through PostgreSQL-backed Matter state, Matter Brain currentness, exact SourceSpan provenance, grounded Q&A and meeting preparation. CaseMesh also retains an older Windows `MeetingState`/SQLite/WPF Live prototype that predates that commercial architecture.

Promoting the legacy meeting store into a second commercial evidence authority would reintroduce stale copies, ambiguous corrections and provenance drift. At the same time, real-time Windows audio, transcription, overlay, latency and endurance behaviour still requires real-device validation and cannot be inferred from automated tests.

The commercial roadmap therefore needs a platform-neutral boundary before further Live work.

## Decision

CaseMesh Live consumes canonical Matter state through an explicit, read-only `CanonicalLiveContextAdapter` in the platform-neutral `CaseMesh.Live` project.

The adapter:

- requires an explicit tenant id and Matter id and fails closed when they do not match the supplied canonical state;
- uses `CanonicalEvidencePolicy` rather than inventing a separate Live currentness policy;
- projects documentary assertions with exact SourceSpan, DocumentVersion, immutable original-object id and SHA-256 provenance;
- marks non-current documentary records as historical rather than silently presenting them as current;
- keeps AI inference in a separate collection that has model provenance and no documentary citation;
- preserves unresolved canonical contradictions as explicit context;
- carries an explicit processing/currentness state for callers that observe active ingestion.

Uploaded meeting/transcript review is introduced as a separate application contract before live capture. Conversation items retain one of the explicit origins `HrSaid`, `UserActuallySaid` or `AiSuggested`. Any Matter evidence references attached to those items are named and validated as **context citations**; they do not become provenance for what a participant actually said. Context citations may resolve only to current canonical documentary evidence.

The existing `MeetingState`, SQLite repository, WPF overlay and Windows audio projects remain compatibility/runtime components. They are not the commercial system of record and this decision does not claim that their real-world gates are verified.

## Consequences

- Canonical PostgreSQL Matter state remains the only commercial evidence authority used by future Live/Review capabilities.
- Live can evolve without copying canonical evidence into a new persistence model.
- AI suggestions cannot masquerade as user speech, HR speech or documentary evidence.
- Transcript statements may be reviewed alongside source-grounded context without inheriting those citations as proof of the spoken wording.
- Currentness rules remain shared with Matter Brain rather than drifting between Prepare and Live.
- Uploaded transcript review can be developed and tested before adding new real-time audio complexity.
- Real Teams/audio/transcription/privacy/latency/endurance promotion remains governed by `docs/GATES.md` and `docs/STATUS.md` and requires local evidence.

## Out of scope

This decision does not verify or redesign process-specific Teams capture, realtime transcription, Windows Credential Manager behaviour, overlay focus, live model latency, Bluetooth/headphone handling or 30+ minute recovery. It also does not add legal merits, outcome prediction, autonomous filing or a general-purpose meeting product.
