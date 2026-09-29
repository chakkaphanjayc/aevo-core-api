# Core API migrations

`aevo-core-api` is the only application repository that owns the target
database migration history. The migration job uses the files in this
directory, takes PostgreSQL advisory lock `748322902`, records SHA-256
checksums in `_aevo_core_migrations`, and fails closed on drift or missing
history.

Local commands:

```sh
dotnet run --project src/Aevo.CoreApi.Migrator -- --dry-run
dotnet run --project src/Aevo.CoreApi.Migrator -- --verify
dotnet run --project src/Aevo.CoreApi.Migrator
dotnet run --project src/Aevo.CoreApi.Migrator -- --rls-check
dotnet run --project src/Aevo.CoreApi.Migrator -- --legacy-removal-check
```

The production command runs as the migration-only Cloud Run Job identity from
`aevo-infrastructure`. API replicas do not apply migrations at startup.

`AEVO_DATABASE_URL` may be an Npgsql key/value connection string or a
`postgresql://` URI. The local Hub wrapper reads the development `DATABASE_URL`
and passes it to this owner without writing another secret-bearing env file.

Migration `0013_control_plane_migration_authority.sql` adds the HUB-005
authority ledger. It records table ownership, current write mode, transition
phase, known writers/readers, and the evidence required before compatibility
tables can be removed. It is metadata only; it does not copy or delete rows.
Privileged operators can inspect it through
`GET /api/v1/admin/migrations/control-plane` with `system.jobs` permission.

The historical Supabase migration sets under the application repositories are
not part of the Core API migration history and must not be pushed to the target
database. Core migrations `0022` through `0027` complete the control-plane
cutover, install the Core-owned workspace template primitives, and remove the
retired Hub compatibility relations, public admin/billing helpers, and local
application-session tables. Migration `0028` adds the RLS-protected typed store
application configuration table and `0029` makes Core template instantiation
copy validated configuration snapshots without copying secrets.

Migration `0014_application_assignment_projection.sql` and
`0015_application_assignment_store_scope_projection.sql` backfill the Core
assignment projection and store-scoped grants. Migration
`0016_application_assignment_reconciliation_marker.sql` records the
development reconciliation checkpoint. Migration `0024` retires the public
assignment, store-binding, subscription, entitlement, billing, catalog, and
authorization-code compatibility relations after active readers moved to Core.

Migration `0017_application_manifest.sql` adds declarative application
metadata to the Core registry. Capabilities and configuration-schema
references are descriptive only; they never grant access. Environment-specific
origins remain in `aevo_application_connections`.

Migration `0018_store_application_binding_projection.sql` adds the Core-owned
`aevo_store_application_bindings` projection and backfills it from the
historical `public.store_application_access` table. Migration `0024` removes
that compatibility table after POS/Play reader and writer cutover. The
migration initializer creates disabled Core rows for stores created by a
transitional store function. `aevo-digital-sing` is excluded.

`--rls-check` is a read/rollback verification command. When the configured
database role bypasses RLS, it selects an existing non-bypass role, grants
only missing probe privileges, runs transaction-local tenant-context and
rollback checks, then revokes only those temporary grants. It never creates
or leaves a probe role behind.

Migration `0020_installation_provisioning_projection.sql` adds the Core-owned
organization installation projection and durable provisioning-operation ledger.
It backfills the historical `public.app_subscriptions` rows; application
installation status does not grant authorization or entitlement.

Migration `0021_billing_entitlement_projection.sql` adds the Core billing event
ledger, organization subscription projection, and organization entitlement
projection. Migration `0024` removes the historical public billing and
entitlement relations after the Core projections became authoritative.

Migration `0025_remove_local_app_session_compatibility.sql` removes the final
application-owned `public.app_sessions` and `public.application_registry`
tables. Migration `0026_retire_public_hub_admin_compatibility.sql` removes the
unused public platform-admin, plan, usage, settings, and query-view helpers;
`0027_retire_hub_compatibility_functions.sql` removes the remaining public Hub
helper RPCs. Accounts issues sessions and Core stores/resolves them for every
app; there is no local application session authority remaining.

Migration `0034_feed_moderation_adapter_boundary.sql` is the Core-owned
server-to-server moderation adapter for the transitional TraceDee/Supabase
moderation domain. It adds an expected-status/version check before delegating
to the existing atomic moderation RPC, preserves exact idempotent retries, and
does not create a media table, expose a public asset URL, or delete content.
The connected development check covers review, replay, and stale-version
conflict behavior; production-origin, media-variant, appeal, and cache
invalidation evidence remain separate gates.

Migration `0035_feed_negative_feedback.sql` is the Core-owned authenticated Go
Feed hide/unhide state and server-only idempotency ledger. It stores only the signed
item-token hash plus the app/session binding, protects the state with RLS, and
lets canonical TRACE/PLACE candidate queries suppress active feedback. It does
not persist anonymous cookie identity, expose a browser database path, or
delete canonical content. The connected development check covers exact replay,
idempotency conflict, signed token/session binding, candidate suppression, and
the Core/Go contract path.

Migration `0036_feed_negative_feedback_history.sql` adds the private,
append-only authenticated hide/unhide transition history. Core writes it in the
same transaction as the current suppression row and idempotency record, keeping
allowlisted reasons without storing raw item tokens or anonymous identity. It
has owner-scoped RLS and is not part of the public Feed response; user deletion
cascades through the Core identity reference. The connected development check
covers one retained hide reason plus the later unhide transition and scoped
cleanup.

Migration `0037_feed_negative_feedback_history_read_index.sql` adds the
Core-owned actor/time index used by the authenticated private history read
route. It is additive and contains no new data or public grant.

`--legacy-removal-check` fails if any retired relation or compatibility
function remains. It does not modify data.
