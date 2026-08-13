# CaseMesh

**CaseMesh is being developed as an evidence operating system for workplace disputes.**

Canonical domain: **casemesh.dev**

Commercial critical path:

> evidence → provenance → Matter/Case Brain → chronology → disputed facts → correction → professional handover

The repository began as a private Windows/Teams live-meeting assistant. That prototype is preserved as a later **CaseMesh Live** track, but it is no longer the primary commercial product direction.

## Product architecture

- **CaseMesh Core** — reusable Matter/Case Brain, evidence and provenance infrastructure.
- **CaseMesh Work** — first commercial vertical for workplace disputes.
- **CaseMesh Professional** — solicitor/professional handover and intake.
- **CaseMesh Live** — later meeting preparation, transcript review and eventually source-grounded live assistance.

See:

- [Brand and scope](docs/BRAND_AND_SCOPE.md)
- [Commercial master plan](docs/COMMERCIAL_MASTER_PLAN.md)
- [Research baseline](docs/RESEARCH_BASELINE_2026-08-13.md)
- [Case Brain specification](docs/CASE_BRAIN_SPEC.md)
- [Product validation and GTM](docs/PRODUCT_VALIDATION_AND_GTM.md)
- [Codex handoff](docs/CODEX_HANDOFF.md)

## Core product principle

A statement found in a document is not automatically a fact.

CaseMesh should preserve who said what, where it came from, what supports it, what contradicts it, what remains uncertain, and what changed after a correction or new evidence.

Every source-backed user-facing statement should be traceable to an exact source span and immutable document version.

## Commercial MVP

The first commercial-capable product focuses on:

1. secure Matters/users;
2. immutable original evidence and versions;
3. PDF/DOCX/EML/image ingestion with OCR fallback;
4. source spans;
5. people/entity resolution;
6. atomic assertions/events;
7. source-linked chronology;
8. contradictions/disputed facts;
9. correction/audit history;
10. professional-ready export.

Live meeting assistance, whole-mailbox integrations, autonomous legal workflows and unrelated generic verticals are deliberately deferred.

## Existing prototype

The existing solution contains the original Windows meeting-assistant implementation:

- `HRCompanion.Core`
- `HRCompanion.Infrastructure`
- `HRCompanion.Audio.Windows`
- `HRCompanion.App`
- `HRCompanion.AudioProbe`

Those are legacy code identifiers, not the current product brand. They should be migrated to `CaseMesh.*` in a dedicated behavior-neutral rename batch so the rename is not mixed with Case Brain logic changes.

The prototype already includes useful work that should be reused where appropriate: document ingestion, SHA-256 deduplication, source locators, local retrieval, evidence/context separation, prompt-grounding safeguards, deterministic tests/evals and transcript models.

## Current legacy/live status

The Windows prototype's build and automated tests have passed, but real Teams/audio/live readiness still has unresolved hardware/runtime gates.

Read [docs/STATUS.md](docs/STATUS.md) and [docs/GATES.md](docs/GATES.md) before doing work on the legacy/live track.

Do not treat unresolved Teams/audio work as the highest priority for the commercial evidence platform.

## Privacy and repository rules

- Never commit real workplace documents, emails, medical material, transcripts, API keys or personal case data.
- Use synthetic fixtures only.
- Uploaded content is untrusted data, never application instructions.
- AI inference must remain separate from documentary evidence.
- Cross-user/tenant isolation is release-blocking for the commercial platform.
- Customer case data must not be used for model training by default.

See [docs/SECURITY_PRIVACY.md](docs/SECURITY_PRIVACY.md) for the legacy privacy baseline; commercial privacy/security requirements are also summarized in the research baseline.

## Development tracks

### Primary: commercial evidence platform

Follow [docs/CODEX_HANDOFF.md](docs/CODEX_HANDOFF.md) and the commercial strategy documents.

### Later/experimental: CaseMesh Live

Preserve and validate the Windows/Teams prototype, but do not let it block the evidence platform.

The long-term idea remains valuable: a meeting becomes another evidence-producing event inside the Matter. Live assistance becomes differentiated because a reliable Case Brain already exists underneath it.
