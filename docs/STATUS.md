# Status

Last update: 2026-08-28.

The commercial Matter evidence platform through Milestone 11 is merged. Issue #29 / draft PR #30 is implementing the canonical CaseMesh Live/Review foundation. This repository work does **not** upgrade any real Windows/Teams/audio readiness gate.

| Area | Status | Notes |
|---|---|---|
| Canonical Matter -> Live/Review boundary | IN_PROGRESS | `CaseMesh.Live` adds a platform-neutral read-only canonical context adapter, exact documentary provenance, separate source-less attributed statements and AI analysis, explicit meeting origins, and tenant-scoped Review context. PR #30 remains draft until its full gates pass. |
| Repository architecture | PARTIAL | Commercial PostgreSQL Matter state is the evidence system of record; the legacy local Windows `MeetingState`/SQLite stack remains a compatibility/runtime prototype for later Live work. Real hardware gates remain open. |
| Build / automated tests | VERIFIED_FOR_AUTOMATED_SCOPE | The Milestone 12 foundation is included in the solution and automated CI. A passing build/test proves only deterministic/software behavior, not live Teams/audio behavior. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, chronological meeting state, deterministic older-turn compaction, six-second live budget, 1.5-second optional analysis budget, latest-turn-wins cancellation, durable final turns and fake overlap/timeout tests are implemented in the legacy Live runtime. |
| Canonical Live context provenance | AUTOMATED_ONLY | Documentary items preserve exact SourceSpan, DocumentVersion, immutable original-object id and SHA-256; non-current documentary records are historical; source-less current attributed statements and AI analysis are separate and uncited. Independent review is still pending while PR #30 is draft. |
| Uploaded meeting/transcript review foundation | AUTOMATED_ONLY | `HrSaid`, `UserActuallySaid` and `AiSuggested` remain distinct. Matter evidence references are context citations only and may resolve only to current canonical documentary evidence; they do not become provenance for spoken wording. Real uploaded-file product flow remains later work. |
| SQLite schema / FTS5 | AUTOMATED_ONLY | Legacy local documents, chunks, facts, transcripts, FTS5/BM25 and tests remain buildable; they are not the commercial evidence authority. |
| TXT/MD/HTML/DOCX/EML/PDF legacy local ingestion | AUTOMATED_ONLY | Recursive import, SHA-256 deduplication, per-file failure isolation, source locators, empty/scanned-text detection and repeated-evidence de-crowding authored. Real representative private-file validation remains pending for the local Live track. |
| Working-context seed import | AUTOMATED_ONLY | Separate `.hrcontext` path maps USER_POSITION vs UNVERIFIED context without creating false documentary evidence; tests authored. |
| Prompt grounding | AUTOMATED_ONLY | Trust hierarchy, prompt-injection resistance, source-ID allow-listing, loaded-framing safeguards and natural spoken style authored. |
| Safety eval corpus | PARTIAL | `evals/hr-exchanges.json` is executed by deterministic tests; the opt-in real answer-model harness still requires local credential/live-model evidence before meeting-ready status. |
| Windows credential storage | AUTOMATED_ONLY | P/Invoke implementation compiles; actual Windows Credential Manager behavior still needs local validation. |
| System-loopback + microphone diagnostics | PARTIAL | NAudio WASAPI microphone lifecycle evidence exists from 2026-08-13. System loopback evidence is not accepted as process-specific Teams ownership. |
| Process-specific Teams capture | AUTOMATED_ONLY | Application loopback targets the selected recognised Teams process tree with no system-loopback fallback, but a real Teams process-specific ownership run is still required. |
| Live OpenAI transcription | PARTIAL | Realtime transcription client and two-source ownership compile; end-to-end/reconnect/order validation remains local/live work. |
| GPT structured assistant | PARTIAL | Structured assistant and deterministic safeguards compile; real model behavior and latency validation remain pending. |
| WPF overlay | PARTIAL | SAY/WATCH/ASK and evidence state compile; focus/runtime testing with Teams foreground remains pending. |
| Live start/stop UI | AUTOMATED_ONLY | WPF constructs separate HR/User capture/transcription paths and restartable failure handling; runtime Teams/UI validation remains pending. |
| Coordinator concurrency test | AUTOMATED_ONLY | Deterministic tests cover cancellation/latest-turn-wins/persistence behavior with fake dependencies. |
| 30-minute rehearsal | NOT_ATTEMPTED | Required before real meeting-ready status. |

## Current Milestone 12 constraints

- Canonical PostgreSQL Matter state remains the only commercial evidence authority.
- The new Review/Live context must not copy evidence into a parallel persistence store.
- A user correction or other source-less statement must not inherit documentary provenance it does not possess.
- AI analysis and `AI_SUGGESTED` content must not become documentary fact or `USER_ACTUALLY_SAID`.
- Context citations shown beside a conversation item do not prove that the participant spoke the cited wording.
- Cross-tenant Review/Live context access must fail closed.

## Local-only gates still open

The accumulated Windows tasks are recorded in `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md`:

- real Teams process-specific remote/user audio ownership, mute/unmute, headphones/Bluetooth, non-Teams exclusion and reconnect;
- real two-source transcription/reconnect/item ordering;
- representative private local Case Brain files outside Git;
- Windows Credential Manager runtime behavior and real answer-model safety eval;
- overlay focus/usability;
- median/p95 latency and stale-generation checks;
- 30+ minute endurance/recovery;
- confirmation that normal operation leaves no raw audio behind.

## 2026-08-13 legacy local audio evidence

- Teams discovery: executed; zero Teams process trees were running, so isolated Teams Start/frames were not testable.
- Invalid PID: clean failure for PID `2147483647`; repeated Stop/Dispose tests pass.
- Default microphone: `Microphone Array (Realtek(R) Audio)` delivered continuous frames across Stop -> Start; active disposal completed without a crash.
- Explicit system-loopback fallback: lifecycle exercised, but the stream was silent and is not accepted as Teams-isolation evidence.
- Raw audio persisted: none in that diagnostic run.

See `docs/GATES.md` for meeting-readiness evidence requirements and `docs/LIVE_LOCAL_VALIDATION_HANDOFF.md` for the deferred local execution package.

Do not upgrade any real Windows/live status without corresponding recorded evidence.
