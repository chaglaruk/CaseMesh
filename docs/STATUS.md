# Status

Last update: 2026-08-13.

| Area | Status | Notes |
|---|---|---|
| Repository architecture | PARTIAL | Narrow single-user Windows architecture, privacy rules, gates, and handoff authored; live hardware gates remain. |
| Build / automated tests | VERIFIED | Windows GitHub Actions restore, Release build, and automated tests pass on the reviewed hardening baseline. This does not verify live hardware/audio behavior. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, chronological meeting state, deterministic older-turn compaction, latest-turn-wins cancellation, bounded live response handling, and tests are implemented. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Local documents, chunks, facts, transcripts, FTS5/BM25 and tests authored. |
| TXT/MD/HTML/DOCX/EML/PDF ingestion | AUTOMATED_ONLY | Recursive import, SHA-256 deduplication, per-file failure isolation, source locators, empty/scanned-text detection and repeated-evidence de-crowding authored. Real representative-file validation pending. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Trust hierarchy, prompt-injection resistance, source-ID allow-listing, loaded-framing safeguards and natural spoken style authored. |
| Safety eval corpus | PARTIAL | `evals/hr-exchanges.json` is executed by deterministic CI tests; commitment cases must carry machine-checkable SAY constraints. An opt-in real answer-model eval harness exists but is not part of normal CI. |
| Windows credential storage | AUTOMATED_ONLY | P/Invoke implementation compiles; actual Windows Credential Manager behavior still needs local validation. |
| System-loopback + mic fallback | AUTOMATED_ONLY | NAudio fallback implementation compiles; real-device validation pending. |
| Process-specific Teams capture | NOT_ATTEMPTED | Critical Gate 1 blocker. `TeamsProcessLoopbackCaptureSource` remains a loud stub and requires supported Windows process-loopback implementation plus real-device validation. |
| Live OpenAI transcription | PARTIAL | Realtime transcription client and two-source ownership compile; end-to-end/reconnect/order validation pending. |
| GPT structured assistant | PARTIAL | Responses API structured assistant compiles; commitment-sensitive turns skip optional AI analysis, ambiguous non-commitment analysis is capped before deterministic fallback, and real latency validation is pending. |
| WPF overlay | PARTIAL | SAY/WATCH/ASK, evidence state, up to three unique source locators and model self-rating display compile; focus/runtime testing pending. |
| Live start/stop UI | NOT_ATTEMPTED | Still blocked on real Teams process-loopback implementation; do not wire a misleading system-audio path as meeting-ready. |
| Coordinator concurrency test | OPEN | Latest-turn-wins logic exists, but a fake audio/transcriber overlap integration test still needs to be added and run. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting. |

## Known deliberate gaps

- True Teams process-specific loopback capture must be implemented with the supported Windows application-loopback mechanism and tested on the user's Windows machine.
- Add an overlapping-final-turn coordinator integration test proving only the newest assistance publishes while all final transcripts persist.
- Realtime reconnect/item ordering needs Windows/live API validation.
- Run the opt-in live answer-model safety eval before meeting-ready status.
- Wire live Start/Stop only after process-specific Teams capture is implemented and validated.
- Run a 30+ minute realistic rehearsal and record latency/recovery observations.

See `docs/GATES.md` for meeting-readiness evidence requirements.

Do not upgrade any Windows/live status without the corresponding evidence.
