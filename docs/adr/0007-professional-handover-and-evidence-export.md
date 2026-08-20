# ADR 0007: Deterministic professional handover and evidence export

- Status: Accepted
- Date: 2026-08-21

## Context

ADR 0006 creates a tenant-scoped, source-linked Matter Brain while preserving documentary assertions, contradictions, corrections and model-derived analysis as distinct epistemic records. CaseMesh now needs a professional handover that a solicitor, adviser or employee can inspect without rebuilding the Matter, while ensuring that the export is a read model rather than a second source of truth. The export must not imply legal merits, leak provider storage details, or silently detach a claim from its immutable document provenance.

## Decision

Add the platform-neutral `CaseMesh.ProfessionalExport` project above `CaseMesh.Core` and `CaseMesh.MatterBrain`. `ProfessionalExportGenerator` accepts one explicit tenant/Matter identity, one caller-generated export identity, the canonical evidence/workplace/Matter Brain state and safe document-processing metadata. It does not read object storage, mutate the Matter or accept arbitrary filenames.

Each export contains:

- a neutral DOCX handover brief;
- separate evidence-index, chronology, assertion and contradiction CSV files;
- a versioned Matter manifest JSON file;
- an original-evidence logical-identity JSON manifest; and
- a ZIP containing those payload artifacts.

The generator assigns stable scoped references such as `DOC-00001`, `SRC-00001`, `AST-00001` and `CHR-00001` after deterministic sorting. Documentary citations retain the complete assertion → source span → document version → original-object/hash chain. Conflicting, rejected and superseded records remain visible. AI inference is labelled and never receives a fabricated documentary citation. Workplace request, response and implementation records remain separate, as do occupational-health recommendations and employer actions.

## Determinism and safety

Generation uses an explicit `TimeProvider`. Canonical snapshot material is normalized and sorted before SHA-256 hashing. JSON property naming and null handling, CSV quoting and line endings, artifact order, filenames, ZIP entry order, compression mode and ZIP timestamps are fixed. Repeating the same export identity, snapshot and clock produces byte-identical payloads and digests. Input size and record counts are bounded before generation.

DOCX is produced as a minimal Open Packaging Convention document without adding a production document-library dependency. The test project opens it through the existing centrally managed Open XML package. Filenames contain only typed Matter/export identifiers and fixed suffixes; Matter titles, evidence text, people, employers and source filenames cannot enter a path. ZIP entries are generated single-segment names and cannot traverse directories.

The neutral brief and deterministic missing-evidence questions describe attribution, provenance and unresolved records. They do not provide legal advice, recommend action, calculate deadlines or predict liability, compensation or outcomes.

## PostgreSQL audit metadata and tenancy

Migration `0005_professional_exports.sql` adds relational export-run, inclusion and artifact-digest tables. It stores export identity, versioned schema/template identifiers, snapshot and artifact SHA-256 digests, generated time, exact included canonical identifiers, safe artifact names and byte lengths. Generated document/CSV/JSON/ZIP bytes and evidence text are not stored in PostgreSQL by this milestone.

Every row carries `(tenant_id, matter_id)`. Composite foreign keys require every included document version, source span, assertion, event and contradiction to belong to the export's tenant/Matter. All tables use `RLS` and `FORCE ROW LEVEL SECURITY` with ADR 0002's transaction-local context. The restricted runtime role receives `SELECT` and `INSERT` only. Rows reject update, direct delete and truncate, while database-driven whole-Matter or tenant privacy cascades remain possible.

`PostgresProfessionalExportService` resolves canonical state and ingestion metadata inside a tenant-scoped transaction, generates the package in memory, and then appends its audit metadata. A wrong or missing tenant context cannot resolve the Matter or export run. Reusing an export identity is idempotent only when all metadata is equal; divergent reuse fails. The service returns no raw bucket key, storage credential or public URL.

## Consequences and deferred work

This milestone supplies deterministic DOCX, CSV, JSON and ZIP artifacts plus an immutable audit trail. PDF is deferred because it is optional for this milestone and a reliable cross-platform renderer would add operational and dependency surface without improving the provenance gate. Artifact persistence/delivery, signed links, web/API/auth/billing, legal-authority retrieval, statutory calculators, outcome or compensation scoring, autonomous filing, mailbox integration and CaseMesh Live remain out of scope.

Original evidence bytes are deliberately excluded from the bundle by default. A later delivery boundary may offer an explicitly authorized evidence-byte package, but it must resolve tenant-scoped storage metadata and preserve the object-store controls from ADR 0003.
