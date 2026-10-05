# Hub Query Platform Phase 1 handoff

## Shipped Core slice

Core exposes `GET /api/v1/query/models`, `GET /api/v1/query/models/{technicalName}`, and
`POST /api/v1/query/execute`. The v1 model catalog is code-owned and currently registers only
`stores`, backed by `public.stores`. It exposes `code`, `name`, `status`, and `createdAt` with
field-specific operators; the AST validator rejects unknown fields/operators, unsupported versions,
more than 64 nodes, limits outside 1..200, offsets above 10000, and request bodies above 64 KB.

Execution uses bound values and fixed server-owned SQL expressions. Every read carries the resolved
organization predicate, the session's store restriction when present, and active membership store
scope. Core requires the Hub session, active organization membership, `store.read`, and the Core-owned
`query_platform` entitlement before execution. The optional `query_rows` entitlement
caps the per-request result; the hard request limit remains 200. Query runs write a safe metadata-only
`QUERY_EXECUTED` event to the existing Core audit log.

## Export-job gate

The completion plan describes reusing Query Platform tables originating in Hub Supabase history and
requires migration reconciliation before Core rollout. This Core checkout has no query model tables,
export-job table, query usage ledger, or reconciled migration for them. The plan also requires a
confirmed trusted worker and private object storage owner before enabling asynchronous exports. No
such owner or artifact contract is defined in this Core worktree. Returning a durable `QUEUED` job
without that consumer would strand accepted requests, so the export create route fails closed with
`EXPORT_WORKER_NOT_CONFIGURED` and no export status/download route is advertised. The import create
route likewise fails closed with `IMPORT_WORKER_NOT_CONFIGURED`.

Before adding export jobs:

1. Reconcile the existing Hub query schema, row IDs, RLS, and grants into the Core migration ledger.
2. Confirm the worker owner, durable outbox event, private object storage boundary, retry policy, and
   download authorization contract.
3. Add Core-owned create/status contracts with organization-scoped idempotency fingerprints and audit
   events, then test duplicate keys and cross-tenant reads against the configured database roles.

## Entitlement provisioning gate

Current Core billing seeds do not include `query_platform` or `query_rows`. Query endpoints therefore
fail closed until the product entitlement policy provisions an enabled `query_platform` row for an
organization. Decide which plans receive this feature and its row cap, then add those values through
the Core-owned plan entitlement migration path. A missing `query_rows` row uses the endpoint's fixed
maximum of 200 rows per request.
