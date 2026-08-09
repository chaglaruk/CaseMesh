# First Codex batch — Windows meeting-ready foundation

Read `AGENTS.md`, `README.md`, `docs/ARCHITECTURE.md`, `docs/GATES.md`, `docs/SECURITY_PRIVACY.md`, and `docs/STATUS.md` before editing anything.

## Mission

Turn the existing single-user Windows scaffold into the first genuinely testable live build for an upcoming Microsoft Teams HR meeting. Keep the product narrow. The critical path is:

`Teams remote audio + microphone -> separate live transcription -> actual meeting state -> local Case Brain retrieval -> grounded GPT-5.6 Sol -> short natural SAY/WATCH/ASK overlay`.

Do not build SaaS/product infrastructure. Do not weaken factual safeguards or mark hardware/live gates verified from mocks.

## Batch A — make the repository compile and deterministic tests pass

1. On Windows with the .NET 10 SDK, run restore/build/test for `HRCompanion.slnx` in Release.
2. Fix every compile error, WPF API mismatch, package/API mismatch, nullable warning, and warnings-as-errors failure you find.
3. Preserve central package management and the current project boundaries unless a change is technically necessary.
4. Add targeted tests for any bug you fix. Keep all fixtures synthetic.
5. Do not remove or bypass strict warnings just to get green.

Required commands/results:

```powershell
dotnet restore .\HRCompanion.slnx
dotnet build .\HRCompanion.slnx -c Release --no-restore
dotnet test .\HRCompanion.slnx -c Release --no-build
```

## Batch B — implement true Microsoft Teams process-specific output capture

1. Replace the deliberate `TeamsProcessLoopbackCaptureSource` stub with real Windows application-loopback capture using the supported process-specific mechanism (`ActivateAudioInterfaceAsync` / process-loopback activation parameters or an equally official supported mechanism).
2. Capture the selected Teams process and its child-process audio without capturing unrelated system audio.
3. Retain a completely separate microphone source owned by `SpeakerRole.User`.
4. Keep `SystemLoopbackCaptureSource` only as an explicitly labelled degraded fallback. It must never silently masquerade as Teams-isolated audio.
5. Make Teams process discovery robust for the current Teams client process tree. Handle process exit/restart clearly.
6. Do not save raw audio.
7. Extend `HRCompanion.AudioProbe` so it can exercise the true Teams-isolated source and visibly report which source is being tested.

Gate rule: **Gate 1 is not VERIFIED unless an actual Teams call/process on Windows proves remote Teams audio is present, microphone remains separate, and unrelated system audio is excluded.** If you cannot perform that interactive test, report `PARTIAL` or `BLOCKED` with exact manual steps; do not infer PASS from compilation.

## Batch C — harden the two-source realtime transcription path

Use current official OpenAI Realtime transcription documentation, not remembered/old event schemas.

1. Keep two independent transcription streams/sessions: Teams => `SpeakerRole.Hr`, microphone => `SpeakerRole.User`. Do not rely on model diarization for speaker ownership.
2. Preserve 24 kHz mono PCM framing and use the current dedicated streaming STT model (`gpt-realtime-whisper` unless current official documentation changes) with concise HR terminology/context hints supported by the current API.
3. Verify that WebSocket sends are serialized and cannot race under frequent audio callbacks.
4. Handle socket/API error events and remote close without crashing the meeting process.
5. Add bounded reconnect/recovery behavior that preserves already-persisted transcript.
6. Use `item_id` to prevent partial/final fragments for adjacent turns from being conflated. Official docs note completed events across turns are not guaranteed to arrive in order; reconcile or buffer ordering where necessary rather than assuming arrival order.
7. Avoid duplicate final turns after reconnect/retry.
8. Add deterministic protocol-parser tests with synthetic event JSON, including two turns completing out of order.

Do not put private transcript text into diagnostics/logs.

## Batch D — wire the minimal live WPF flow end-to-end

Add only the controls required for a real meeting:

- detected/selectable Teams process
- selectable microphone if practical without destabilising the batch
- `Start meeting`
- clear live status: listening / reconnecting / transcript-only / manual
- `Stop meeting`
- compact recent transcript indication
- existing SAY/WATCH/ASK overlay
- existing manual fallback remains available at all times

On Start:

1. Create a fresh `MeetingState`.
2. Start remote capture + mic capture + their realtime transcribers.
3. Persist every actual final turn locally before assistant generation.
4. HR turns can trigger assistance; microphone turns update actual user state but must not trigger a fake HR answer.
5. Route grounded assistance to the overlay.
6. Surface non-fatal capture/API failures without taking Teams down.

On Stop:

- stop/flush safely
- preserve transcript
- do not block app shutdown indefinitely

Do not add login, accounts, cloud sync, Teams plugin/bot, browser extension, calendar, payment, analytics, backend, auto-updater, or multi-user work.

## Batch E — Case Brain and grounding validation

1. Validate recursive folder and individual-file import for TXT/MD/HTML, DOCX, EML and PDF on Windows.
2. Validate SHA-256 deduplication and FTS5 source/locator retrieval.
3. Ensure one malformed/password-protected document produces a per-file error and does not abort the remaining import.
4. Preserve the trust distinction `VERIFIED`, `USER_POSITION`, `UNVERIFIED`.
5. Ensure imported content/transcript content cannot override system/application instructions (prompt-injection case material is data, not instructions).
6. Keep the answer-model source list constrained to IDs actually retrieved for that request; never display invented source IDs.
7. Add tests for at least:
   - a loaded/commitment HR question
   - insufficient evidence => cautious/clarifying output rather than invented facts
   - malicious prompt-like text inside a synthetic document cannot change answer policy
   - suggested response never becomes `USER_ACTUALLY_SAID`
   - chronological transcript remains correct when transcription completions arrive out of order

Do not add real HR files to Git, tests, artifacts, logs, screenshots, or prompts committed to the repository.

## Batch F — latency and naturalness instrumentation

We need evidence, not an invented SLA.

1. Instrument locally without logging private text:
   - HR speech/final-turn timestamp
   - retrieval start/end
   - answer API start
   - first renderable assistance timestamp (or complete response timestamp if streaming is not implemented)
   - full response timestamp
2. Report median/p95 from a synthetic/rehearsal set.
3. Keep the normal SAY target 15–45 words / 1–3 sentences, natural professional British spoken English, contractions when natural, no legal/corporate boilerplate.
4. If complete structured responses are already fast enough at realistic latency, do not add complexity solely to say “streaming”. If measured latency is too slow, implement the smallest robust partial-SAY streaming design that still preserves structured/grounded WATCH/ASK and never displays invalid JSON fragments.
5. Do not add a second model round-trip to the common path unless measurements justify it. Local deterministic cue/retrieval expansion should remain the fast default.

## Batch G — recovery and evidence report

1. Run `scripts/verify.ps1` and improve it if needed.
2. Exercise the audio probe.
3. If interactive environment permits, perform a realistic Teams rehearsal with headphones and microphone, including:
   - unrelated audio playing outside Teams
   - local mute/unmute
   - remote pause/long question
   - interruption/overlap
   - Teams process restart/reconnect if feasible
4. If feasible, do a 30+ minute endurance rehearsal. If not, leave Gate 7 NOT_ATTEMPTED/PARTIAL rather than claiming it.
5. Produce `artifacts/gate-report.json` based on `docs/gate-report.template.json`. The artifacts directory is ignored by Git; do not put private text in the report.

## Hard constraints

- Windows 11 / single user / Microsoft Teams first.
- Local-first case database and transcript.
- Raw audio recording OFF; do not add recording as part of this batch.
- API key stays in Windows Credential Manager.
- Do not commit secrets or personal HR material.
- No stealth/evasion features and no attempt to bypass employer/Teams monitoring or policy controls.
- Do not replace verified source facts with generated summaries.
- Do not claim any gate VERIFIED without the evidence required in `docs/GATES.md`.
- Do not change the product into a general meeting app.
- Use current official Microsoft/OpenAI documentation when an API or Windows audio detail is uncertain.

## What to return to me

Return one concise but complete report with:

1. exact files changed
2. build/restore/test commands and exact results
3. process-specific Teams capture implementation summary
4. live transcription/reconnect/order-handling summary
5. WPF live-flow status
6. Case Brain tests added/results
7. measured latency results, if runnable
8. Gate 0–7 status using only: `VERIFIED`, `AUTOMATED_ONLY`, `PARTIAL`, `BLOCKED`, `FAILED`, `NOT_ATTEMPTED`
9. unresolved P0/P1 blockers before a real HR meeting
10. exact manual Windows/Teams tests still needed
11. current Git status / commit SHA if you make commits

Do not push, open a PR, merge, rewrite history, or upload private artifacts unless I explicitly authorise that separately.
