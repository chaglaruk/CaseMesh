# HR Companion — Commercial Master Plan

Date: 2026-08-13

## Direction

The commercial product is an **evidence operating system for workplace disputes**.

New critical path:

`evidence -> provenance -> structured case state -> chronology -> dispute map -> correction -> professional handover`

The current Windows live-meeting prototype remains useful research and may return as a later feature, but it is no longer the first commercial wedge.

## First product surfaces

### Employee workspace

- create a case;
- upload evidence;
- inspect a source-linked timeline;
- see who made each assertion;
- see contradictions and missing evidence;
- correct extraction;
- ask case-grounded questions;
- export a professional-ready pack.

### Professional handover

- original evidence;
- source-linked chronology;
- people map;
- evidence index;
- disputed assertions;
- unanswered factual questions;
- concise case brief;
- downloadable structured bundle.

First professional delivery should be Word/PDF/CSV/ZIP. A portal can wait until firms repeatedly request one.

## Repository transition

Reuse:

- existing document parsers;
- SHA-256 deduplication;
- source locators;
- working-context vs primary-evidence separation;
- prompt-injection safeguards;
- source allow-listing;
- deterministic tests/evals;
- model abstraction;
- transcript models for later uploaded meeting evidence.

Generalize or replace:

- single-user SQLite as the main system of record;
- WPF as the primary UI;
- the three-state `CaseFact` model;
- meeting state as the central domain object;
- live-audio gates as top repository priority;
- FTS5-only retrieval as the long-term platform retriever.

Do not delete the existing meeting projects during the first refactor. Keep them buildable and isolate them from the new commercial critical path.

## Target architecture

Preserve the C# investment:

```text
React/Next.js web app
        |
        v
ASP.NET Core .NET 10 API
        |
        +-- PostgreSQL + pgvector
        +-- private object storage
        +-- background jobs/workers
        +-- OCR/document services
        +-- model router
        +-- export service
        +-- audit/security services
```

Managed providers are preferred for the MVP, subject to the privacy/security and regulatory gates summarized in `RESEARCH_BASELINE_2026-08-13.md`.

Avoid Kubernetes, microservices, Kafka, Neo4j, custom vector infrastructure and self-hosted foundation models initially.

## Case Brain

The new domain should contain at least:

`Case`, `Person`, `Organisation`, `Document`, `DocumentVersion`, `SourceSpan`, `Assertion`, `Event`, `AssertionEventLink`, `Communication`, `EmploymentTerm`, `HealthAbsenceEvent`, `Request`, `Response`, `Issue`, `Contradiction`, `Deadline`, `LegalAuthority`, `Task`, `Objective`, `AnalysisNode`, `AuditEvent`.

The central evidence entity is `Assertion`, not a free-text `Fact`.

## Product invariants

1. A statement found in a document is not automatically a fact.
2. Assertions retain attribution, source, relevant time and dispute state.
3. Employee, employer, third-party and AI statements remain distinct.
4. Extraction confidence is not truth confidence.
5. Contradictory assertions coexist until resolved.
6. AI analysis is stored separately from documentary evidence.
7. Corrections preserve audit history.
8. Every evidence-based displayed statement resolves through `statement -> assertion/event -> source span -> document version -> original hash`.
9. Vector search is never the system of record.
10. Legal/process material and user case evidence are separate retrieval domains.

## AI processing pipeline

```text
upload
 -> quarantine/validation
 -> native parsing
 -> OCR only when required
 -> deterministic metadata
 -> structured extraction
 -> entity/assertion/event normalization
 -> source-span verification
 -> Case Brain merge
 -> retrieval
 -> stronger reasoning when needed
 -> citation verification
 -> output
```

Use lower-cost models for extraction/classification and stronger models for cross-document ambiguity, contradiction analysis and professional synthesis.

Keep authorization, tenancy, hashes, citation existence, date arithmetic, entitlements and destructive actions deterministic.

## MVP navigation

1. Timeline
2. Evidence
3. People
4. Disputed
5. Questions
6. Prepare
7. Export

Every extracted statement supports `Correct`, `Wrong`, and `Needs context`.

## Validation targets

- >=60% of qualified activated users upload five or more evidence items;
- >=50% return to add evidence after the first chronology;
- >=70% rate the source-linked chronology materially above a generic chat summary;
- >99% valid displayed source links;
- zero cross-tenant leakage;
- ordinary human support below 30 minutes per paid case;
- >=60% of lawyer testers report meaningful time savings;
- >=30% of lawyer testers demonstrate willingness to pay.

## Roadmap

### Phase 0 — validation and operating model

Finalize the Case Brain schema, build a clickable prototype, create synthetic eval data, interview employees and solicitors, prepare data-flow/compliance materials, and run a controlled concierge beta.

### Phase 1 — private evidence MVP

Build authentication, cases, secure uploads, original hashes/versions, parsing/OCR, source spans, entities, assertions/events, chronology, contradictions, corrections/audit trail and professional export.

### Phase 2 — commercial pilot

Add quotas, the chosen commercial model, case-grounded Q&A, gap analysis, meeting preparation, mature export/delete/support processes and professional workflows.

### Phase 3 — public England & Wales launch

Add refined onboarding, selected email forwarding, versioned guidance corpus where appropriate, reminders, accessibility, security testing and organic acquisition surfaces.

### Phase 4 — professional ecosystem

Add portal/annotations, organization roles, SSO, APIs and union/insurer pilots only after repeated demand.

### Phase 5 — later capabilities

Uploaded meeting recording/transcript analysis, consent-based live transcription, near-live copilot after safety evaluation, additional UK jurisdictions and international packs.

## Codex build order

1. Commercial architecture scaffolding while keeping the existing build green.
2. V2 epistemic domain and invariant tests.
3. PostgreSQL persistence and tenancy model.
4. Ingestion v2 with immutable originals, versions and source spans.
5. Case Brain merge, entity resolution, contradiction candidates and corrections.
6. Web MVP: auth, case, upload, timeline, evidence, disputed view and correction UX.
7. Professional export.
8. Case-grounded Q&A after provenance/retrieval tests pass.
9. Deferred features only after their relevant gates pass.

## Success definition

The product succeeds when a user can reliably answer:

- What happened?
- Who says that?
- Where is the evidence?
- What conflicts with it?
- What is missing?
- What changed when new evidence arrived?
- Can a professional use this case without rebuilding it from scratch?
