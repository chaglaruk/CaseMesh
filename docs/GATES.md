# Meeting-readiness gates

## Gate 0 — Build and deterministic tests

**Status: VERIFIED (local Windows, 2026-08-13).** `dotnet restore`, Release build and all 30 automated tests passed with zero warnings/failures.

**Pass when:** Windows Release build and all automated tests succeed.

The deterministic test suite must include the safety-critical HR exchange corpus in `evals/hr-exchanges.json`; it is not acceptable for that corpus to exist only as documentation.

## Gate 1 — Audio ownership

**Status: PARTIAL.** Process-tree capture is implemented and automated conversion/PID/lifecycle checks pass. A real microphone delivered continuous frames across restart on this laptop. No Teams process was running, so isolated remote frames, mute/unmute, headphones/Bluetooth, exclusion and reconnect remain unverified.

**Goal:** remote Teams audio and microphone are separate, stable sources.

Required real-device evidence:

- Teams remote speaker appears only as `HR/REMOTE` input.
- User microphone appears only as `USER` input.
- Headphones/Bluetooth are tested.
- Mute/unmute is tested.
- A non-Teams audio source does not pollute process-specific Teams capture.
- Restart/reconnect behavior is observed.

Compilation or mocked audio is `AUTOMATED_ONLY`, never `VERIFIED`.

Run the no-audio-persistence probe during a controlled Teams call:

```powershell
dotnet run --project .\tools\CaseMesh.AudioProbe\CaseMesh.AudioProbe.csproj -c Release -- --pid <TEAMS_ROOT_PID> --seconds 10
```

Play Teams remote speech, then separately play non-Teams system audio. Confirm Teams RMS rises only for the first, microphone RMS responds only to local speech, and both probe cycles complete. Repeat with mute/unmute, wired headphones and Bluetooth if available.

## Gate 2 — Live transcription

**Status: AUTOMATED_ONLY.** Separate role ownership and coordinator routing compile and are fake-tested; live API transcription, interruption behaviour and reconnect are still required.

Required:

- two source streams retain correct role labels
- partial/final text is stable enough for a live meeting
- interruptions and pauses do not create obvious false turns
- reconnect does not erase persisted transcript

## Gate 3 — Case Brain

**Status: AUTOMATED_ONLY.** Ingestion/retrieval tests pass; representative private files must be tested outside Git before promotion.

Required:

- TXT/MD, DOCX, EML, PDF ingest
- recursive folder import
- SHA-256 deduplication
- local FTS5 retrieval
- source metadata is preserved
- Git never sees imported case data

## Gate 4 — Grounded assistance

**Status: PARTIAL.** Deterministic grounding/commitment constraints pass. The live answer-model eval was not run because no configured credential was present.

Required:

- latest HR question -> relevant evidence -> structured `SAY/WATCH/ASK`
- missing evidence produces clarification/caution, not invention
- source IDs in output resolve to real retrieved evidence
- suggested answer never becomes `USER_ACTUALLY_SAID` unless captured from microphone transcript
- loaded framing and commitment traps do not silently become user agreements
- retrieved source locators are visible in the live overlay when case evidence is used

Before meeting-ready status, run the safety-critical exchange corpus against the configured live answer model as a manual/release eval. Normal CI does not call the API.

On the Windows machine where CaseMesh has already saved its API key:

```powershell
$env:CASEMESH_RUN_LIVE_EVALS = "1"
dotnet test .\tests\CaseMesh.Infrastructure.Tests\CaseMesh.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~LiveHrExchangeCorpus"
Remove-Item Env:CASEMESH_RUN_LIVE_EVALS
```

The harness uses the same Windows Credential Manager entry as the app. `OPENAI_API_KEY` may be used as a temporary override, and `CASEMESH_ANSWER_MODEL` may override the answer model for comparison runs.

## Gate 5 — Naturalness and latency

**Status: AUTOMATED_ONLY.** Six-second cancellation, 1.5-second optional-analysis fallback and stale-generation behaviour are covered; rehearsal median/p95 latency is open.

Target UX:

- normal `SAY`: 15–45 words / 1–3 sentences
- natural spoken British English
- no legal/corporate boilerplate
- first useful text appears quickly enough for a natural conversational pause
- a newer final turn invalidates older in-flight assistance; stale answers must never appear later
- the live assistance path has a bounded latency budget and degrades without blocking transcript persistence

Record median and p95 latency from realistic rehearsal. Do not hard-code an unverified SLA claim from model/network assumptions.

## Gate 6 — Overlay usability

**Status: AUTOMATED_ONLY.** Live dispatcher wiring and overlay rendering build; usability while Teams has focus has not been exercised.

Required:

- always-on-top overlay remains readable beside Teams
- overlay does not steal focus during normal use
- `SAY`, `WATCH`, and `ASK` are visually distinct
- evidence-backed vs no-case-evidence state is visible at a glance
- manual fallback remains usable if audio fails

## Gate 7 — Endurance/recovery

**Status: NOT_ATTEMPTED.** No 30+ minute real rehearsal was performed.

Required:

- 30+ minute rehearsal
- transcript persisted incrementally
- conversations longer than the recent-turn window retain older actual speaker-attributed context
- API failure degrades gracefully
- app restart can recover the unfinished local meeting
- no raw audio is saved unless explicitly enabled

## Meeting-ready definition

All Gates 0–7 must be `VERIFIED`, except optional enhancements explicitly outside v0.1.
