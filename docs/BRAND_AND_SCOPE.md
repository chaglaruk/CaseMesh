# CaseMesh — Brand and Product Scope

Date: 2026-08-13

## Brand decision

The product, repository, solution, projects and namespaces are now **CaseMesh**.

Canonical public domain: **casemesh.dev**.

`HR Companion` is a retired working name. It may remain only in historical research/source titles or deliberate legacy migration constants/tests where changing the text would be inaccurate or break backwards compatibility.

## Core positioning

Initial market positioning:

> **CaseMesh is an evidence operating system for workplace disputes.**

Supporting promise:

> Turn a messy workplace history into a source-backed case file: what happened, who said what, what proves it, what conflicts, and what is missing.

Avoid positioning CaseMesh as:

- an AI employment lawyer;
- a generic employment-law chatbot;
- a generic meeting assistant;
- an employer HR system;
- a tool whose main value is aggressive grievance/claim drafting.

## Long-term platform vision

CaseMesh should be architected as a **matter-centric evidence and context platform**, while commercial go-to-market remains deliberately narrow.

The long-term shared loop is:

`Context -> Prepare -> Conversation/Action -> Review -> New Evidence -> Updated Matter`

The durable core is the structured evidence state, not any one conversation interface.

## Product layers

These are architectural/product layers, not necessarily separate paid SKUs at launch.

### CaseMesh Core

The reusable evidence engine:

- matters;
- documents and immutable versions;
- source spans;
- people and organisations;
- assertions;
- events;
- provenance;
- contradictions;
- corrections;
- audit history;
- retrieval;
- professional export.

Core primitives should not be unnecessarily hard-coded to employment law.

### CaseMesh Work

The first commercial vertical: UK workplace disputes, initially focused on England and Wales where legal/process functionality is enabled.

Employment-specific extensions may include:

- employment terms;
- sickness/absence and Occupational Health records;
- reasonable-adjustment requests;
- grievance/disciplinary/capability stages;
- Acas process state;
- employment-law authorities;
- employment-specific deadlines only where validated and permitted.

### CaseMesh Professional

Professional handover and intake workflows for claimant-side employment solicitors and, later, unions/insurers where validated.

Initial professional value:

- source-linked chronology;
- evidence index;
- people map;
- disputed-facts matrix;
- missing factual questions;
- original evidence bundle;
- concise neutral case brief.

### CaseMesh Live

A later capability that uses an already-built Matter/Case Brain during important conversations.

Progression:

1. meeting preparation;
2. transcript/recording upload after the meeting;
3. automatic extraction of new assertions/events/commitments into the Matter;
4. consent-based live transcription;
5. source-grounded near-live suggestions after safety, latency and legal/privacy gates pass.

CaseMesh Live is **not** the MVP and must not become the critical path before the evidence engine proves value.

## Why the architecture is broader than the launch market

The initial market should stay narrow because workplace disputes have a strong combination of urgency, document volume, contradictions, professional handover and willingness-to-pay moments.

The underlying CaseMesh primitives can later support adjacent high-context disputes or matters, but expansion must be evidence-led.

Do not build tenant, insurance, sales, interview, consumer-complaint or general meeting verticals merely because the core could support them.

A new vertical requires proof of:

1. repeated evidence-organisation pain;
2. clear willingness to pay or professional ROI;
3. a distribution path;
4. acceptable legal/privacy risk;
5. strong reuse of CaseMesh Core;
6. no damage to the employee-side trust proposition.

## Employer-side boundary

Do not add employer-side HR analytics, employee-risk scoring, or tools designed to help employers defeat worker claims without an explicit strategic review.

CaseMesh's trust moat depends on workers and their advisers believing that sensitive evidence is not being repurposed against them.

## UI information architecture

Prefer **Matters** as the top-level product concept rather than separating the product into unrelated `Cases` and `Meetings`.

A future Matter can expose:

- Overview
- Timeline
- Evidence
- People
- Disputed
- Questions
- Prepare
- Review
- Export

`Live` appears only when the capability is enabled and appropriate.

## Domain architecture rule

The root domain aggregate should be generic enough to survive future verticals. Prefer concepts such as:

- `Matter`
- `Person`
- `Organisation`
- `Document`
- `DocumentVersion`
- `SourceSpan`
- `Assertion`
- `Event`
- `Contradiction`
- `Objective`
- `Task`
- `AnalysisNode`
- `AuditEvent`

Employment-specific records should extend or project from that core rather than redefine it.

## Website/domain intent

Use `casemesh.dev` as the canonical brand domain.

Initial public site should explain one narrow use case: workplace-dispute evidence organisation and professional handover.

Do not make the homepage a catalogue of hypothetical future verticals.

A future hosted product may use `app.casemesh.dev`; this is a planning convention, not a deployment requirement for the first engineering batch.

## Naming migration status

Completed on 2026-08-13:

1. repository/product brand -> **CaseMesh**;
2. GitHub repository -> `chaglaruk/CaseMesh`;
3. solution/projects/namespaces -> `CaseMesh.*`;
4. local project folder/origin -> CaseMesh repository;
5. legacy local app-data/Credential migration identifiers intentionally preserved where required for backwards-compatible migration.

Do not create another naming-migration batch. Future package/application/deployment identifiers should use CaseMesh when introduced.

## Product decision rule

When choosing between a flashy interaction feature and stronger evidence integrity, prioritize evidence integrity.

The live experience becomes differentiated only because CaseMesh already knows the matter reliably.
