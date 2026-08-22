# ADR 0009: Matter Evidence retrieval and citation-gated Q&A

- Status: Accepted
- Date: 2026-08-21

## Context

The authenticated Web MVP can display canonical Matter state, but cannot answer a scoped factual question across evidence. The Q&A layer must remain a view over PostgreSQL and the Matter Brain, preserve attribution and contradictions, and reject model-invented citations. It must not blend customer evidence with external legal authority.

## Decision

Add the platform-neutral `CaseMesh.Qa` application layer above Core and Matter Brain. It defines provider-neutral retrieval and reasoning interfaces, bounded context/result records, stable retrieval identities, deterministic citation verification, conservative factual-gap rules and a fail-loud evaluation report. Core gains no PostgreSQL, model-provider or embedding dependency.

`PostgresMatterEvidenceRetriever` is the first Matter Evidence adapter. It runs every query in ADR 0002's transaction-local tenant context and uses explicit tenant/Matter predicates in addition to forced RLS. Results always resolve to an exact `SourceSpan`, immutable document version, original-object identity and SHA-256. Ingestion-backed spans are compared with `document_ingestion_state.current_span_set_id`; an older parser span remains retrievable as historical, and a current result that becomes historical while an answer is being formed fails canonical re-verification. A stable result ID is a deterministic digest of tenant, Matter, material kind, canonical record and source span. After reasoning completes, every used citation is resolved again through PostgreSQL; a missing, changed, cross-tenant or fabricated ID produces an insufficient-evidence result rather than an unsupported answer.

Migration `0007_matter_evidence_retrieval.sql` adds PostgreSQL `simple`-configuration GIN full-text indexes to exact source text and canonical labels for assertions, events, people, organisations, aliases, communications and workplace records. New immutable rows and append-only corrections are indexed by PostgreSQL at row-write time, so only affected rows change; there is no global tenant/Matter rebuild or second evidence copy. Structural SQL joins fuse assertions/events/workplace relationships with FTS matches. PostgreSQL canonical state remains the system of record.

The initial provider is deterministic evidence synthesis. It creates concise attributed claims from the bounded retrieval set, labels disputed and historical material, and cannot invoke tools or interpret evidence text as instructions. `IMatterReasoningProvider` permits a later external model adapter without changing Core or the citation gate. Provider output is schema-, enum-, length-, prohibited-output- and citation-validated before display. Evidence claims require citations; source-less analysis must be explicitly typed as analysis and cannot carry a documentary citation. The user-facing summary is derived deterministically from verified claim counts rather than displaying uncited provider prose. Provider failure returns an explicit insufficient-evidence result without logging or reflecting the evidence context.

Conversation transcripts are not persisted in this milestone. Each question independently retrieves and verifies current canonical Matter state. The Web `New thread` action clears only bounded client answer/question state, so an assistant answer can never become a later factual source. Ordinary logs contain neither prompts, evidence context nor provider output.

Factual gaps are deterministic evidence-review prompts: unresolved contradictions, source-less assertions, request/response/implementation separation, corrected history, conflicting chronology dates and unresolved entity-match proposals. They link to existing Matter views and do not assert legal duties, liability or merits.

The API checks membership before retrieval, refuses Q&A while Matter evidence jobs are active, applies a dedicated per-user rate limit, and returns exact source-span projections for clickable citations. React renders strings as text; raw model/evidence HTML is never executed. The UI labels the surface `What your evidence shows` and keeps future external guidance separate.

## pgvector evaluation

pgvector is deliberately not introduced. The required synthetic corpus is small, its decisive relationships are exact relational provenance links, and the real-PostgreSQL lexical evaluation covers sickness counts, employment terms, adjustments, Occupational Health, corrected chronology and employer/third-party conflicts while an absent query returns no result. Adding an embedding model, vector dimension/version lifecycle and pgvector CI image would add operational and stale-index surface without improving the acceptance corpus's citation validity or retrieval coverage. The provider-neutral contracts leave a measured future vector accelerator possible when representative pilot queries demonstrate a lexical-recall gap.

## Consequences

Matter Q&A remains deterministic and usable without paid provider credentials, while a later reasoning adapter can improve synthesis behind the same authorization and citation boundary. Full-text indexes are accelerators only and may be recreated without losing evidence. Historical and contradictory records remain retrievable rather than being silently collapsed.

External legal/process authority RAG, jurisdiction/effective-date authority indexes, legal merits/liability/win/compensation scoring, deadline calculation, billing, mailbox import and CaseMesh Live remain out of scope.
