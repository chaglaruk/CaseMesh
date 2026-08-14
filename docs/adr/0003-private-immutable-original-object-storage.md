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

Objects are created with `If-None-Match: *`. The adapter exposes no overwrite operation and never sets a public ACL or bucket policy. Identical retries verify the existing bytes; different content fails before storage because the incoming stream's computed SHA-256 does not match the registered logical original. S3 ETags are not treated as content hashes. The adapter verifies bucket versioning fail-closed and rejects both enabled and suspended versioning: an unqualified delete in a versioned bucket would retain evidence-bearing versions after relational ownership was removed.

Incoming and downloaded streams are copied through a process-private, delete-on-close temporary file using a bounded buffer. SHA-256 and byte length are computed from actual bytes. This avoids whole-file memory buffering and ensures a read is released to its destination only after integrity verification. Missing or divergent physical objects fail explicitly.

Migration `0002_object_storage.sql` adds relational `original_object_storage` metadata with composite tenant/Matter/original-object/hash ownership, a generated-key check, global physical-locator uniqueness, forced RLS and immutable-update enforcement. Missing or wrong transaction-local tenant context remains fail-closed under ADR 0002. During an upgrade, restricted runtime roles that already have read/insert/delete privileges on the 0001 Matter table receive the corresponding storage-table privileges; read-only roles and `PUBLIC` gain nothing, and storage updates remain unavailable.

The store sequence is: acquire an identity-scoped PostgreSQL session advisory lease, resolve the logical identity through tenant-scoped PostgreSQL, stage and verify bytes, conditionally create the object, then insert immutable metadata. The lease spans both systems and serializes stores for the same original identity. Metadata insertion also takes a Matter `FOR KEY SHARE` lock, participating in the deletion protocol. If a newly created object's metadata insert fails, compensation runs before the identity lease is released, so no concurrent metadata claim can commit against bytes being removed. If cleanup cannot be confirmed, the service reports an explicit retry-required compensation failure. A retry either completes metadata for matching bytes or detects divergence. Advisory unlock is explicit before a connection returns to the pool; an unlock anomaly clears that pool rather than leaking coordination state.

Matter and tenant deletion are storage-aware and privacy-first. Physical objects are deleted idempotently before metadata and relational scope deletion occur in one PostgreSQL transaction. The final transaction locks the owning scope and requires the current metadata set to equal the set whose physical objects were deleted. A concurrent store therefore causes a retry instead of having its metadata removed while its bytes are orphaned; an insert blocked behind the scope lock fails its foreign key after deletion and runs upload compensation. A partial physical failure leaves database ownership intact and is safely retryable. Database triggers reject the legacy raw Matter/tenant deletion APIs while storage metadata exists, preventing supported deletion from silently orphaning bytes.

Production endpoints require HTTPS. Plain HTTP is accepted only through an explicit loopback-only local-test override. Buckets are externally provisioned private and unversioned; production provider-managed encryption at rest and access policy remain deployment responsibilities. The application credential needs permission to read bucket-versioning state so unsupported retention semantics fail closed. This design does not claim end-to-end encryption. Credentials come from runtime configuration and are not stored in Core, PostgreSQL metadata or source control; the options container uses ordinary class diagnostics so record-generated formatting cannot disclose credentials.

Real integration CI uses PostgreSQL 17 and MinIO built from the exact upstream security-fix commit `9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a` (`RELEASE.2025-10-15T17-29-55Z`). That release fixes the then-current session-policy advisory and, unlike Garage 2.3.0, enforces conditional `PutObject` preconditions required by the immutable create boundary. The service runs ephemerally with synthetic root credentials and a private bucket authorization boundary. The test fixture creates an ephemeral bucket and verifies both anonymous denial and that an existing physical object with missing metadata cannot be overwritten. The runtime adapter itself never creates public policies or ACL grants.

## Consequences

Object storage remains replaceable behind a typed boundary, while the first implementation is S3-compatible. The bounded staging file adds local disk I/O but provides deterministic pre-write hashing, retryable conditional creation and verified reads without unbounded memory use.

Signed URLs, browser-direct upload, web/API endpoints, malware scanning, MIME classification, parsing, OCR, extraction, KMS/client-side encryption and legacy SQLite-file migration are deferred. They require later authentication, ingestion and deployment decisions and are not part of Milestone 4.
