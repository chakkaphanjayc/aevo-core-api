-- DISCOVERY-006A: append-only authenticated Go Feed hide/unhide history.
-- The current-state row remains the serving suppression boundary; this table
-- preserves the allowlisted reason and transition audit without exposing it
-- through the public Feed response.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_negative_feedback_history (
  history_id uuid primary key default gen_random_uuid(),
  actor_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null default 'GO',
  item_type text not null,
  item_id text not null,
  feed_session_id uuid not null,
  item_token_hash text not null,
  action text not null default 'HIDE',
  reason_code text,
  active boolean not null,
  idempotency_key text not null,
  request_hash text not null,
  request_id text not null,
  created_at timestamptz not null default now(),
  constraint aevo_feed_negative_feedback_history_app_check
    check (app_code = 'GO'),
  constraint aevo_feed_negative_feedback_history_item_type_check
    check (item_type in ('TRACE', 'PLACE')),
  constraint aevo_feed_negative_feedback_history_item_id_check
    check (char_length(trim(item_id)) between 1 and 128),
  constraint aevo_feed_negative_feedback_history_token_hash_check
    check (item_token_hash ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_negative_feedback_history_action_check
    check (action = 'HIDE'),
  constraint aevo_feed_negative_feedback_history_reason_check
    check (reason_code is null or char_length(trim(reason_code)) between 1 and 64),
  constraint aevo_feed_negative_feedback_history_idempotency_check
    check (char_length(trim(idempotency_key)) between 8 and 200),
  constraint aevo_feed_negative_feedback_history_request_hash_check
    check (request_hash ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_negative_feedback_history_request_id_check
    check (char_length(trim(request_id)) between 1 and 128),
  unique (actor_id, app_code, idempotency_key)
);

create index if not exists aevo_feed_negative_feedback_history_item_idx
  on aevo_feed_negative_feedback_history (actor_id, app_code, item_type, item_id, created_at desc);

alter table aevo_feed_negative_feedback_history enable row level security;

drop policy if exists aevo_feed_negative_feedback_history_owner_read_policy on aevo_feed_negative_feedback_history;
create policy aevo_feed_negative_feedback_history_owner_read_policy
  on aevo_feed_negative_feedback_history
  for select
  using (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

drop policy if exists aevo_feed_negative_feedback_history_owner_insert_policy on aevo_feed_negative_feedback_history;
create policy aevo_feed_negative_feedback_history_owner_insert_policy
  on aevo_feed_negative_feedback_history
  for insert
  with check (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

revoke all on table aevo_feed_negative_feedback_history from public, anon, authenticated;

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
  'aevo_feed_negative_feedback_history',
  'feed-negative-feedback-history',
  '{}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api}',
  '{aevo-core-api}',
  'CORE_ONLY',
  'CANONICAL',
  'CANONICAL',
  now(),
  null
)
on conflict (schema_name, table_name) do update set
  semantic_domain = excluded.semantic_domain,
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

comment on table aevo_feed_negative_feedback_history is
  'Core-owned append-only authenticated Go Feed hide/unhide history. Reasons remain allowlisted and private.';
