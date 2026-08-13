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
The most recent 32 actual transcript turns plus compacted earlier actual transcript. Compaction preserves speaker ownership, chronology, and the original spoken text; it is not an AI-generated factual summary. Structured open-question or commitment tracking is intentionally not claimed until a reliable extraction path is implemented and validated.

### Tier 3 — Retrieved evidence
The few most relevant chunks from local source documents, plus source metadata.

The answer request uses all three tiers. It should never upload the whole archive by default.

## Trust hierarchy

When records conflict, preserve all evidence but prefer:

1. primary source document/email
2. explicitly verified fact ledger entry
3. actual meeting transcript, including deterministic compacted transcript context
4. explicit user-position/context record
5. generated historical summary, if one is introduced later

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

## Live-turn policy

Durable transcript recording and AI answer generation are separate operations. A new final transcript turn immediately makes any older pending answer stale. The old assistance request is cancelled, the transcript remains recorded, and only the newest generation may publish to the overlay. Live assistance has a six-second end-to-end budget across optional AI analysis plus answer generation; budget expiry is surfaced as a non-fatal live error instead of allowing an obsolete answer to appear later.

## Failure modes

`FULL`: capture + transcription + retrieval + assistant all healthy.

`TRANSCRIPT_ONLY`: assistant/retrieval API unavailable; persist transcript and keep meeting usable.

`MANUAL`: audio pipeline unavailable; user can paste/type the latest HR turn and still request a grounded response.

## Audio strategy

The Windows capture layer uses NAudio 3 application loopback for the explicitly selected, recognised Microsoft Teams root process and its descendants, plus an independent default-microphone WASAPI recorder. Both streams are converted in memory to 24 kHz PCM16 mono frames; raw audio is not written. A system-loopback source remains available only as an explicitly labelled diagnostic fallback and never counts as Teams-isolation evidence. Process capture still requires real Teams/device validation before Gate 1 can be `VERIFIED`.
