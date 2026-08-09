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
- `tests/*` — deterministic unit tests.

## Current status

GitHub Actions has successfully completed `.NET 10` restore, Windows Release build, and the automated test suite for the current code baseline. This verifies the build/test portion only. **Do not call the app meeting-ready until process-specific Teams capture, live transcription, overlay behavior, latency, recovery, and real-device rehearsal gates pass.**

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
dotnet run --project .\tools\HRCompanion.AudioProbe\HRCompanion.AudioProbe.csproj
```

## AI defaults

Configuration defaults are intentionally centralized and replaceable:

- answer model: `gpt-5.6-sol`
- lightweight intent/retrieval helper: `gpt-5.6-luna`
- live transcription: `gpt-live-transcribe`

The app's memory is **not ChatGPT memory**. Case knowledge must come from the local Case Brain, verified sources, meeting transcripts, and explicit user-position records.

## Repository rule

Never commit real HR documents, emails, medical material, transcripts, API keys, or other personal case data. Use templates and synthetic eval fixtures only.
