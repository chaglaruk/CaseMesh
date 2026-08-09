# HR Companion

A private, single-user Windows assistant for live Microsoft Teams HR meetings.

The product goal is deliberately narrow:

> Listen to the Teams side and the user's microphone, maintain live meeting context, retrieve the most relevant evidence from a local HR case knowledge base, and show a fast, short, natural spoken-English response suggestion with factual safeguards.

## v0.1 non-goals

No login, accounts, subscriptions, payment, cloud sync, mobile app, browser extension, Teams plugin, multi-user support, admin portal, analytics, calendar, CRM, or SaaS backend.

## Meeting loop

1. Capture Teams/remote audio and microphone as separate logical sources.
2. Transcribe both streams with stable speaker ownership (`HR` vs `USER`).
3. Persist the actual transcript locally.
4. Detect whether the latest remote turn needs an answer, caution, or follow-up.
5. Retrieve only the relevant local evidence and verified case facts.
6. Ask the answer model for a concise structured result.
7. Render `SAY`, `WATCH`, and `ASK` in an always-on-top overlay.
8. Never treat an AI suggestion as something the user actually said.

## Privacy model

- Case documents, local index, fact ledger, transcripts, and meeting state live under the user's local application-data folder.
- Raw meeting audio is **not recorded by default**.
- Only the minimum relevant context needed for a response should be sent to the configured OpenAI API.
- API keys must not be committed or stored in plaintext.
- `data/`, local databases, audio, logs, and secrets are ignored by Git.

See [docs/SECURITY_PRIVACY.md](docs/SECURITY_PRIVACY.md).

## Projects

- `HRCompanion.Core` — domain types, contracts, orchestration, deterministic cue logic.
- `HRCompanion.Infrastructure` — SQLite, FTS5 retrieval, document ingestion, OpenAI request contracts, Windows credential storage.
- `HRCompanion.Audio.Windows` — Windows audio capture abstractions and safe fallback capture.
- `HRCompanion.App` — minimal WPF desktop shell and overlay.
- `HRCompanion.AudioProbe` — local Windows validation utility for audio gates.
- `HRCompanion.SmokeProbe` — credential-backed synthetic dual-Realtime/Sol smoke utility; no key is accepted on the command line.
- `HRCompanion.QualityProbe` — ten-case synthetic Sol quality matrix for manual spoken-answer review; it never reads the real Case Brain.
- `tests/*` — deterministic unit tests.

## Current status

GitHub Actions has successfully completed `.NET 10` restore, Windows Release build, and the automated test suite for the current code baseline. A credential-backed synthetic smoke has also produced separate HR and USER final Realtime transcripts with provider item IDs, followed by a grounded Sol response with valid source IDs and zero reported protocol errors. This proves the synthetic API path only. **Do not call the app meeting-ready until process-specific Teams capture, real pipeline latency, overlay behavior, recovery, and real-device rehearsal gates pass.**

Read [docs/STATUS.md](docs/STATUS.md) and [docs/GATES.md](docs/GATES.md) before continuing.

## Loading case context

Two import paths are intentionally separate:

- **Source material** — PDF, DOCX, EML, TXT, MD, HTML and folders. These become searchable evidence with source locators.
- **Working context** — `.hrcontext`, TXT or MD containing explicitly labelled `USER_POSITION` / `WORKING_CONTEXT` records. These seed the case ledger but are **not** treated as verified documentary evidence.

A private starter context may be imported first, followed by the real source-document folder. When they conflict, primary source material and facts explicitly marked `VERIFIED` must outrank working summaries. Never place either the private context file or real case material inside the repository.

## Local development

Requirements:

- Windows 11
- .NET 10 SDK
- Visual Studio 2026 or VS Code/Rider with .NET tooling
- An OpenAI API key

Typical commands:

```powershell
dotnet restore .\HRCompanion.slnx
dotnet build .\HRCompanion.slnx -c Release
dotnet test .\HRCompanion.slnx -c Release --no-build
```

Run the audio probe before integrating live meeting features:

```powershell
dotnet run --project .\tools\HRCompanion.AudioProbe\HRCompanion.AudioProbe.csproj -- --list
dotnet run --project .\tools\HRCompanion.AudioProbe\HRCompanion.AudioProbe.csproj -- --self-test --seconds 5
dotnet run --project .\tools\HRCompanion.AudioProbe\HRCompanion.AudioProbe.csproj -- --isolation-self-test --seconds 5
dotnet run --project .\tools\HRCompanion.AudioProbe\HRCompanion.AudioProbe.csproj -- --teams <PID> --seconds 30
```

`--self-test` proves target-process capture on the local Windows audio stack. `--isolation-self-test` plays audio outside a silent target and checks that it is excluded. Neither replaces a real Teams/headset/mute/restart Gate 1 rehearsal. Full system loopback is available only through the explicitly degraded `--system-fallback` option.

After saving the API key through the app, run the synthetic live API smoke path:

```powershell
.\scripts\smoke.ps1
```

The script reads `HRCompanion/OpenAI` from Windows Credential Manager, generates synthetic 24 kHz mono speech only under a temporary directory, creates dedicated short-lived transcription sessions for separate HR/USER Realtime streams, tests one grounded Sol response, reports content-free metrics, and removes the temporary audio. It exits before audio generation or API access when the credential is absent.

For manual synthetic answer-quality review:

```powershell
.\scripts\quality-matrix.ps1
```

The quality matrix sends ten synthetic HR scenarios to the configured Sol answer model and prints the actual `SAY`, `WATCH`, `ASK`, source-ID validity, and isolated request timing. It does not import private material and does not automatically certify naturalness. Its timing must not be reported as Gate 5 end-to-end latency.

## AI defaults

Configuration defaults are intentionally centralized and replaceable:

- answer model: `gpt-5.6-sol`
- lightweight intent/retrieval helper: `gpt-5.6-luna`
- live transcription: `gpt-live-transcribe`, using dedicated short-lived Realtime transcription sessions

The app's memory is **not ChatGPT memory**. Case knowledge must come from the local Case Brain, verified sources, meeting transcripts, and explicit user-position records.

## Repository rule

Never commit real HR documents, emails, medical material, transcripts, API keys, or other personal case data. Use templates and synthetic eval fixtures only.
