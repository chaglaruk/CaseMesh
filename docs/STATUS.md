# Status

Last update: 2026-08-09.

| Area | Status | Notes |
|---|---|---|
| Repository architecture | PARTIAL | Narrow single-user Windows architecture, privacy rules, gates, and handoff authored. |
| Build / automated tests | VERIFIED | GitHub Actions on Windows Server 2025 with .NET 10 completed restore, Release build, and tests successfully on the current code baseline. This does not verify live hardware/audio behavior. |
| Static source validation | AUTOMATED_ONLY | JSON/XML parse checks and a C# lexical/delimiter scan were also performed during authoring; CI compilation supersedes these for build validity. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, chronological meeting state, cue engine, local HR aliases, tests authored. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Local documents, chunks, facts, transcripts, FTS5/BM25 and tests authored. |
| TXT/MD/HTML/DOCX/EML/PDF ingestion | AUTOMATED_ONLY | Recursive import, SHA-256 deduplication, per-file failure isolation, source locators, empty/scanned-text detection and repeated-evidence de-crowding authored. Real representative-file validation pending. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Trust hierarchy, separate application instructions, prompt-injection resistance, evidence-ID allow-list/schema and natural spoken style authored. |
| Windows credential storage | AUTOMATED_ONLY | P/Invoke implementation compiles; actual Windows Credential Manager behavior still needs local validation. |
| System-loopback + mic fallback | AUTOMATED_ONLY | NAudio implementation compiles; real-device validation pending. |
| Process-specific Teams capture | NOT_ATTEMPTED | Critical Gate 1 task for Codex/Windows environment. Microsoft application-loopback design was researched, but the repository keeps a loud stub rather than shipping unverified COM interop as if it worked. |
| Live OpenAI transcription | PARTIAL | `gpt-live-transcribe` WebSocket client, 24 kHz PCM, two-source ownership, serialized sends and `item_id` retention compile; end-to-end/reconnect/order validation pending. |
| GPT structured assistant | PARTIAL | Responses API client, `gpt-5.6-sol`, low verbosity/reasoning, source-constrained structured response compiles; live API/latency verification pending. |
| WPF overlay | PARTIAL | Minimal case import, manual fallback, local USER_POSITION context, Credential Manager API key, SAY/WATCH/ASK and topmost overlay compile; focus/runtime testing pending. |
| Live start/stop UI | NOT_ATTEMPTED | Deliberately blocked on real Teams process-loopback implementation rather than wiring a misleading system-audio path. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting. |

## CI history relevant to bootstrap

- Initial restore exposed a high-severity vulnerable transitive `SQLitePCLRaw.lib.e_sqlite3 2.1.11`; the repository now directly pins `2.1.12`.
- Subsequent CI exposed and fixed strict nullable/xUnit analyzer compile errors.
- The resulting baseline completed restore, Release build, and tests successfully.

## Known deliberate gaps

- True Teams process-specific loopback capture must be implemented with the supported Windows application-loopback mechanism and tested on the user's Windows machine.
- Realtime reconnection and explicit cross-turn `item_id` ordering/reconciliation need Windows/live API verification. Item IDs are retained so this can be implemented without changing the public transcript contract again.
- Assistant Responses are currently rendered after a complete short structured response. Instrument real latency before deciding whether partial SAY streaming is necessary.
- EML message bodies are indexed; embedded attachment contents are not recursively extracted from inside the EML in v0.1. Files exported alongside the email are imported normally.
- No semantic embedding index yet. v0.1 uses local FTS5/BM25 plus zero-latency HR concept aliases and optional lightweight AI retrieval expansion when local analysis is ambiguous.

See `docs/AUTHORING_VALIDATION.md` for the original authoring-environment checks and `docs/GATES.md` for live evidence requirements.

Do not upgrade any Windows/live status without evidence described in `docs/GATES.md`.
