# ADR 0002: Commercial PostgreSQL persistence, tenant ownership and RLS

- Status: Accepted
- Date: 2026-08-14

## Context

CaseMesh now has a generic Matter evidence domain and a workplace-dispute extension, but only the preserved CaseMesh Live prototype has persistence. Commercial evidence contains sensitive workplace and health material, so tenant isolation and provenance-preserving storage must exist before ingestion or web/API work.

## Decision

Commercial persistence is implemented in the platform-neutral `CaseMesh.Persistence.Postgres` project with direct Npgsql and repository-owned SQL migrations. The generic Core owns a provider-neutral `TenantId` and validated snapshot/rehydration boundary; it has no PostgreSQL, Npgsql or ORM dependency.

Every commercial row is tenant-scoped. Matter-owned relationships use `(tenant_id, matter_id, id)` keys and composite foreign keys so a link cannot silently cross a tenant boundary. PostgreSQL Row-Level Security is enabled and forced on every tenant table. Repository operations run inside a transaction and set `casemesh.tenant_id` with transaction-local `set_config(..., true)`, so pooled connections do not retain tenant context after commit or rollback. Missing context yields no visible rows, and writes fail closed.

Audit events are append-only through the repository. A database trigger rejects direct UPDATE and DELETE. It permits only a database-driven cascading DELETE nested under whole-Matter or whole-tenant cleanup; there is no supported API to update, overwrite or directly delete an audit event.

Migrations are ordered embedded SQL resources with a visible `casemesh_internal.schema_migrations` ledger and a transaction-scoped advisory lock. Reapplying an applied migration is a deterministic no-op.

## Consequences

The legacy `CaseMesh.Infrastructure` SQLite repository remains separate and unchanged because it belongs to the single-user Windows Live prototype. It is not migrated or treated as the commercial database.

The first persistence layer uses Npgsql only; EF Core and other ORMs are not introduced because explicit SQL keeps composite tenant ownership, RLS policies, append-only triggers and provenance relationships reviewable. Object bytes, object-storage keys, authentication, memberships, web endpoints, pgvector and ingestion remain later milestones.

Application connections must use a non-superuser role without `BYPASSRLS`; the runtime store checks the effective PostgreSQL role and rejects a connection that can bypass RLS. Migrations require a separately controlled owner/admin connection. Integration tests create a restricted application role and prove correct, wrong, missing and pooled tenant-context behavior against real PostgreSQL, as well as rejection of an unsafe admin role.
