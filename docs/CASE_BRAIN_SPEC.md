# Case Brain Specification

Date: 2026-08-13

## Purpose

The Case Brain is the canonical structured state of a workplace-dispute case. Chat, summaries, timelines and exports are views over this state; none of them are the source of truth.

## Design goals

- preserve original evidence;
- preserve provenance at source-span level;
- distinguish assertion from established event;
- preserve disagreement instead of collapsing it;
- support incremental updates;
- support user/professional correction;
- support reproducible AI analysis;
- make professional handover auditable.

## Core entities

### Case

Fields should include:

- id;
- tenant/user ownership;
- jurisdiction;
- employment status;
- employer organisation id;
- employment start/end dates when known;
- case stage;
- user objectives;
- created/updated timestamps.

### Person

- id;
- case id;
- display name;
- role labels;
- organisation links;
- aliases;
- contact metadata where necessary;
- merge/split audit history.

### Organisation

- id;
- case id;
- name;
- type;
- aliases;
- relationships.

### Document

Represents the logical evidence item.

- id;
- case id;
- title;
- document type;
- origin channel;
- document date if known;
- uploader;
- processing status;
- sensitivity flags.

### DocumentVersion

Represents an immutable file version.

- id;
- document id;
- object-storage key;
- SHA-256;
- MIME type;
- size;
- received/imported timestamp;
- parser version;
- OCR status;
- duplicate/derived/original state.

### SourceSpan

The smallest addressable evidence region used for citation.

- id;
- document version id;
- page number where relevant;
- text start/end offsets;
- bounding box where image/layout citation is required;
- extracted text;
- extracted-text digest;
- parser/OCR version;
- extraction confidence.

### Assertion

The central epistemic object.

Suggested fields:

- id;
- case id;
- subject entity/reference;
- predicate;
- object/value;
- asserted by;
- event time or time range;
- assertion time;
- source span id;
- origin class;
- assertion class;
- dispute state;
- integrity state;
- extraction confidence;
- created-by model/provider/version when applicable;
- user verification state;
- superseded-by reference where applicable.

### Event

Represents something that may have happened, constructed from one or more assertions.

- id;
- case id;
- event type;
- start/end time;
- participants;
- event status;
- human-readable neutral label;
- verification state.

An event is not automatically true merely because assertions point to it.

### AssertionEventLink

Links assertions to events with relation type such as:

- supports;
- contradicts;
- qualifies;
- supersedes;
- contextualizes.

### Contradiction

- id;
- case id;
- assertion A;
- assertion B;
- contradiction type;
- detected by;
- resolution state;
- resolution note/reference;
- created/resolved timestamps.

### AnalysisNode

Stores AI interpretation separately from evidence.

- id;
- case id;
- analysis type;
- retrieval/source ids used;
- provider/model;
- prompt/template version;
- output;
- generated timestamp;
- verification status;
- superseded-by reference.

### AuditEvent

Append-only record of material state change.

Examples:

- extraction created;
- user corrected date;
- entities merged;
- assertion marked disputed;
- document version superseded;
- analysis regenerated after new evidence.

## Evidence classification

Avoid one scalar confidence score. Use orthogonal dimensions.

### Origin class

- OriginalContemporaneousRecord
- IndependentThirdPartyRecord
- EmployerAuthoredDocument
- EmployeeAuthoredDocument
- ParticipantOrWitnessStatement
- RetrospectiveNote
- TranscriptDerivedRecord
- OcrDerivedRecord
- AiGeneratedInference

### Assertion class

- DirectlyDocumentedEvent
- DirectQuotation
- AttributedAssertion
- UserAssertion
- EmployerAssertion
- ThirdPartyAssertion
- DerivedCalculation
- AiInference

### Dispute state

- Corroborated
- Uncorroborated
- Disputed
- Contradicted
- Superseded
- Incomplete
- Unverified

### Integrity state

- OriginalHashVerified
- Duplicate
- DerivedCopy
- Incomplete
- OcrUncertain
- MetadataUncertain

### User verification state

- NotReviewed
- Confirmed
- Rejected
- NeedsContext

## Provenance invariant

Every displayed evidence-based statement must be navigable through:

`displayed statement -> assertion/event -> source span -> document version -> original object/hash`

If any link is missing, the UI must not present the statement as source-backed.

## Example

Bad state:

> Employee had 12 sickness days.

Correct state:

- Assertion A: employer stated 12 sickness days; source = capability letter page 2.
- Assertion B: attendance data appears to record 10 sickness days; source = attendance report rows X–Y.
- Contradiction: numeric mismatch between A and B.
- Event remains unresolved until verified.

## Correction rules

User corrections do not delete history.

Example flow:

1. AI extracts event date as 12 March.
2. User selects `Wrong` and corrects to 13 March.
3. An `AuditEvent` records the correction.
4. The old assertion/version becomes superseded or rejected.
5. Dependent chronology/analysis is recalculated.
6. Previous AI output remains auditable.

## Incremental update rule

Adding a new document should reprocess only affected entities/assertions/events plus dependent analyses. Do not regenerate the entire case state from a monolithic prompt.

## Retrieval domains

Maintain two separate domains:

### Case Evidence RAG

Contains only the user's case evidence and structured case state.

### Legal/Process Authority RAG

Contains validated external authorities with jurisdiction and effective-date metadata.

The UI should clearly separate:

- what the evidence shows;
- what external guidance says;
- what is uncertain;
- what needs verification.

## Initial persistence approach

Use PostgreSQL as the system of record, relational link tables for the case graph, full-text search and pgvector for retrieval acceleration.

Do not introduce a dedicated graph database until graph traversal is demonstrated to be a real bottleneck.

## Required tests

- source-span chain cannot reference another tenant;
- assertion cannot be marked source-backed without a valid source span;
- AI inference cannot silently enter documentary evidence classes;
- contradictions preserve both sides;
- corrections create audit events;
- superseded versions remain traceable;
- duplicate file hashes do not create duplicate originals;
- user-facing citation ids always resolve;
- one new document does not mutate unrelated case records;
- export contains stable references back to original evidence.
