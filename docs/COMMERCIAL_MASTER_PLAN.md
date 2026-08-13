# CaseMesh — Commercial Master Plan

Date: 2026-08-13

## Direction

CaseMesh launches as an **evidence operating system for workplace disputes** on top of a reusable matter-centric evidence core.

Brand and scope decisions in `docs/BRAND_AND_SCOPE.md` take precedence over older working-name assumptions.

The commercial critical path is:

`evidence -> provenance -> structured Matter/Case Brain -> chronology -> dispute map -> correction -> professional handover`

The existing Windows live-meeting prototype remains useful research and may return later as **CaseMesh Live**, but it is no longer the first commercial wedge.

## First product surfaces

### Employee workspace

- create a Matter;
- upload evidence;
- inspect a source-linked timeline;
- see who made each assertion;
- see contradictions and missing evidence;
- correct extraction;
- ask matter-grounded questions;
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

## Product architecture

The platform has four conceptual layers:

- **CaseMesh Core** — reusable Matter/Case Brain, evidence and provenance;
- **CaseMesh Work** — first commercial vertical for workplace disputes;
- **CaseMesh Professional** — solicitor/professional handover and intake;
- **CaseMesh Live** — later preparation, transcript review and live grounded assistance.

These are architectural layers, not launch pricing tiers.

Architecture may be broader than go-to-market, but the roadmap stays workplace-dispute focused until product-market fit is demonstrated.

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

- single-user SQLite as the main commercial system of record;
- WPF as the primary commercial UI;
- the three-state `CaseFact` model;
- meeting state as the central domain object;
- live-audio gates as top repository priority;
- FTS5-only retrieval as the long-term platform retriever.

Do not delete the existing meeting projects during the first refactor. Keep them buildable and isolate them from the new commercial critical path.

Remaining `HRCompanion.*` solution/project/namespace identifiers are legacy names. Rename them to `CaseMesh.*` in a separate behavior-neutral batch.

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

## Matter / Case Brain

The root commercial aggregate is `Matter`.

Core entities should contain at least:

`Matter`, `Person`, `Organisation`, `Document`, `DocumentVersion`, `SourceSpan`, `Assertion`, `Event`, `AssertionEventLink`, `Communication`, `Contradiction`, `Task`, `Objective`, `AnalysisNode`, `AuditEvent`.

Employment-specific records such as `EmploymentTerm`, `HealthAbsenceEvent`, workplace-process state, deadlines and legal authorities sit above this reusable core.

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
10. Legal/process material and user evidence are separate retrieval domains.
11. Generic Matter entities do not require employment-specific fields.

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
 -> Matter Brain merge
 -> retrieval
 -> stronger reasoning when needed
 -> citation verification
 -> output
```

Use lower-cost models for extraction/classification and stronger models for cross-document ambiguity, contradiction analysis and professional synthesis.

Keep authorization, tenancy, hashes, citation existence, date arithmetic, entitlements and destructive actions deterministic.

## MVP navigation

Top level: **Matters**.

Inside a Matter:

1. Overview
2. Timeline
3. Evidence
4. People
5. Disputed
6. Questions
7. Prepare
8. Export

`Review` is added with meeting/transcript ingestion. `Live` is later.

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

Finalize Core vs workplace-extension boundaries, build a CaseMesh prototype, create synthetic eval data, interview employees and solicitors, prepare data-flow/compliance materials, and run a controlled concierge beta.

### Phase 1 — private evidence MVP

Build authentication, Matters, secure uploads, original hashes/versions, parsing/OCR, source spans, entities, assertions/events, chronology, contradictions, corrections/audit trail and professional export.

### Phase 2 — commercial pilot

Add quotas, the chosen commercial model, matter-grounded Q&A, gap analysis, meeting preparation, mature export/delete/support processes and professional workflows.

### Phase 3 — public England & Wales launch

Launch workplace-dispute positioning on `casemesh.dev` with refined onboarding, selected email forwarding, versioned guidance where appropriate, reminders, accessibility, security testing and organic acquisition surfaces.

### Phase 4 — professional ecosystem

Add portal/annotations, organization roles, SSO, APIs and union/insurer pilots only after repeated demand.

### Phase 5 — CaseMesh Live

Add uploaded meeting recording/transcript analysis, then consent-based live transcription and near-live source-grounded assistance only after safety, privacy, latency and legal gates pass.

### Phase 6 — adjacent vertical experiments

Test one adjacent high-context dispute/matter vertical at a time only after Core + Work prove product-market fit. Do not pre-build generic meeting, sales or interview products.

## Codex build order

0. Mechanical rename of legacy `HRCompanion.*` identifiers to `CaseMesh.*`, behavior unchanged.
1. V2 generic evidence-domain scaffolding while keeping the existing build green.
2. Matter-root epistemic domain and invariant tests.
3. Employment-specific extension types needed by synthetic workplace fixtures.
4. PostgreSQL persistence and tenancy model.
5. Ingestion v2 with immutable originals, versions and source spans.
6. Matter Brain merge, entity resolution, contradiction candidates and corrections.
7. Web MVP: auth, Matters, upload, timeline, evidence, disputed view and correction UX.
8. Professional export.
9. Matter-grounded Q&A after provenance/retrieval tests pass.
10. Meeting preparation.
11. Live capabilities only after their separate gates pass.

## Success definition

The product succeeds when a user can reliably answer:

- What happened?
- Who says that?
- Where is the evidence?
- What conflicts with it?
- What is missing?
- What changed when new evidence arrived?
- What should I prepare for next?
- Can a professional use this Matter without rebuilding it from scratch?

CaseMesh Live succeeds later only if it can use that reliable Matter state without weakening those guarantees.
