# Security and privacy

## Local-first policy

The application handles potentially sensitive employment material. Its default behavior is therefore deliberately narrow:

- source documents remain local
- index/database remain local
- transcripts remain local
- raw meeting audio is not saved by default
- only relevant evidence excerpts and meeting context are sent to the configured model endpoint

## Secrets

The OpenAI API key must be stored using Windows Credential Manager. Never persist it in source, SQLite, JSON settings, logs, crash dumps, or Git.

## Logging

Logs must not contain:

- API keys
- full document bodies
- full model request context
- raw audio

Diagnostic logging may use document IDs, chunk IDs, lengths, timings, model IDs, HTTP status, and redacted error metadata.

## Personal data and Git

`data/`, databases, imports, exports, audio, and local settings are ignored. Tests and examples must use synthetic content.

## Meeting-policy reminder

Technical capability is not the same as permission to use transcription/AI in a workplace meeting. The user is responsible for complying with workplace policy and applicable consent/privacy requirements. This repository does not attempt to hide the application from meeting participants or bypass monitoring/security controls.
