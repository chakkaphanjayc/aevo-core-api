# Core API PostgreSQL baseline

The SQL in this directory targets Cloud SQL for PostgreSQL. It is not a Supabase migration and must not be pushed through the Supabase CLI.

Every request that touches tenant-owned data must execute inside a transaction with server-resolved values such as:

```sql
select set_config('aevo.user_id', $1, true);
select set_config('aevo.organization_id', $2, true);
select set_config('aevo.store_id', $3, true);
```

The `true` transaction-local flag is mandatory. Connection-pool tests must prove that a subsequent request cannot observe a previous request's tenant context.

Migration `0002_admin_control_plane.sql` adds the application registry and
connection records, Aevo Go settings and rollout flags, and the append-only
product event stream used for Admin dashboards. Registry/config rows are
seeded as defaults; runtime connection status is only written by a real probe.

Migration `0003_runtime_identity_support.sql` adds the server-side identity
projection and additive CSRF hash field used when Accounts creates an opaque
app-scoped session. It does not expose identity-provider tokens to clients.

Migration `0004_application_registry_reconciliation.sql` is safe to run after
`0002` and adds the `DIGITAL_SIGN` registry code when an older control-plane
baseline did not include it.

Migration `0005_session_lifecycle.sql` adds the non-null idle and absolute
session-expiry columns. Apply it before enabling the Core API protected routes;
existing session rows are backfilled from `expires_at` and therefore do not
gain extra lifetime during the migration.

Migration `0006_auth_session_contract.sql` finishes the session contract. It
revokes rows that predate CSRF persistence, makes the CSRF hash mandatory,
adds the application foreign key and creates the session activity index. It is
idempotent and safe to apply on every deployment; it never deletes session
history.

Migration `0009_hub_onboarding_authority.sql` moves the shared Hub onboarding
session schema into the Core API migration ledger. The legacy Hub Supabase
migration remains only as a read-only compatibility reference during the data
transition; it must not be applied by the Hub launcher.

Migration `0011_place_projection.sql` adds the additive canonical Place
registry, source/legacy mappings, immutable revisions, field provenance, and
public projection state. It does not rewrite Store or TraceDee rows and does
not rewrite Store or TraceDee rows. The public read handlers are projection-
gated until the migration and projection rows exist; MAP-002 replay/projection
checks must pass before client route cutover.

Migration `0012_place_workflows.sql` adds additive relationship, redirect,
claim, private evidence, community submission, and immutable workflow-event
tables for MAP-006 through MAP-010. It does not approve claims, apply
submissions, or rewrite losing Place identities by itself.

Migration `0030_place_projection_replay_runs.sql` adds only operational
fingerprint/reason metadata to the existing projection-run ledger. The guarded
replay route uses it for retry identity; it does not select an external source,
create canonical Place rows, or delete source/legacy rows.

The ordered migration set is the only authority for the Core API Cloud SQL
schema. Run the migrations before enabling a new protected route, verify the
schema, then deploy the route. Legacy session readers may remain enabled only
for the explicitly documented migration window; they are not part of the
steady-state contract.
