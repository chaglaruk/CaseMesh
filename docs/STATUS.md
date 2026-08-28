# Status

Last update: 2026-08-28.

The commercial Matter evidence platform through Milestone 12 is merged. Issue #31 / draft PR #32 is hardening the canonical CaseMesh Live/Review projection after late post-merge review findings. This repository work does **not** upgrade any real Windows/Teams/audio readiness gate.

| Area | Status | Notes |
|---|---|---|
| Canonical Matter -> Live/Review boundary | MERGED_HARDENING_IN_PROGRESS | Milestone 12 merged as `0bb9124a6ba91a7028553c9e4928bb36570d6708`. Issue #31 / PR #32 addresses late correctness/performance findings before the next product milestone. |
| Repository architecture | PARTIAL | Commercial PostgreSQL Matter state is the evidence system of record; the legacy local Windows `MeetingState`/SQLite stack remains a compatibility/runtime prototype for later Live work. Real hardware gates remain open. |
| Build / automated tests | VERIFIED_FOR_AUTOMATED_SCOPE | The canonical Live foundation and hardening are covered by solution and CI tests. Passing automated tests proves deterministic/software behavior only, not real Teams/audio behavior. |
| Core orchestration | AUTOMATED_ONLY | Speaker ownership, chronological meeting state, deterministic older-turn compaction, live budgets, latest-turn-wins cancellation, durable final turns and fake overlap/timeout tests remain implemented in the legacy Live runtime. |
| Canonical Live context provenance | HARDENING_IN_PROGRESS | Documentary items reference exact canonical SourceSpan provenance; default Review context no longer needs aggregate exact source text inline. Source metadata includes immutable document/original/hash linkage, exact-text digest/length, parser version and extraction confidence; exact text is retrieved separately through tenant/Matter-scoped Review source detail. Source-less current/historical statements and AI analysis remain separate and uncited. |
| Uploaded meeting/transcript review foundation | AUTOMATED_ONLY | `HrSaid`, `UserActuallySaid` and `AiSuggested` remain distinct. Matter evidence references are context citations only and may resolve only to current canonical documentary evidence. Real uploaded-file product flow is the next product milestone after PR #32. |
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

## Current canonical Live hardening constraints

- Canonical PostgreSQL Matter state remains the only commercial evidence authority.
- Review/Live context must not copy evidence into a parallel persistence store.
- Default context must not inline unbounded aggregate exact source text; exact source text is retrieved on demand through an authenticated tenant/Matter/source path.
- SourceSpan parser/extraction metadata and assertion extraction confidence must survive the boundary where applicable.
- Source-less corrected/rejected statements remain visible as historical attributed statements and never acquire documentary provenance.
- AI analysis and `AI_SUGGESTED` content must not become documentary fact or `USER_ACTUALLY_SAID`.
- Structured-extraction contradiction provenance remains auditable; trusted human-correction rules remain labelled deterministic.
- Context citations shown beside a conversation item do not prove that the participant spoke the cited wording.
- Cross-tenant Review/Live context and source-detail access must fail closed.

## Next product milestone after PR #32

Uploaded meeting/transcript Review on the hardened canonical boundary:

- uploaded transcript/recording-derived transcript ingestion and review;
- explicit `HR_SAID` / `USER_ACTUALLY_SAID` ownership;
- AI analysis and `AI_SUGGESTED` kept separate;
- contradictions, commitments, unanswered questions, factual gaps and follow-up items grounded in canonical Matter context;
- exact source inspection without promoting transcript/model content to established fact.

Real-time CaseMesh Live remains later and requires the separate local gates below.

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
