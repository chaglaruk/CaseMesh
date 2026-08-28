# CaseMesh Live local Windows validation handoff

Date: 2026-08-28

## Purpose

This handoff contains only the CaseMesh Live work that requires the user's real Windows machine, Teams/audio devices, saved credentials or a sustained interactive rehearsal. Repository CI, mocks and compilation must never be reported as evidence that these gates passed.

The canonical commercial Live foundation is developed separately under Issue #29 / PR #30. Do not run this package until that repository foundation is stable enough to consume canonical Matter context.

## Rules

- Use real Windows runtime evidence for every promotion in `docs/GATES.md` / `docs/STATUS.md`.
- Do not commit real workplace, medical, email, transcript or credential data.
- Do not persist raw audio unless an explicit test requires it; the default validation must confirm that raw audio is absent afterward.
- Keep `HR/REMOTE`, `USER_ACTUALLY_SAID` and `AI_SUGGESTED` ownership distinct.
- A Matter source shown beside a transcript/suggestion is context evidence; it does not prove that the speaker said the cited wording.
- Record exact commands, device/runtime versions, pass/fail evidence and any blocker. Do not upgrade a gate from inference.

## Package A — Gate 1 real Teams audio ownership

During a controlled Teams call, run the process-loopback probe against the recognised Teams root PID:

```powershell
dotnet run --project .\tools\CaseMesh.AudioProbe\CaseMesh.AudioProbe.csproj -c Release -- --pid <TEAMS_ROOT_PID> --seconds 10
```

Verify separately:

1. remote Teams speech raises only the `HR/REMOTE` stream;
2. local microphone speech raises only the `USER` stream;
3. unrelated non-Teams system audio does not contaminate process-specific Teams capture;
4. Teams mute/unmute behaves correctly;
5. capture survives stop/start and reconnect;
6. wired headphones are exercised;
7. Bluetooth is exercised when available;
8. no raw audio file is created by the normal path.

## Package B — Gate 2 live transcription

With both real sources active, verify:

- role labels remain correct for remote and user streams;
- partial/final text is usable during normal turns;
- interruptions/overlap and pauses do not create obvious false ownership;
- reconnect preserves ordering and does not erase already persisted final transcript turns;
- failure/reconnect does not promote AI suggestions to actual speech.

## Package C — Gate 3 representative private Case Brain files

Outside Git, exercise representative private copies of:

- TXT/MD;
- DOCX;
- EML;
- PDF including a scanned/OCR case;
- recursive folder import;
- SHA-256 duplicate handling;
- retrieval and source-locator preservation.

Confirm Git status contains no imported case data before and after the run.

## Package D — Gate 4 real answer-model safety eval

Use the existing Windows Credential Manager entry when present:

```powershell
$env:CASEMESH_RUN_LIVE_EVALS = "1"
dotnet test .\tests\CaseMesh.Infrastructure.Tests\CaseMesh.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~LiveHrExchangeCorpus"
Remove-Item Env:CASEMESH_RUN_LIVE_EVALS
```

A temporary `OPENAI_API_KEY` override may be used only for the process lifetime. `CASEMESH_ANSWER_MODEL` may be used for an intentional comparison run.

Record loaded-framing/commitment-trap behavior, citation resolution and `SAY/WATCH/ASK` output. Never save the key into the repository.

## Package E — Windows Credential Manager runtime

Exercise actual save/read/update/delete behavior for the CaseMesh credential entry on this machine and verify secrets do not appear in logs, screenshots committed to Git, test fixtures or config files.

## Package F — Gate 5 naturalness and latency

Run realistic rehearsals and record at least:

- time to first useful text;
- end-to-end response latency;
- median latency;
- p95 latency;
- cancellation when a newer final turn arrives;
- absence of stale answers appearing after a newer generation;
- transcript persistence continuing when assistance is slow/failing.

Do not invent an SLA from model/network assumptions.

## Package G — Gate 6 overlay usability

With Teams foreground, verify:

- always-on-top visibility;
- no unwanted focus stealing;
- clear visual separation of `SAY`, `WATCH`, and `ASK`;
- evidence-backed versus no-evidence state is visible;
- manual fallback remains usable when audio/transcription fails.

## Package H — Gate 7 endurance and recovery

Run a 30+ minute realistic rehearsal and verify:

- incremental transcript persistence;
- older actual speaker-attributed context survives beyond the recent-turn window;
- API failure degrades gracefully;
- application restart can recover an unfinished meeting;
- no stale in-flight answer appears after recovery;
- no raw audio remains unless explicitly enabled for a controlled diagnostic.

## Completion output

Return a gate-by-gate evidence table with `PASS`, `FAIL` or `BLOCKED`, the exact observations supporting each result, any changed files/commits, and the final `git status`. Only update `docs/GATES.md` and `docs/STATUS.md` for gates supported by actual recorded evidence.
