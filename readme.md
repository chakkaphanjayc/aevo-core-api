# Aevo Core API

The Core API is the system-of-record boundary for identity, app-scoped sessions, tenant/store scope, authorization, entitlements and privileged audit events. Applications must call this API through versioned contracts; they must not read Cloud SQL or transitional Supabase tables directly.

## Current implementation boundary

This repository contains the .NET 10 host, request-id/error envelope,
liveness/readiness endpoints, app-session gate, Cloud SQL data access and the
first control-plane vertical slice. When configured, the API resolves opaque
app-scoped sessions, platform permissions, application registry/connection
records, immutable audit logs, Aevo Go settings/flags and event aggregates.
It also exposes projection-gated public Place Map/Search/Nearby/Detail reads,
guarded Admin Place registry/detail mutations, and read-only workflow queues;
those paths fail closed until the additive Place projection tables contain
valid rows. A separately guarded Admin maintenance boundary can hard-delete
exact legacy mapping/source-link compatibility rows during approved test or
maintenance windows while retaining canonical Place, revision, and audit history.
The local development launcher passes the migrated development database and
session configuration; `/ready` remains explicit and fails closed if any
required dependency is missing. The API never fabricates user, permission,
connection, metric or audit records.

The versioned Hub route inventory is published by `/api/v1/hub/contract`. Its
`implementationStatus` remains `partial` until the Hub organization, store,
application-access, catalog, onboarding and audit routes are implemented in
this repository. The Edge launcher can therefore replace the legacy Hub
gateway without claiming that domain parity is complete.

Required runtime settings:

- `AEVO_DATABASE_URL`
- `AEVO_IDENTITY_PLATFORM_PROJECT_ID`
- `AEVO_ACCOUNTS_API_ORIGIN`
- `AEVO_ACCOUNTS_SERVICE_SECRET` (shared only with the Accounts Worker)
- `AEVO_CORE_API_SERVICE_SECRET` (shared only with first-party application API servers for session resolution)
- `AEVO_SESSION_SECRET` (Secret Manager only outside local development)
- `AEVO_FEED_CURSOR_SECRET` (a separate Secret Manager value with at least 32 bytes outside local development)
- `AEVO_FEED_EVENT_WORKER_TOKEN` (a separate Secret Manager value for the private Feed event consumer outside local development)
- `AEVO_PLACE_CURSOR_SECRET` (Secret Manager only outside local development;
  may fall back to the session secret when the dedicated secret is omitted)
- `AEVO_ALLOW_LEGACY_HARD_DELETE` (set `true` only for an explicitly approved
  legacy compatibility-row maintenance window outside development/test/local)
- `AEVO_ALLOW_LEGACY_FAVORITE_RECONCILIATION` (set `true` only for an
  explicitly approved Customer favorites reconciliation window outside
  development/test/local; the Core route still requires `system.jobs`, CSRF,
  an idempotency key, and an explicit mapping)
- `AEVO_ALLOW_LEGACY_FAVORITE_HARD_DELETE` (set `true` only when the approved
  reconciliation window also permits exact legacy favorite deletion; it never
  enables broad or unmapped deletion)
- `AEVO_PLACE_PROJECTION_REPLAY_TOKEN` (deployment-owned replay job only; never
  expose it to browsers or public workers)
- `AEVO_ALLOW_PLACE_PROJECTION_REPLAY` (keep `false` outside an explicitly
  approved test or maintenance environment)
- `AEVO_PLACE_REFERENCE_RESOLUTION` (keep `false` during the legacy
  transition; enable only after target mapping/redirect migration and route
  smoke pass to emit DB-resolved canonical references in Feed)
- `AEVO_ENVIRONMENT`
- `AEVO_BUILD_VERSION`

Run locally after installing the .NET 10 SDK:

```sh
dotnet run --project src/Aevo.CoreApi/Aevo.CoreApi.csproj
```

The container listens on port `8080`. Cloud Run receives the database URL and signing/session material from Google Secret Manager; the service never receives a service-account JSON key. In `nonprod`, `staging`, and `production`, `/ready` fails closed unless the dedicated Feed cursor secret and Feed event worker token are present and usable; the cursor signer does not use the local/session fallback in those environments.

## Contract and security rules

- `x-request-id` is bounded and returned on every response.
- In production, `/api/*` routes require the Edge Gateway HMAC headers; the
  signature covers timestamp, method, path/query, the raw body hash, and the
  injected application code. Core rejects a missing app context, and the
  gateway proves the trusted ingress boundary, not the end-user identity.
- Browser callers receive only app-scoped session cookies; Core API does not accept a client-selected organization as authority.
- Accounts is the only service allowed to call `/internal/auth/sessions`; the
  shared service secret is checked before an identity projection or session is
  written.
- Admin sessions use `aevo_admin_session` and `aevo_admin_csrf` by default. The
  `AEVO_SESSION_COOKIE_NAMES`/`AEVO_CSRF_COOKIE_NAMES` maps keep Go, Play and
  POS cookies isolated when those applications are routed through Core.
- Session rows use a rolling idle timeout (`AEVO_SESSION_IDLE_TIMEOUT_SECONDS`)
  bounded by `AEVO_SESSION_ABSOLUTE_TIMEOUT_SECONDS`; refresh rotates the
  opaque token and never extends the absolute expiry.
- Password sign-in and registration default to a browser-session cookie. An
  explicit `rememberMe: true` request stores only that preference in Core and
  uses `AEVO_REMEMBERED_SESSION_IDLE_TIMEOUT_SECONDS` (default 7 days) and
  `AEVO_REMEMBERED_SESSION_ABSOLUTE_TIMEOUT_SECONDS` (default 30 days). The
  persistent cookie is bounded by the absolute expiry (not the rolling idle
  expiry) and remains `HttpOnly`, `SameSite=Lax`, and `Secure` outside local
  development; no session token is written to browser storage. Session rows and
  one-time handoff codes are Core-private and revoked from the Supabase Data
  API roles.
- A Hub-to-app handoff inherits the Hub session's persistence policy. A
  transient Hub session cannot request a persistent target-app session; a
  caller may only keep the same policy or down-scope the target session.
- Session resolution is intentionally database-backed source-of-truth work,
  with request-local de-duplication only. This ensures logout, password
  changes, refresh-token rotation, idle expiry, and administrative revocation
  take effect on the next request.
- Generic session lookup fails closed when more than one app cookie is present;
  app-specific routes read only their own cookie name. Admin logout validates
  CSRF and clears both the opaque session and readable CSRF cookie.
- Accounts may call the private `/internal/auth/sessions/revoke` contract with
  the shared service secret; the application code is part of the revoke query.
- `401` means no valid app session; `403` is reserved for a valid session without the required platform/app permission.
- `503` configuration responses are explicit and contain no secrets.
- State-changing Admin routes always require the session's CSRF hash and an
  `x-csrf-token` header; there is no local bypass.
- `POST /internal/place/projections/replay` is a deployment-owned, token-gated
  maintenance boundary. It accepts only validated compact summaries for
  existing canonical Place rows, requires an active privileged platform actor,
  records a replay run and audit row, and never creates or deletes canonical
  rows. The route is disabled outside development/test/local unless the
  explicit maintenance flag is enabled.
- Database tenant context is set transactionally and must be cleared by transaction completion before connection reuse.

Preview and apply the ordered Core migration set, including the additive Place
projection/workflow migrations, with the migration-only project:

```sh
dotnet run --project src/Aevo.CoreApi.Migrator/Aevo.CoreApi.Migrator.csproj -- --dry-run
dotnet run --project src/Aevo.CoreApi.Migrator/Aevo.CoreApi.Migrator.csproj
dotnet run --project src/Aevo.CoreApi.Migrator/Aevo.CoreApi.Migrator.csproj -- --verify
```

The runner accepts either an Npgsql key/value connection string or a
`postgresql://` URI such as the one used by the local Supabase pooler. The set
is additive and rerunnable; `0006` revokes only sessions that cannot
satisfy the CSRF contract and never deletes session or audit history. API
replicas never apply migrations during startup.

Migration `0030_place_projection_replay_runs.sql` adds the replay fingerprint
and reason columns used to make an approved projection replay retryable by its
run id. It does not select a source/provider or mutate canonical Place rows.
