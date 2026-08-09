# AGENTS.md — HR Companion engineering contract

## Mission

Build a meeting-ready, personal Windows-only HR copilot. Optimize for reliability, factual grounding, low latency, and natural spoken output — not feature breadth.

## Immutable scope

- One Windows user.
- Microsoft Teams is the primary meeting platform.
- No login/account/payment/subscription/cloud-sync/backend/mobile/browser-extension/Teams-plugin/admin/analytics/calendar/CRM work.
- No real personal HR case data may be committed.
- Audio recording defaults OFF.
- `AI_SUGGESTED`, `USER_ACTUALLY_SAID`, and `HR_SAID` must remain separate concepts in storage and logic.
- Never claim a Windows/audio gate is verified from mocks or compilation alone.

## Product priority

The critical path is:

`Teams audio -> transcript -> case retrieval -> grounded answer -> overlay`

A feature that does not improve that path is lower priority than fixing reliability or latency.

## Answer UX contract

Normal output should be natural British spoken English, usually 1–3 sentences, easy to glance at and then say without reading word-for-word.

Avoid:

- legal-letter tone
- corporate filler
- long preambles
- repeating the question
- invented facts/dates/medical conclusions/promises
- asserting that the user said or agreed to something unless it is in `USER_ACTUALLY_SAID`

When evidence is insufficient, prefer clarification over invention.

## Engineering gates

Follow `docs/GATES.md`. Status labels are strict:

- `VERIFIED` — demonstrated on the required real environment or by the specified deterministic evidence.
- `AUTOMATED_ONLY` — build/unit/integration coverage only.
- `PARTIAL` — some required evidence exists, but not all.
- `BLOCKED` — cannot currently execute the required validation.
- `FAILED` — executed and failed.
- `NOT_ATTEMPTED` — no meaningful evidence yet.

Never relabel `AUTOMATED_ONLY` as `VERIFIED` for real Teams/audio behavior.

## Git workflow

- Keep changes reviewable and cohesive.
- Do not rewrite history, force push, reset away work, or silently delete user changes.
- Before declaring a task done: inspect diff, build, run relevant tests, and report exact validation.
- Batch related work when it reduces handoffs, but keep Gate 1 (audio) evidence explicit.

## Architecture constraints

- Domain/interfaces stay in `HRCompanion.Core`.
- Windows-only capture implementation stays in `HRCompanion.Audio.Windows`.
- Persistence/document/OpenAI integrations stay in `HRCompanion.Infrastructure`.
- WPF UI stays thin; business logic belongs outside views.
- Local retrieval must work without sending the entire case archive to a model.
- SQLite FTS5 is a real first-stage retriever. Do not call keyword-only search "semantic search".
- Any future embedding/semantic layer must be optional and locally cacheable.

## Required evidence before a real HR meeting

At minimum:

1. Windows Release build passes.
2. Unit tests pass.
3. Teams/remote and microphone audio are separately captured on the user's actual setup.
4. Headphones/Bluetooth path is tested.
5. Live transcription labels remote vs user correctly.
6. PDF/DOCX/EML/TXT imports and retrieval work on representative files.
7. The answer model cites only supplied/retrieved evidence and fails safely when context is missing.
8. A 30+ minute rehearsal does not lose the transcript or crash.
9. Median/p95 answer latency is recorded from a realistic rehearsal.
10. Overlay remains usable while Teams has focus.
