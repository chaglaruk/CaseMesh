# ADR 0005: Safe evidence ingestion and versioned exact source spans

Status: Accepted

Date: 2026-08-14

## Context

CaseMesh now has tenant-aware PostgreSQL persistence and private immutable original-object storage. The next commercial step must turn supported synthetic or customer-supplied evidence into exact, durable `SourceSpan` records without treating the document, its instructions, or extracted text as trusted application commands. This is a safety and provenance boundary, not an AI interpretation boundary.

The legacy Windows importer is local-path and extension oriented. It remains available to the prototype but is not a suitable commercial security boundary and is not migrated here.

## Decision

Add a platform-neutral `CaseMesh.Ingestion` project. It depends on Core and the provider-neutral storage contract, while Core gains no parser, OCR, malware, storage-provider, or PostgreSQL dependency. Commercial orchestration accepts typed tenant, Matter, document-version, and original-object identities; it never accepts a raw object key or local customer filename.

The pipeline always performs a tenant-scoped `ReadVerifiedAsync` first. The storage layer verifies immutable SHA-256 and byte length while streaming. A bounded temporary file with owner-only Unix permissions is used for tools that require a seekable path. The file and rasterized pages are best-effort deleted after the attempt. Evidence bytes and text are not written to ordinary logs or failure codes.

### Safety and media routing

ClamAV is represented by `IMalwareScanner`; the initial adapter invokes `clamscan` without a shell. A clean result is required before any format detector or parser runs. A threat produces a durable quarantined attempt. Scanner absence, timeout, or error fails closed with a typed failure. CI uses the real ClamAV engine with an isolated synthetic EICAR signature database, avoiding a flaky production-signature download during every build.

Content routing uses byte signatures and container structure rather than caller MIME type or filename. The allowlist is PDF, DOCX, EML, UTF-8 TXT, PNG, and JPEG. ZIP is accepted only when its required DOCX entries are present. Bounded input bytes, ZIP entries and expansion, pages, extracted characters, regions, and external-process runtime are deterministic controls. Macros, scripts, active content, attachment bytes, and instruction-like document text are never executed.

### Parsing and OCR

Native extraction uses PdfPig for page text, Open XML for DOCX paragraphs/table cells, MimeKit for EML headers/text body/attachment metadata, and strict UTF-8 for TXT. PNG/JPEG uses the provider-neutral `IOcrEngine`; the initial adapter invokes Tesseract 5 and consumes word-level TSV so stored OCR text has genuine pixel bounding boxes and confidence. Native PDF text is preferred and does not invoke OCR. Image-only PDFs may use the replaceable `IPdfPageRasterizer`; the initial Poppler `pdftoppm` adapter creates bounded PNG pages before Tesseract. No bounding box is invented for native text.

Rasterization derives its maximum output dimension from both the configured dimension and pixel-count limits, and every generated PNG is signature- and dimension-validated again before OCR. OCR-only span sets record parser identity as `none`; the OCR provider/version remains in its dedicated relational columns and on each derived region rather than being mislabeled as a native parser.

### Relational provenance and idempotency

Migration `0003_ingestion.sql` adds:

- append-only `ingestion_attempts`, containing status, scanner identity/result, deterministic timestamps, byte length, and typed non-content failure codes;
- append-only `ingestion_span_sets`, uniquely keyed by tenant/Matter/document version and the complete pipeline fingerprint;
- mutable `document_ingestion_state`, a queryable current-status projection that references the latest attempt and current span set; and
- optional ingestion provenance columns on the existing `source_spans` system of record: span set, ordinal, stable locator kind/value, native/OCR route, provider/version, and real OCR bounding box.

The fingerprint includes pipeline, scanner, parser, OCR provider, and provider versions. Retrying the same immutable document with the same fingerprint returns the existing span set and cannot duplicate spans. A version change creates a new span set; old spans and attempts remain. Region and span-set identifiers are deterministic hashes of typed ownership, pipeline identity, and ordinal. Text digests are recomputed before persistence, and existing identifiers may only be reused when all immutable provenance fields match.

All new tables use the ADR 0002 transaction-local tenant context, RLS and `FORCE ROW LEVEL SECURITY`. Composite foreign keys carry tenant, Matter, document, document version, original object, attempt, and span-set ownership so a cross-tenant, cross-Matter, or cross-version link cannot be created even if application filtering is wrong. Restricted roles created before migration receive only the needed table privileges; `PUBLIC` gains none. Runtime store connections still reject `SUPERUSER` and `BYPASSRLS` roles.

## Failure and retry model

The object store, malware process, parser/OCR tools, and PostgreSQL do not form a distributed transaction. Immutable originals are never modified. A successful parse is committed with its span set, exact spans, completed attempt, and current-state projection in one PostgreSQL transaction. Failures persist a typed attempt and current failure/quarantine state in one transaction. A failed later pipeline retains the last completed span-set pointer while recording the new failed attempt and status; quarantine clears the pointer, and neither path deletes historical spans. A stale attempt cannot overwrite a later current-state timestamp. Equal deterministic timestamps are permitted because the row lock serializes the actual attempts; rejecting equality would make injected fixed clocks unusable. If failure-state persistence itself fails, the caller receives an explicit persistence failure containing the two exceptions but no evidence body; retry remains required.

## Security and operations

Temporary inputs are bounded, generated from non-sensitive identifiers, and not exposed through Core models. External commands are executed with `ProcessStartInfo.ArgumentList`, no shell, redirected output, a timeout, and process-tree termination on timeout or caller cancellation. Ordinary exception messages and database failure codes contain classifications only, not evidence text, filenames, object keys, hashes, credentials, or tool stderr.

The required Linux CI gate uses PostgreSQL 17, the same pinned-source MinIO strategy as Milestone 4, real ClamAV, Tesseract, and Poppler. Missing required service/tool configuration fails the gate instead of silently skipping. The Windows full-solution, PostgreSQL, and object-storage jobs remain separate regression gates.

## Consequences and deferred work

The ingestion layer now produces exact, queryable documentary spans with explicit native/OCR provenance and durable processing history. Extraction confidence remains a provider observation, never truth confidence. Supported privacy deletion remains the storage-aware Matter/tenant workflow from ADR 0003; there is no document- or document-version-delete API in this milestone, and direct child deletion remains rejected so append-only extraction history cannot be silently removed. EML attachment bytes are described but not recursively ingested. Password-protected documents, ZIP/archive ingestion, MIME pipelines, commercial malware operations, parser sandbox containers, richer layout reconstruction, OCR language selection, and production task scheduling require later milestones.

AI assertion/event/entity extraction, Matter Brain merging, embeddings, pgvector, Q&A, web/API/auth/billing, mailbox integrations, legal outcome scoring, deadlines, and CaseMesh Live are deliberately out of scope.
