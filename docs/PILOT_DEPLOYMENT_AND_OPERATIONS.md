# Closed-pilot deployment and operations contract

## Deployment boundary

CaseMesh is deployed as one ASP.NET Core API/worker process, one Next.js Web process, PostgreSQL 17+, and one private S3-compatible bucket with versioning disabled. For a new database, run `CaseMesh.DbMigrate --through 0001`, create and grant the restricted runtime login role on the Matter tables, then run `CaseMesh.DbMigrate` to current. Migration 0008 fails closed when it cannot discover that role. Run database migration and bucket provisioning as release jobs before switching traffic. The runtime is not a migrator and must use a PostgreSQL login with `NOSUPERUSER NOBYPASSRLS`.

Terminate TLS at a trusted ingress and expose only the configured `CaseMesh__PublicOrigin`. Forwarded headers must be accepted only from explicitly configured proxy addresses/networks. Do not expose PostgreSQL, object storage, scanner/OCR processes, the pilot-admin tool, or internal service ports publicly.

## Required API configuration

Non-secret settings:

- `CaseMesh__PostgresConnectionString` (runtime role; the password remains a secret)
- `CaseMesh__PublicOrigin` (`https://`)
- `CaseMesh__OidcAuthority`, `CaseMesh__OidcClientId`
- `CaseMesh__S3Endpoint`, `CaseMesh__S3Region`, `CaseMesh__S3BucketName`
- `CaseMesh__ClamAvExecutablePath`, `CaseMesh__TesseractExecutablePath`, `CaseMesh__PopplerExecutablePath`
- `CaseMesh__BuildIdentity` (immutable image/commit identifier; not a secret)
- bounded upload settings and `CaseMesh__AllowInsecureLocalObjectStorage=false`

Secrets must enter through the deployment secret store: runtime PostgreSQL password, `CaseMesh__OidcClientSecret`, `CaseMesh__S3AccessKey`, and `CaseMesh__S3SecretKey`. Never put them in committed files, images, command arguments, telemetry, or ordinary logs.

The migration job receives a distinct `CaseMesh__PostgresAdminConnectionString`. The object-store provisioning job receives the S3 settings and credentials and must pass the private-bucket/versioning gates. The application refuses production startup when required configuration is absent.

## Health and rollout

- `GET /health/live` proves only that the API process can answer.
- `GET /health/ready` checks migration/runtime-role safety, private object storage, ingestion executables, evidence/deletion worker heartbeats, Q&A provider configuration, and build identity. It returns 503 when any component is not ready and never returns paths, credentials, evidence, exception messages, or provider keys.

Use liveness only for process restart. Use readiness to admit traffic. During rollout, require migrations and private bucket provisioning to complete, start the API, wait for readiness, then switch traffic. Roll back the image/config only; do not reverse an applied data migration unless a separately reviewed forward repair exists.

## Entitlements, retention, and support

Closed-pilot defaults are persisted per tenant by migration 0008. They bound active Matters, original bytes, evidence counts, ingestion attempts, daily Q&A/context, exports, and conversation history. The browser cannot grant capacity. Use the offline tool with opaque IDs:

```text
CaseMesh.PilotAdmin grant <tenant-id> <tier-code>
CaseMesh.PilotAdmin status <tenant-id> <matter-id>
CaseMesh.PilotAdmin reconcile <tenant-id>
CaseMesh.PilotAdmin benchmark <tenant-id> <matter-id> <3-100>
```

Every command also requires `CaseMesh__PilotAdminTenantId` to exactly match the requested tenant, making the operator's approved scope explicit and preventing accidental cross-tenant diagnostics. `grant` alone requires the privileged operator connection. All other commands require the RLS-enforced runtime connection and cannot search globally. The benchmark command is read-only. Output contains only typed counts, durations, tier, and opaque IDs. Do not paste customer-provided text into command arguments.

Generated exports expire under each tenant's policy and are unavailable after expiry. Maintenance deletes their private bytes before metadata and prunes expired reservations, failed job rows, daily counters, and privacy-safe operational events. Original evidence has no timer deletion: authenticated Matter deletion creates a durable job that deletes generated artifacts, then originals, then relational state. Deletion failures use exponential backoff and enter an operator-visible terminal state after five failed attempts; alert on any `deletion-terminal-failure` queue depth. Completed deletion receipts remain until tenant deletion as proof of erasure. Never delete database rows manually to force completion.

## Backup, recovery, and incidents

Encrypt PostgreSQL backups and object storage at rest with access limited to the deployment/restore role. Restore drills must use synthetic data and verify tenant RLS, exact original/export hashes, private bucket policy, deletion replay, and readiness. Backup retention must not exceed the approved customer/privacy schedule; document the unavoidable backup-deletion lag in pilot terms.

For an incident, preserve typed usage/job metadata, build identity, and infrastructure audit logs. Do not copy evidence bodies into tickets or analytics. Rotate affected credentials, disable readiness/traffic if tenant isolation or private delivery is uncertain, and reconcile the explicitly identified tenant/Matter using the safe CLI.
