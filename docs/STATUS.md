# Status

Last update: 2026-08-09.

| Area | Status | Notes |
|---|---|---|
| Repository architecture | PARTIAL | Narrow single-user Windows architecture, privacy rules, gates, and handoff authored. |
| Build / automated tests | VERIFIED | GitHub Actions on Windows Server 2025 with .NET 10 completed restore, Release build, and tests successfully on the baseline. Review-branch CI is also enabled for independent validation of Codex/audit fixes. This does not verify live hardware/audio behavior. |
| Static source validation | AUTOMATED_ONLY | JSON/XML parse checks and a C# lexical/delimiter scan were also performed during authoring; CI compilation supersedes these for build validity. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, durable provider item IDs, chronological meeting state, cue engine, local HR aliases, persistence-before-assistance and timing instrumentation compile and are covered by deterministic tests. High-risk informational turns are explicitly routed to assistance rather than being silently ignored. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Local documents, chunks, facts, transcripts, FTS5/BM25 and tests authored. |
| TXT/MD/HTML/DOCX/EML/PDF ingestion | AUTOMATED_ONLY | One recursive synthetic import test exercises every format, SHA-256 deduplication, FTS5 retrieval, PDF page locators, malformed-PDF isolation and repeated-evidence de-crowding. Private representative-file validation remains pending. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Tests verify malicious document text remains in untrusted input rather than application instructions, invented source IDs are removed, SAY never becomes USER_ACTUALLY_SAID, and the natural British speech contract remains explicit. |
| Windows credential storage | AUTOMATED_ONLY | P/Invoke implementation compiles; actual Windows Credential Manager behavior still needs local validation. |
| System-loopback + mic fallback | AUTOMATED_ONLY | NAudio implementation compiles; real-device validation pending. |
| Process-specific Teams capture | PARTIAL | Uses Windows `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_PARAMS`, include-target-process-tree mode and `VAD\\Process_Loopback`. Real self-tests captured a target tone (peak 23627) and excluded an unrelated tone from a silent target (peak 1). No Teams call/headset/mute/restart evidence exists yet. |
| Live OpenAI transcription | AUTOMATED_ONLY | Two independent 24 kHz PCM sessions now default to the current dedicated streaming STT model `gpt-realtime-whisper`; serialized sends, safe errors/close, bounded reconnect, item-chain ordering and duplicate-final suppression compile. Synthetic A/B out-of-order protocol tests pass; no live API session was run. |
| GPT structured assistant | PARTIAL | Responses API client, `gpt-5.6-sol`, low verbosity/reasoning, source-constrained structured response compiles; live API/latency verification pending. |
| WPF overlay | PARTIAL | App launch/normal close succeeded and the topmost non-activating SAY/WATCH/ASK overlay plus manual fallback compile. Visual/focus testing beside Teams is still required. |
| Live start/stop UI | PARTIAL | Teams/microphone selectors, fresh meeting creation, dual capture/transcription, compact actual transcript, LISTENING/RECONNECTING/TRANSCRIPT ONLY/MANUAL status, bounded stop and local unfinished-meeting recovery are wired. Live Teams/API exercise remains pending. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting. |

## CI history relevant to bootstrap

- Initial restore exposed a high-severity vulnerable transitive `SQLitePCLRaw.lib.e_sqlite3 2.1.11`; the repository now directly pins `2.1.12`.
- Subsequent CI exposed and fixed strict nullable/xUnit analyzer compile errors.
- The resulting baseline completed restore, Release build, and tests successfully.

## Known deliberate gaps

- Teams process-specific loopback is implemented with the supported Windows application-loopback mechanism, but must still be tested during a real call with unrelated audio, headphones/Bluetooth, mute and Teams restart.
- Realtime reconnect and `item_id` ordering are deterministic-test covered but still need live API verification under pauses, interruption and overlap.
- The current coordinator still needs a live-concurrency hardening pass so persistence is never delayed by answer generation and stale SAY output cannot appear after the user has already started answering.
- Realtime audio sending still needs bounded-queue/backpressure validation so a slow/reconnecting socket cannot accumulate an unbounded number of fire-and-forget send tasks; audio gaps during reconnect must be surfaced honestly.
- Assistant Responses are currently rendered after a complete short structured response. Instrument real latency before deciding whether partial SAY streaming is necessary.
- EML message bodies are indexed; embedded attachment contents are not recursively extracted from inside the EML in v0.1. Files exported alongside the email are imported normally.
- No semantic embedding index yet. v0.1 uses local FTS5/BM25 plus zero-latency HR concept aliases and optional lightweight AI retrieval expansion when local analysis is ambiguous.

See `docs/AUTHORING_VALIDATION.md` for the original authoring-environment checks and `docs/GATES.md` for live evidence requirements.

Do not upgrade any Windows/live status without evidence described in `docs/GATES.md`.
