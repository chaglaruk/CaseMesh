# Meeting-readiness gates

## Gate 0 — Build and deterministic tests

**Status: VERIFIED (local Windows, 2026-09-05).** At `8848b002b6f14d2ea65d1a72eb2b5bdec3762809`, the Release solution build passed with zero warnings/errors and the full solution run passed 309 tests, skipped 111 environment-gated tests and failed none. Focused defect branches were also built and tested separately; no remote CI is claimed for them.

**Pass when:** Windows Release build and all automated tests succeed.

The deterministic test suite must include the safety-critical HR exchange corpus in `evals/hr-exchanges.json`; it is not acceptable for that corpus to exist only as documentation.

## Gate 1 — Audio ownership

**Status: PARTIAL (local Windows, 2026-09-05).** The built-in Realtek microphone and speakers delivered continuous packets in two system-fallback probe stop/start cycles without raw-audio persistence. Realtek microphone frames also crossed the live OpenAI transcription boundary in repeated fresh sessions with USER ownership. A second Teams participant was unavailable, so remote Teams ownership, mute/unmute, non-Teams exclusion and connection recovery are blocked rather than failed. No wired-headphone endpoint was present; a Bluetooth controller was present but no Bluetooth audio endpoint was available.

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

**Status: PARTIAL (local Windows, 2026-09-05).** Real built-in Realtek microphone audio reached the live OpenAI Realtime transcription service as USER speech after the startup protocol defect was corrected on `fix/live-transcription-session-protocol` (`f189ef4282a182f2dc95216610fe87b0d21c45b7`). Two fresh `gpt-4o-mini-transcribe` sessions produced 11/15 non-empty delta updates and 2/1 final events with no capture failure. One final omitted opening words and created a separate short turn; another was substantively usable but misheard the product name. First-useful-update timing was 3.59 seconds and 13.01 seconds. Remote ownership, alternating two-source turns, overlap/interruption, pause/reconnect ordering and persisted-final recovery remain blocked by the unavailable second participant or otherwise unverified.

Required:

- two source streams retain correct role labels
- partial/final text is stable enough for a live meeting
- interruptions and pauses do not create obvious false turns
- reconnect does not erase persisted transcript

## Gate 3 — Case Brain

**Status: PARTIAL (local Windows, 2026-09-05).** A real local importer/repository run used representative synthetic-private files under `%LOCALAPPDATA%\Temp\CaseMesh.LiveValidation.20260905`, outside Git. Recursive TXT, Markdown, DOCX, EML, normal PDF and OCR-text-layer PDF import, SHA-256 duplicate rejection, retrieval and persisted source identifiers/locators passed; PDF results resolved to `p.1`, and Git stayed clean before and after. An image-only scanned PDF through an installed OCR engine was not exercised because Tesseract/Poppler were unavailable, so this gate is not VERIFIED.

Required:

- TXT/MD, DOCX, EML, PDF ingest
- recursive folder import
- SHA-256 deduplication
- local FTS5 retrieval
- source metadata is preserved
- Git never sees imported case data

## Gate 4 — Grounded assistance

**Status: PARTIAL (local Windows, 2026-09-05).** The live safety corpus passed against `gpt-5.6-sol` using the Windows Credential Manager credential. Ten additional real model calls rejected loaded framing and commitment traps, avoided unsupported absence/medical claims, used SAY/WATCH/ASK separation, and resolved a cited synthetic PDF to the expected persisted source and `p.1` locator. AI suggestions did not become captured USER speech. Evidence-backed locator rendering in the real overlay and full two-source meeting behaviour remain unverified, so this gate is not VERIFIED.

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

**Status: PARTIAL (local Windows, 2026-09-05).** Ten real answer-path samples on `gpt-5.6-sol` had a 2.61-second median and 4.29-second p95 (1.84-second minimum, 4.30-second maximum). Real microphone transcription first-useful timing varied from 3.59 to 13.01 seconds in the selected-model runs. The tester found the displayed manual-fallback wording insufficiently human, so the naturalness target did not pass. Runtime stale-generation cancellation still depends on real HR/remote final turns; only its automated coverage passed.

Target UX:

- normal `SAY`: 15–45 words / 1–3 sentences
- natural spoken British English
- no legal/corporate boilerplate
- first useful text appears quickly enough for a natural conversational pause
- a newer final turn invalidates older in-flight assistance; stale answers must never appear later
- the live assistance path has a bounded latency budget and degrades without blocking transcript persistence

Record median and p95 latency from realistic rehearsal. Do not hard-code an unverified SLA claim from model/network assumptions.

## Gate 6 — Overlay usability

**Status: PARTIAL (local Windows, 2026-09-05).** With Teams foregrounded, typed text remained in Teams while the topmost CaseMesh overlay stayed visible and did not take focus. Manual fallback produced and displayed assistance; SAY was visually prominent, WATCH/ASK headings used distinct colours, and NO CASE EVIDENCE was clear at a glance. The tester noted weak WATCH/ASK typography hierarchy. Evidence-backed locator display was not exercised. Closing the main window while the overlay was open exposed an orphan-process defect; `fix/overlay-shutdown-lifecycle` (`fc2485d5e2dec84663d9b59548bc584b79b6ffac`) fixed it, and a direct repeat confirmed that both windows and the process closed.

Required:

- always-on-top overlay remains readable beside Teams
- overlay does not steal focus during normal use
- `SAY`, `WATCH`, and `ASK` are visually distinct
- evidence-backed vs no-case-evidence state is visible at a glance
- manual fallback remains usable if audio fails

## Gate 7 — Endurance/recovery

**Status: NOT_ATTEMPTED.** No 30+ minute real rehearsal was performed on 2026-09-05. Short stop/start and application-close checks do not satisfy this gate.

Required:

- 30+ minute rehearsal
- transcript persisted incrementally
- conversations longer than the recent-turn window retain older actual speaker-attributed context
- API failure degrades gracefully
- app restart can recover the unfinished local meeting
- no raw audio is saved unless explicitly enabled

## Meeting-ready definition

All Gates 0–7 must be `VERIFIED`, except optional enhancements explicitly outside v0.1.
