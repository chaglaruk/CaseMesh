# Codex handoff

Codex should first read `AGENTS.md`, `README.md`, `docs/GATES.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY_PRIVACY.md`, and `docs/STATUS.md`.

The first Windows batch is fully specified in [`docs/FIRST_CODEX_BATCH.md`](FIRST_CODEX_BATCH.md). It is deliberately broad enough to save iteration time while preserving strict gate evidence.

Highest-priority unresolved work:

1. restore/build/test on Windows/.NET 10 and fix all compile/runtime mismatches
2. real process-specific Teams application-loopback capture
3. robust two-source `gpt-live-transcribe` reconnect + `item_id` ordering
4. wire Start/Stop live WPF path into the existing Case Brain/orchestrator/overlay
5. validate ingestion/grounding and prompt-injection safeguards
6. measure latency/naturalness rather than inventing performance claims
7. produce strict Gate 0–7 evidence

Never mark hardware/live Gates 1/2/5/6/7 verified from compilation, mocks, or system-loopback fallback.
