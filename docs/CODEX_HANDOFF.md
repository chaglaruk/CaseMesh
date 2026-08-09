# Codex handoff

Codex should first read `AGENTS.md`, `README.md`, `docs/GATES.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY_PRIVACY.md`, and `docs/STATUS.md`.

The first Windows batch is fully specified in [`docs/FIRST_CODEX_BATCH.md`](FIRST_CODEX_BATCH.md). It is deliberately broad enough to save iteration time while preserving strict gate evidence.

The first coordinated Windows batch now includes the supported process-loopback implementation, deterministic realtime ordering/recovery, live Start/Stop wiring, broad synthetic Case Brain/grounding coverage, and latency instrumentation. Highest-priority unresolved validation work:

1. real Teams call validation with unrelated audio, headset/Bluetooth, mute and process restart
2. live two-source `gpt-live-transcribe` protocol and reconnect exercise
3. live GPT-5.6 Sol grounding/naturalness and realistic median/p95 latency measurement
4. visual overlay/focus validation beside Teams
5. a 30+ minute endurance/restart rehearsal

Never mark hardware/live Gates 1/2/5/6/7 verified from compilation, mocks, or system-loopback fallback.
