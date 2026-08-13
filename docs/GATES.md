# Meeting-readiness gates

## Gate 0 — Build and deterministic tests

**Pass when:** Windows Release build and all automated tests succeed.

The deterministic test suite must include the safety-critical HR exchange corpus in `evals/hr-exchanges.json`; it is not acceptable for that corpus to exist only as documentation.

## Gate 1 — Audio ownership

**Goal:** remote Teams audio and microphone are separate, stable sources.

Required real-device evidence:

- Teams remote speaker appears only as `HR/REMOTE` input.
- User microphone appears only as `USER` input.
- Headphones/Bluetooth are tested.
- Mute/unmute is tested.
- A non-Teams audio source does not pollute process-specific Teams capture.
- Restart/reconnect behavior is observed.

Compilation or mocked audio is `AUTOMATED_ONLY`, never `VERIFIED`.

## Gate 2 — Live transcription

Required:

- two source streams retain correct role labels
- partial/final text is stable enough for a live meeting
- interruptions and pauses do not create obvious false turns
- reconnect does not erase persisted transcript

## Gate 3 — Case Brain

Required:

- TXT/MD, DOCX, EML, PDF ingest
- recursive folder import
- SHA-256 deduplication
- local FTS5 retrieval
- source metadata is preserved
- Git never sees imported case data

## Gate 4 — Grounded assistance

Required:

- latest HR question -> relevant evidence -> structured `SAY/WATCH/ASK`
- missing evidence produces clarification/caution, not invention
- source IDs in output resolve to real retrieved evidence
- suggested answer never becomes `USER_ACTUALLY_SAID` unless captured from microphone transcript
- loaded framing and commitment traps do not silently become user agreements
- retrieved source locators are visible in the live overlay when case evidence is used

Before meeting-ready status, run the safety-critical exchange corpus against the configured live answer model as a manual/release eval. Deterministic cue tests are necessary but do not by themselves validate model wording or forbidden claims.

## Gate 5 — Naturalness and latency

Target UX:

- normal `SAY`: 15–45 words / 1–3 sentences
- natural spoken British English
- no legal/corporate boilerplate
- first useful text appears quickly enough for a natural conversational pause
- a newer final turn invalidates older in-flight assistance; stale answers must never appear later
- the live assistance path has a bounded latency budget and degrades without blocking transcript persistence

Record median and p95 latency from realistic rehearsal. Do not hard-code an unverified SLA claim from model/network assumptions.

## Gate 6 — Overlay usability

Required:

- always-on-top overlay remains readable beside Teams
- overlay does not steal focus during normal use
- `SAY`, `WATCH`, and `ASK` are visually distinct
- evidence-backed vs no-case-evidence state is visible at a glance
- manual fallback remains usable if audio fails

## Gate 7 — Endurance/recovery

Required:

- 30+ minute rehearsal
- transcript persisted incrementally
- conversations longer than the recent-turn window retain older actual speaker-attributed context
- API failure degrades gracefully
- app restart can recover the unfinished local meeting
- no raw audio is saved unless explicitly enabled

## Meeting-ready definition

All Gates 0–7 must be `VERIFIED`, except optional enhancements explicitly outside v0.1.
