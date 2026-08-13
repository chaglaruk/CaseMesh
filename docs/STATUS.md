# Status

Last update: 2026-08-13.

| Area | Status | Notes |
|---|---|---|
| Repository architecture | PARTIAL | Narrow single-user Windows architecture, privacy rules, gates, and handoff authored; live hardware gates remain. |
| Build / automated tests | VERIFIED | Local Windows Release restore/build and all 30 automated tests pass on 2026-08-13. This does not verify live Teams/audio behaviour. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, chronological meeting state, deterministic older-turn compaction, six-second live budget, 1.5-second optional analysis budget, latest-turn-wins cancellation, durable final turns and fake overlap/timeout tests are implemented. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Local documents, chunks, facts, transcripts, FTS5/BM25 and tests authored. |
| TXT/MD/HTML/DOCX/EML/PDF ingestion | AUTOMATED_ONLY | Recursive import, SHA-256 deduplication, per-file failure isolation, source locators, empty/scanned-text detection and repeated-evidence de-crowding authored. Real representative-file validation pending. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Trust hierarchy, prompt-injection resistance, source-ID allow-listing, loaded-framing safeguards and natural spoken style authored. |
| Safety eval corpus | PARTIAL | `evals/hr-exchanges.json` is executed by deterministic CI tests; commitment cases carry machine-checkable SAY constraints. The opt-in real answer-model harness remains available, but no Credential Manager key or `OPENAI_API_KEY` was present for this update, so it was not run. |
| Windows credential storage | AUTOMATED_ONLY | P/Invoke implementation compiles; actual Windows Credential Manager behavior still needs local validation. |
| System-loopback + microphone diagnostics | PARTIAL | NAudio 3 WASAPI capture produced continuous 24 kHz PCM16 mono microphone frames on this laptop; Stop→Start, repeated Stop and active Dispose completed. System loopback stayed explicitly labelled and is not Gate 1 evidence. |
| Process-specific Teams capture | AUTOMATED_ONLY | NAudio `3.0.0-preview.20` application loopback captures the selected recognised Teams process tree only, with no system-loopback fallback. Conversion and invalid/arbitrary PID behaviour are tested, but no Teams process was running for a real isolated start/frame check. |
| Live OpenAI transcription | PARTIAL | Realtime transcription client and two-source ownership compile; end-to-end/reconnect/order validation pending. |
| GPT structured assistant | PARTIAL | Responses API structured assistant compiles; commitment-sensitive turns skip optional AI analysis, ambiguous non-commitment analysis is capped before deterministic fallback, and real latency validation is pending. |
| WPF overlay | PARTIAL | SAY/WATCH/ASK, evidence state, up to three unique source locators and model self-rating display compile; focus/runtime testing pending. |
| Live start/stop UI | AUTOMATED_ONLY | WPF exposes discovery/selection, Start Live and Stop Live; it constructs separate HR/User capture and transcribers, dispatches updates safely, and returns to restartable MANUAL mode on failure. Runtime Teams/UI validation remains. |
| Coordinator concurrency test | AUTOMATED_ONLY | Deterministic tests prove HR A cancellation by HR B, cancellation by User speech, B-only publication, persistence of all final turns, current-generation timeout, and clean stop with no active fake AI call. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting. |

## Known deliberate gaps

- Run the process-loopback probe while a real Teams call produces remote audio; verify HR-only ownership, mute/unmute, headphones/Bluetooth, non-Teams exclusion and restart/reconnect.
- Realtime reconnect/item ordering needs Windows/live API validation.
- Save an HR Companion OpenAI credential, then run the opt-in live answer-model safety eval once before meeting-ready status.
- Exercise Start Live / Stop Live and overlay focus behaviour in a real Teams session.
- Run a 30+ minute realistic rehearsal and record latency/recovery observations.

## 2026-08-13 local audio evidence

- Teams discovery: executed; zero Teams process trees were running, so isolated Teams Start/frames were not testable.
- Invalid PID: clean failure for PID `2147483647`; repeated Stop/Dispose tests pass.
- Default microphone: `Microphone Array (Realtek(R) Audio)` delivered 60 50-ms frames over three seconds, then delivered another 60 after Stop→Start; active disposal completed without a crash.
- Explicit system-loopback fallback: lifecycle exercised, but the stream was silent and is not accepted as Teams-isolation evidence.
- Raw audio persisted: none.

See `docs/GATES.md` for meeting-readiness evidence requirements.

Do not upgrade any Windows/live status without the corresponding evidence.
