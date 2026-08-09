# Status

Last update: 2026-08-09.

| Area | Status | Notes |
|---|---|---|
| Repository architecture | PARTIAL | Narrow single-user Windows architecture, privacy rules, gates, and handoff authored. |
| Build / automated tests | VERIFIED | GitHub Actions on Windows Server 2025 with .NET 10 completed restore, Release build, and tests successfully on the baseline. Review-branch CI is also enabled for independent validation of Codex/audit fixes. This does not verify live hardware/audio behavior. |
| Static source validation | AUTOMATED_ONLY | JSON/XML parse checks and a C# lexical/delimiter scan were also performed during authoring; CI compilation supersedes these for build validity. |
| Core orchestration | AUTOMATED_ONLY | SQLite insertion is now the publication boundary: inserted/duplicate results are explicit, state/UI/AI follow only new durable inserts, and DB failure remains non-fatal without split-brain state. Live manual turns share the generation/cancellation lifecycle. Tests cover stale manual guidance, USER/newer-HR supersession, Luna/retrieval/Sol cancellation, slow persistence beyond bounded stop, and current health plus sticky gap metadata. High-risk informational turns remain routed to assistance. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Local documents, chunks, facts, transcripts, FTS5/BM25 and tests authored. |
| TXT/MD/HTML/DOCX/EML/PDF ingestion | AUTOMATED_ONLY | One recursive synthetic import test exercises every format, SHA-256 deduplication, FTS5 retrieval, PDF page locators, malformed-PDF isolation and repeated-evidence de-crowding. Private representative-file validation remains pending. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Tests verify malicious document text remains in untrusted input rather than application instructions, invented source IDs are removed, SAY never becomes USER_ACTUALLY_SAID, and the natural British speech contract remains explicit. A ten-case credential-backed synthetic Sol quality matrix is available for manual review; it never reads the real Case Brain. |
| Windows credential storage | VERIFIED | The application credential was retrieved successfully by the local smoke preflight without printing or exporting it. |
| System-loopback + mic fallback | PARTIAL | Built-in Realtek microphone capture produced about 236.7 KiB of converted audio over a five-second probe. No headset/Bluetooth endpoint was present, and system loopback remains an explicitly degraded fallback. |
| Process-specific Teams capture | PARTIAL | Uses Windows `ActivateAudioInterfaceAsync` with include-target-process-tree mode. The fixed 44.1 kHz/16-bit/stereo format is the format in Microsoft's current sample; `AUTOCONVERTPCM` is enabled and packet bytes derive from block alignment. Post-change self-tests captured a target tone (peak 23625) and excluded an unrelated tone (peak 1). No Teams call/headset/mute/restart evidence exists. |
| Live OpenAI transcription | PARTIAL | Credential-backed synthetic validation now passes with two independent dedicated short-lived Realtime transcription sessions using `gpt-live-transcribe`: both HR and USER produced final transcripts with provider item IDs, and the smoke reported zero protocol errors. The connection no longer bootstraps a general Realtime voice model and then attempts to change the session type. This is strong synthetic API evidence but not a real Teams-call transcription rehearsal. |
| GPT structured assistant | PARTIAL | A credential-backed synthetic smoke completed a `gpt-5.6-sol` structured response with a short SAY, valid constrained source IDs and no reported protocol error. Ten-case manual answer-quality review and real end-to-end meeting latency evidence remain pending. |
| WPF overlay | PARTIAL | App launch/normal close succeeded and the topmost non-activating SAY/WATCH/ASK overlay plus manual fallback compile. Visual/focus testing beside Teams is still required. |
| Live start/stop UI | PARTIAL | Teams/microphone selectors, dual capture/transcription, compact actual transcript, stale-guidance clearing, bounded stop and local unfinished-meeting recovery are wired. One compact local preflight reports Credential Manager key, Teams/microphone selection, Case Brain access and optional source count without a paid request. Current HR/USER health remains primary while historical gap metadata stays visible. A real Teams/API exercise remains pending. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting. |

## CI history relevant to bootstrap

- Initial restore exposed a high-severity vulnerable transitive `SQLitePCLRaw.lib.e_sqlite3 2.1.11`; the repository now directly pins `2.1.12`.
- Subsequent CI exposed and fixed strict nullable/xUnit analyzer compile errors.
- The resulting baseline completed restore, Release build, and tests successfully.
- Live synthetic validation exposed an incorrect general-Realtime bootstrap for dedicated transcription. The client now creates a short-lived transcription session via the Realtime client-secret API and connects that session directly before accepting audio.

## Known deliberate gaps

- Teams process-specific loopback is implemented with the supported Windows application-loopback mechanism, but must still be tested during a real call with unrelated audio, headphones/Bluetooth, mute and Teams restart.
- Realtime reconnect, bounded backpressure, gap reporting, cancellation and `item_id` ordering are deterministic-test covered; live synthetic transcription now passes, but pauses, interruption and overlap still require real Teams-call validation.
- The concurrency generation rules prevent deterministic stale SAY races, but still need validation during a real rapid HR-to-USER exchange.
- Assistant Responses are currently rendered after a complete short structured response. Instrument the real application pipeline before deciding whether partial SAY streaming is necessary. Smoke and quality-probe request timings are diagnostics only and are not Gate 5 latency evidence.
- EML message bodies are indexed; embedded attachment contents are not recursively extracted from inside the EML in v0.1. Files exported alongside the email are imported normally.
- No semantic embedding index yet. v0.1 uses local FTS5/BM25 plus zero-latency HR concept aliases and optional lightweight AI retrieval expansion when local analysis is ambiguous.

See `docs/AUTHORING_VALIDATION.md` for the original authoring-environment checks and `docs/GATES.md` for live evidence requirements.

Do not upgrade any Windows/live status without evidence described in `docs/GATES.md`.
