# ADR 0003: Private immutable original-evidence object storage

- Status: Accepted
- Date: 2026-08-14

## Context

CaseMesh PostgreSQL persistence records tenant-scoped logical original-object identities and SHA-256 hashes, but Milestone 3 deliberately did not store evidence bytes. Commercial evidence may contain workplace, health and third-party personal data. Original bytes therefore require a private, immutable, tenant-safe storage boundary before ingestion or web upload work begins.

PostgreSQL and an object store cannot share a distributed transaction. Privacy deletion also means immutable bytes cannot simply be retained forever.

## Decision

`CaseMesh.Storage` defines the provider-neutral, typed original-evidence API and storage orchestration. Its public operations accept `TenantId`, Matter id, original-object id and streams; they never accept caller-supplied bucket names or raw object keys. `CaseMesh.Storage.S3` implements the backend with the AWS S3 SDK. `CaseMesh.Persistence.Postgres` implements tenant-scoped storage metadata. `CaseMesh.Core` has no S3 or storage-provider dependency.

The S3-compatible object key is generated deterministically as:

`v1/tenants/{tenant UUID}/matters/{matter UUID}/originals/{original-object UUID}`

Filenames, Matter titles, people, email subjects and evidence text never enter the key. Equal hashes in different tenant or Matter scopes produce different physical authorization boundaries. Within one Matter, document versions sharing the existing logical original-object identity share one stored object.

Objects are created with `If-None-Match: *`. The adapter exposes no overwrite operation and never sets a public ACL or bucket policy. Identical retries verify the existing bytes; different content fails before storage because the incoming stream's computed SHA-256 does not match the registered logical original. S3 ETags are not treated as content hashes.

Incoming and downloaded streams are copied through a process-private, delete-on-close temporary file using a bounded buffer. SHA-256 and byte length are computed from actual bytes. This avoids whole-file memory buffering and ensures a read is released to its destination only after integrity verification. Missing or divergent physical objects fail explicitly.

Migration `0002_object_storage.sql` adds relational `original_object_storage` metadata with composite tenant/Matter/original-object/hash ownership, a generated-key check, global physical-locator uniqueness, forced RLS and immutable-update enforcement. Missing or wrong transaction-local tenant context remains fail-closed under ADR 0002.

The store sequence is: resolve the logical identity through tenant-scoped PostgreSQL, stage and verify bytes, conditionally create the object, then insert immutable metadata. If a newly created object's metadata insert fails, the service checks that no concurrent metadata claim exists and attempts compensating deletion. If cleanup cannot be confirmed, it reports an explicit retry-required compensation failure. A retry either completes metadata for matching bytes or detects divergence.

Matter and tenant deletion are storage-aware and privacy-first. Physical objects are deleted idempotently before metadata and relational scope deletion occur in one PostgreSQL transaction. The final transaction locks the owning scope and requires the current metadata set to equal the set whose physical objects were deleted. A concurrent store therefore causes a retry instead of having its metadata removed while its bytes are orphaned; an insert blocked behind the scope lock fails its foreign key after deletion and runs upload compensation. A partial physical failure leaves database ownership intact and is safely retryable. Database triggers reject the legacy raw Matter/tenant deletion APIs while storage metadata exists, preventing supported deletion from silently orphaning bytes.

Production endpoints require HTTPS. Plain HTTP is accepted only through an explicit loopback-only local-test override. Buckets are externally provisioned private; production provider-managed encryption at rest and access policy remain deployment responsibilities. This design does not claim end-to-end encryption. Credentials come from runtime configuration and are not stored in Core, PostgreSQL metadata or source control.

Real integration CI uses PostgreSQL 17 and Garage `v2.3.0` as an actively maintained, open-source S3-compatible service. Garage is initialized as an ephemeral single-node service with synthetic credentials and a private bucket authorization boundary. The test fixture creates an ephemeral bucket and verifies that an object written through the adapter is unavailable anonymously. The runtime adapter itself never creates public policies or ACL grants.

## Consequences

Object storage remains replaceable behind a typed boundary, while the first implementation is S3-compatible. The bounded staging file adds local disk I/O but provides deterministic pre-write hashing, retryable conditional creation and verified reads without unbounded memory use.

Signed URLs, browser-direct upload, web/API endpoints, malware scanning, MIME classification, parsing, OCR, extraction, KMS/client-side encryption and legacy SQLite-file migration are deferred. They require later authentication, ingestion and deployment decisions and are not part of Milestone 4.
