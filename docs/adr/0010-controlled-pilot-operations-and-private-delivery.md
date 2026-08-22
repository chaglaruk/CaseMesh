# ADR 0010: Controlled pilot operations and private derived delivery

## Status

Accepted for the closed workplace-dispute pilot.

## Context

The authenticated Web MVP, immutable evidence pipeline, Matter Brain, professional export and citation-gated Q&A establish product correctness, but they do not by themselves bound cost, deliver generated exports privately, prove cross-system privacy deletion, or expose safe operational health. A closed pilot needs deterministic server-side controls without adding payment processing, a public administration surface, vendor-specific observability or distributed infrastructure.

## Decision

### Entitlements, reservations and accounting

Each tenant has one persisted `pilot_entitlements` row. It contains explicit limits for active Matters, original bytes, evidence items, processing attempts, Q&A requests/context, exports and retained history, plus retention settings. New tenants receive the conservative `closed-pilot` defaults. A narrowly scoped offline pilot-admin command may change the row through a privileged operator connection; no browser or public API can grant entitlements and no UI flag implies billing status.

Hard count/byte limits use short-lived tenant-scoped PostgreSQL reservations. The reservation transaction takes a deterministic tenant resource advisory lock, measures canonical rows plus unexpired reservations and inserts the reservation only when the limit remains satisfied. The application performs the cross-system operation and then releases the reservation after canonical state accounts for it; failure releases it without claiming usage. Daily Q&A and export limits use atomic `INSERT ... ON CONFLICT ... DO UPDATE ... WHERE quantity + requested <= limit`. These controls are PostgreSQL/RLS application controls, not model decisions.

Usage events contain typed categories, opaque Matter IDs, quantities, durations and bounded outcome codes only. They never contain filenames, evidence text, names, questions, answers, prompts, credentials, hashes or raw provider keys. Conversation history remains disabled (`0`) because Q&A transcripts are not persisted.

### Private generated export delivery

Professional export generation remains deterministic. The bundle ZIP is stored through a generated-artifact storage abstraction under a typed key separate from immutable originals:

`v1/tenants/{tenant}/matters/{matter}/generated/exports/{export}/{kind}`

The object remains private; the browser receives only opaque CaseMesh identifiers and an application download route. The API re-authorizes tenant membership, reads the object server-side, verifies SHA-256 and length against immutable export audit metadata, and streams it without exposing a bucket or provider key. Re-storing the same identity is idempotent only when bytes and metadata match. Artifacts have a configurable expiry; expiry prevents delivery and maintenance removes physical bytes before metadata.

Original evidence bytes are not copied into the bundle. The existing original-evidence manifest remains metadata-only.

### Privacy deletion and reconciliation

Matter deletion becomes an explicit durable job with `pending`, `processing`, `completed` and `retryable-failure` states. A per-Matter advisory lock serializes deletion with upload, ingestion, correction, Q&A and export work. Processing deletes derived export objects first, then immutable originals, and only then commits the relational Matter cascade. A physical/provider failure leaves the Matter and job retryable and never reports completion. Replays are idempotent.

A simple hosted maintenance loop claims deletion jobs with leases, recovers stale leases, removes expired export objects, expires stale quota reservations and prunes typed operational metadata under each tenant policy. Retrieval uses native PostgreSQL FTS indexes over canonical rows, so the Matter cascade removes index entries atomically; there is no separate retrieval object to reconcile. Physical orphan discovery is limited to provider capabilities and explicit tenant prefixes; the pilot does not perform an unrestricted cross-tenant bucket crawl.

### Health, telemetry and support boundary

`/health/live` proves only that the process responds. `/health/ready` reports a bounded component/status document and returns not-ready when PostgreSQL schema/runtime-role safety, private object storage, required ingestion dependencies, the background worker heartbeat, or the configured Q&A provider is unavailable. It exposes no configuration values, credentials, paths, evidence or exception messages. Build identity is a non-secret readiness field.

Telemetry uses standard .NET `Meter` counters/histograms with fixed low-cardinality tags: route template, outcome and typed operation/failure category. Questions, answers, evidence, filenames, names, hashes, credentials and raw object keys are prohibited as tags or log fields. This remains OpenTelemetry-compatible without selecting a commercial backend.

Support operations use explicit tenant/Matter opaque identifiers through an offline CLI and the same RLS-aware repositories. There is no staff Web portal, global data search or hidden RLS bypass. Entitlement mutation requires a separate privileged operator connection and is outside the public runtime role.

### Configuration and accessibility

Production configuration validates HTTPS origin, OIDC, private S3 settings, runtime database role behavior, and explicit scanner/OCR/rasterizer executable configuration. Retention and tenant-specific quotas are persisted rather than trusted from the frontend. Deployment documentation distinguishes runtime and migrator/operator credentials and lists liveness/readiness routes and trusted-proxy assumptions.

Core browser flows use semantic landmarks/headings, visible keyboard focus, labelled controls and errors, textual status/dispute indicators, responsive overflow handling and reduced-motion rules. Automated axe checks complement a documented manual keyboard/screen-reader/contrast checklist; passing automation alone is not a WCAG conformance claim.

## Consequences

- The closed pilot has deterministic limits and recoverable cross-system operations without Stripe, Kafka, Kubernetes, a public admin API or vendor telemetry.
- PostgreSQL remains the system of record for entitlements, reservations, usage and lifecycle status; object storage remains authoritative for physical generated bytes.
- Per-Matter locks deliberately trade same-Matter write concurrency for evidence/currentness integrity. Independent tenants and Matters remain concurrent.
- Generated export delivery is server-streamed, which is simpler and easier to authorize than signed URLs at pilot scale.
- Original evidence is never timer-deleted. Only an explicit Matter privacy-deletion lifecycle can remove it.
- Representative pilot benchmarks, rather than assumption, decide whether Issue #12 batching is justified. Repository-wide action/container digest pinning remains Issue #13 unless handled as a separate coherent supply-chain change.

## Out of scope

Payment processing, legal merits/outcome automation, external authority RAG, autonomous filing, broad staff administration, mailbox ingestion, CaseMesh Live, Kubernetes and commercial observability vendor selection remain out of scope.
