# Issue #12 PostgreSQL write re-evaluation

Date: 2026-08-22

## Workload and method

The real PostgreSQL 17 integration benchmark persists one synthetic closed-pilot Matter containing 100 immutable document versions, 100 exact source spans, and 100 attributed assertions. It performs one warm-up save followed by twenty complete snapshot saves through the production `PostgresMatterStore` and RLS-enforced runtime role. The fixture contains no personal data.

Local reference environment: PostgreSQL 17 Docker service on the Windows development host, Release build, local TCP connection. Result:

```json
{"documents":100,"sourceSpans":100,"assertions":100,"iterations":20,"medianMilliseconds":753.19775,"p95Milliseconds":927.8285}
```

The integration test intentionally asserts data scale and successful real-service completion, not a machine-specific latency threshold. `CaseMesh.PilotAdmin benchmark` provides an operator-visible, read-only median/p95 load measurement for an explicitly selected synthetic or approved pilot Matter without printing its contents or writing a stale snapshot back to the Matter.

## Decision

Do not fold a writer rewrite into Issue #23. Roughly one second for a full 100-document snapshot is visible but not a closed-pilot safety or correctness blocker: evidence ingestion is durable/backgrounded, same-Matter mutations are deliberately serialized, typical early Matters are smaller, and the current writer's explicit statements preserve well-tested immutable-conflict and provenance error boundaries. The measurement does justify keeping Issue #12 open before scale-sensitive beta.

The next Issue #12 experiment should compare the same fixture at 10/100/200 documents using `NpgsqlBatch` for the highest-volume independent tables first (`document_versions`, `source_spans`, `assertions`) while measuring transaction duration, allocations, lock time, and conflict diagnostics. Set-based `unnest` should be considered only if batch results remain insufficient. No optimization may weaken RLS, immutable identity checks, supersession guards, or per-row failure attribution.
