# ADR 0011: Canonical Matter meeting preparation

## Status

Accepted for the commercial workplace-dispute MVP.

## Context

CaseMesh already has a legacy local `MeetingState` prototype and a commercial canonical Matter state built from `MatterEvidenceGraph`, `MatterBrainState` and `WorkplaceMatter`. The commercial roadmap requires a meeting-preparation surface before any Live meeting-assistance milestone. Reusing the legacy meeting object as a second source of truth would create provenance drift, stale copies and ambiguity over which representation controls evidence, corrections and disputes.

Meeting preparation is also a high-stakes presentation surface. It must not silently turn attributed statements into facts, hide unresolved contradictions, re-present rejected or superseded evidence as current, or mix external legal guidance and outcome prediction into the user's own evidence record.

## Decision

The commercial `Prepare` surface is a deterministic read projection over one canonical, tenant-scoped Matter state.

The authenticated API obtains the current web user, verifies workspace membership, acquires the existing per-Matter state lock, observes whether ingestion is active and loads the Matter Brain once. Preparation content is then derived from that loaded state without model calls and without writing a separate meeting-preparation database model.

The projection exposes:

- a bounded set of current source-backed attributed evidence points;
- a bounded chronology derived from non-rejected, non-superseded events;
- structured participant records with identity-ambiguity caution;
- unresolved contradictions with both attributed sides retained;
- factual evidence-review prompts from the existing `FactualGapAnalyzer`;
- immutable document-version/source-span references for evidence that should be reviewed before the meeting;
- an explicit currentness state when evidence ingestion is still active.

Source-backed priority points exclude rejected assertions, superseded assertions and AI inference. AI inference remains structurally separate under the existing domain invariant and cannot become a source-backed preparation point. Unresolved contradictions may include historical assertions only when those assertions are explicitly marked historical in the projection.

Only source spans referenced by the preparation projection are returned to the browser. Every returned citation retains its immutable `DocumentVersionId` and exact locator metadata. The Web `Prepare` view uses the existing citation-detail panel rather than copying evidence into an uncited narrative.

Preparation is evidence organisation only. It does not provide legal merits, liability analysis, compensation or settlement prediction, external-authority retrieval, live transcription or autonomous negotiation. External legal guidance and CaseMesh Live remain separate future surfaces.

## Consequences

- The canonical Matter remains the single commercial source of truth before, during and after preparation.
- Corrections, dispute state and ingestion currentness automatically flow into a newly loaded preparation view; there is no preparation cache to reconcile.
- Preparation output is deterministic and testable without an LLM dependency.
- The per-Matter lock favors currentness integrity over same-Matter read/write concurrency during preparation generation, consistent with the closed-pilot operations policy.
- The legacy `MeetingState` prototype can continue to compile for compatibility but is not promoted into the commercial architecture.
- A future Live milestone must consume canonical Matter context through an explicit adapter/gate rather than becoming a parallel evidence store.

## Out of scope

Live audio/transcription, speaker diarization, legal authority RAG, merits/outcome scoring, autonomous actions, preparation persistence, shared collaborative editing and public-link sharing are out of scope for this decision.
