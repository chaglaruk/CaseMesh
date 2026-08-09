# Architecture

## Design principle

This is not a general meeting product. It is a local-first, one-user assistant optimized for a single high-value path: a live Teams HR conversation.

## Runtime pipeline

```text
Teams/remote audio --------> Remote capture ----> Realtime transcription --┐
                                                                            |
Microphone ----------------> Mic capture -------> Realtime transcription --+--> Meeting state
                                                                            |         |
                                                                            |         +--> Fast intent/cue analysis
                                                                            |                    |
Local case DB <---- import/chunk/FTS5 <---- documents/emails                |                    +--> Retrieval query
      |                                                                                          |
      +---------------- verified facts + evidence snippets <------------------------------------+
                                                                                                 |
                                                                                                 v
                                                                                     Grounded answer request
                                                                                                 |
                                                                                         SAY / WATCH / ASK
                                                                                                 |
                                                                                             WPF overlay
```

## Context tiers

### Tier 1 — Case core
Small, stable, explicit records: people, objectives, verified facts, user positions, important dates.

### Tier 2 — Current meeting state
Recent actual transcript, concise rolling summary, open questions, commitments, unresolved items.

### Tier 3 — Retrieved evidence
The few most relevant chunks from local source documents, plus source metadata.

The answer request uses all three tiers. It should never upload the whole archive by default.

## Trust hierarchy

When records conflict, preserve all evidence but prefer:

1. primary source document/email
2. explicitly verified fact ledger entry
3. actual meeting transcript
4. explicit user-position/context record
5. generated historical summary

A summary is never allowed to silently overwrite a primary source.

## Retrieval

v0.1 uses:

- normalized keyword search
- SQLite FTS5/BM25
- basic metadata/recency hints

True embedding-based semantic search is a later optional layer and must not block meeting readiness.

## AI roles

- transcription: live transcription model
- fast intent/cue step: lightweight model or deterministic logic
- spoken answer: high-quality answer model

All model IDs are configuration values, not hard-coded business logic.

The Realtime WebSocket connection and its transcription worker are separate model roles. The connection uses the current documented Realtime connection model; `audio.input.transcription.model` remains `gpt-live-transcribe` inside the dedicated transcription session.

## Failure modes

`FULL`: capture + transcription + retrieval + assistant all healthy.

`ASSISTANT_DEGRADED`: both actual-speech transcription streams remain healthy, but retrieval/answer generation is unavailable. Transcript persistence continues.

`TRANSCRIPTION_DEGRADED`: at least one actual-speech source is unavailable. The UI identifies HR vs USER reconnecting separately and keeps manual entry available.

`TRANSCRIPTION_GAP` is sticky historical metadata rather than the current primary state. Current HR/USER reconnect or failure remains visible while the UI also warns that the transcript may be incomplete. The app exposes source-specific accepted/sent/dropped counts and queue high-water marks without logging PCM or text.

`MANUAL`: audio pipeline unavailable; user can paste/type the latest HR turn and still request a grounded response.

## Live concurrency

Actual final turns use a short serialized SQLite-first ingestion path. SQLite reports whether a turn was inserted or already durable; only a successful new insert enters `MeetingState`, raises the actual-turn UI event, or starts assistance. Retrieval, optional Luna analysis and the Sol request run outside that boundary. Live manual HR turns use this same lifecycle. Any newer real speech advances a conversation generation, cancels obsolete model work where possible and prevents late SAY/WATCH/ASK output from rendering. Automatic assistance has an eight-second live-use timeout; this is a safety cap, not a target or latency claim.

Stop waits a bounded interval for in-flight actual-turn ingestion, but does not cancel or dispose synchronization still owned by a slow durable insert. Once stopped/disposed, no new callbacks are accepted and any already-entered insert cannot start assistance.

Each Realtime transcriber owns one sender worker and a 12-frame bounded queue. Overflow drops the oldest queued frame in favour of current audio and marks a visible transcription gap. Frames encountered while disconnected are also dropped and counted rather than replayed after the conversation has moved on.

## Audio strategy

The target is process-specific capture of Microsoft Teams plus separate microphone capture. Process capture follows Microsoft's current Application Loopback sample: `ActivateAudioInterfaceAsync`, include-target-process-tree activation, 44.1 kHz/16-bit/stereo PCM, shared-mode `AUTOCONVERTPCM`, and byte counts derived from block alignment. The repository includes a labelled system-loopback fallback and an audio probe, but a real Teams/headset call is still required before calling Gate 1 verified.

Protocol references checked on 2026-08-09:

- [OpenAI Realtime transcription guide](https://developers.openai.com/api/docs/guides/realtime-transcription)
- [OpenAI Realtime WebSocket guide](https://developers.openai.com/api/docs/guides/realtime-websocket)
- [OpenAI Realtime server events](https://developers.openai.com/api/reference/resources/realtime/server-events)
- [Microsoft Application Loopback sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback)
