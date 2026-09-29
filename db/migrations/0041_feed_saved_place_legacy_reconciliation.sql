-- FEED-015: controlled reconciliation boundary for the legacy Customer
-- favorites source. The source remains read-only unless a privileged,
-- environment-gated Core maintenance request explicitly applies the result.

create table if not exists aevo_feed_saved_place_reconciliation_runs (
  run_id uuid primary key default gen_random_uuid(),
  actor_id uuid not null references aevo_identity_users(id) on delete restrict,
  mode text not null,
  customer_id uuid,
  store_id uuid,
  requested_limit integer not null,
  scanned_count integer not null default 0,
  mapped_count integer not null default 0,
  unmapped_count integer not null default 0,
  ambiguous_count integer not null default 0,
  identity_missing_count integer not null default 0,
  not_public_count integer not null default 0,
  already_saved_count integer not null default 0,
  inserted_count integer not null default 0,
  deleted_legacy_count integer not null default 0,
  truncated boolean not null default false,
  status text not null default 'COMPLETED',
  reason text not null,
  request_id text not null,
  idempotency_key text not null,
  request_hash text not null,
  response_body jsonb not null,
  created_at timestamptz not null default now(),
  completed_at timestamptz not null default now(),
  constraint aevo_feed_saved_place_reconciliation_mode_check
    check (mode in ('DRY_RUN', 'MIGRATE', 'MIGRATE_AND_DELETE')),
  constraint aevo_feed_saved_place_reconciliation_limit_check
    check (requested_limit between 1 and 5000),
  constraint aevo_feed_saved_place_reconciliation_counts_check
    check (
      scanned_count >= 0
      and mapped_count >= 0
      and unmapped_count >= 0
      and ambiguous_count >= 0
      and identity_missing_count >= 0
      and not_public_count >= 0
      and already_saved_count >= 0
      and inserted_count >= 0
      and deleted_legacy_count >= 0
    ),
  constraint aevo_feed_saved_place_reconciliation_status_check
    check (status in ('COMPLETED', 'FAILED')),
  constraint aevo_feed_saved_place_reconciliation_reason_check
    check (char_length(trim(reason)) between 1 and 240),
  constraint aevo_feed_saved_place_reconciliation_key_check
    check (char_length(trim(idempotency_key)) between 8 and 200),
  constraint aevo_feed_saved_place_reconciliation_hash_check
    check (request_hash ~ '^[0-9a-f]{64}$')
);

create unique index if not exists aevo_feed_saved_place_reconciliation_actor_key_idx
  on aevo_feed_saved_place_reconciliation_runs (actor_id, idempotency_key);

create index if not exists aevo_feed_saved_place_reconciliation_created_idx
  on aevo_feed_saved_place_reconciliation_runs (created_at desc, run_id);

alter table aevo_feed_saved_place_reconciliation_runs enable row level security;
revoke all on table aevo_feed_saved_place_reconciliation_runs from public, anon, authenticated;

drop policy if exists aevo_feed_saved_place_reconciliation_read_policy
  on aevo_feed_saved_place_reconciliation_runs;
create policy aevo_feed_saved_place_reconciliation_read_policy
  on aevo_feed_saved_place_reconciliation_runs
  for select
  using (
    current_setting('aevo.platform_role', true)
      in ('platform_owner', 'platform_admin', 'platform_ops', 'platform_support')
  );

drop policy if exists aevo_feed_saved_place_reconciliation_write_policy
  on aevo_feed_saved_place_reconciliation_runs;
create policy aevo_feed_saved_place_reconciliation_write_policy
  on aevo_feed_saved_place_reconciliation_runs
  for all
  using (
    current_setting('aevo.platform_role', true)
      in ('platform_owner', 'platform_admin', 'platform_ops')
  )
  with check (
    current_setting('aevo.platform_role', true)
      in ('platform_owner', 'platform_admin', 'platform_ops')
  );

insert into aevo_control_plane_migration_ledger (
  schema_name,
  table_name,
  semantic_domain,
  historical_migration_repos,
  current_owner,
  target_owner,
  runtime_writers,
  readers,
  current_write_mode,
  migration_phase,
  transition_state,
  last_reconciled_at,
  delete_after
)
values (
  'aevo',
  'aevo_feed_saved_place_reconciliation_runs',
  'feed-saved-place-legacy-reconciliation',
  '{aevo-hub}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api}',
  '{aevo-core-api,aevo-admin}',
  'CORE_ONLY',
  'RECONCILE',
  'RECONCILE_REQUIRED',
  now(),
  'After customer_favorites parity, export, rollback, and target-environment cleanup approval'
)
on conflict (schema_name, table_name) do update set
  semantic_domain = excluded.semantic_domain,
  historical_migration_repos = excluded.historical_migration_repos,
  current_owner = excluded.current_owner,
  target_owner = excluded.target_owner,
  runtime_writers = excluded.runtime_writers,
  readers = excluded.readers,
  current_write_mode = excluded.current_write_mode,
  migration_phase = excluded.migration_phase,
  transition_state = excluded.transition_state,
  last_reconciled_at = excluded.last_reconciled_at,
  delete_after = excluded.delete_after,
  updated_at = now();

comment on table aevo_feed_saved_place_reconciliation_runs is
  'Core-owned audit/idempotency ledger for explicit customer_favorites to canonical Feed saved-place reconciliation; it never infers Place identity.';
