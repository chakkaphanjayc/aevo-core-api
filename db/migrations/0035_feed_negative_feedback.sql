-- DISCOVERY-004: Core-owned durable negative feedback for authenticated Go Feed users.
-- Anonymous discovery remains request/session-local; this table never stores an
-- anonymous cookie key or a browser-supplied actor. Core resolves actor/session
-- identity and writes through the server-only data boundary.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_negative_feedback (
  feedback_id uuid primary key default gen_random_uuid(),
  actor_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null default 'GO',
  item_type text not null,
  item_id text not null,
  feed_session_id uuid not null,
  item_token_hash text not null,
  action text not null default 'HIDE',
  reason_code text,
  active boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_feed_negative_feedback_app_check
    check (app_code = 'GO'),
  constraint aevo_feed_negative_feedback_item_type_check
    check (item_type in ('TRACE', 'PLACE')),
  constraint aevo_feed_negative_feedback_item_id_check
    check (char_length(trim(item_id)) between 1 and 128),
  constraint aevo_feed_negative_feedback_token_hash_check
    check (item_token_hash ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_negative_feedback_action_check
    check (action = 'HIDE'),
  constraint aevo_feed_negative_feedback_reason_check
    check (reason_code is null or char_length(trim(reason_code)) between 1 and 64),
  unique (actor_id, app_code, item_type, item_id)
);

create index if not exists aevo_feed_negative_feedback_active_lookup_idx
  on aevo_feed_negative_feedback (actor_id, app_code, active, item_type, item_id)
  where active = true;

create table if not exists aevo_feed_negative_feedback_idempotency (
  actor_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null default 'GO',
  idempotency_key text not null,
  request_hash text not null,
  response_body jsonb not null,
  created_at timestamptz not null default now(),
  primary key (actor_id, app_code, idempotency_key),
  constraint aevo_feed_negative_feedback_idempotency_app_check
    check (app_code = 'GO'),
  constraint aevo_feed_negative_feedback_idempotency_key_check
    check (char_length(trim(idempotency_key)) between 8 and 200),
  constraint aevo_feed_negative_feedback_idempotency_hash_check
    check (request_hash ~ '^[0-9a-f]{64}$')
);

alter table aevo_feed_negative_feedback enable row level security;
alter table aevo_feed_negative_feedback_idempotency enable row level security;

drop policy if exists aevo_feed_negative_feedback_owner_read_policy on aevo_feed_negative_feedback;
create policy aevo_feed_negative_feedback_owner_read_policy
  on aevo_feed_negative_feedback
  for select
  using (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

drop policy if exists aevo_feed_negative_feedback_owner_insert_policy on aevo_feed_negative_feedback;
create policy aevo_feed_negative_feedback_owner_insert_policy
  on aevo_feed_negative_feedback
  for insert
  with check (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

drop policy if exists aevo_feed_negative_feedback_owner_update_policy on aevo_feed_negative_feedback;
create policy aevo_feed_negative_feedback_owner_update_policy
  on aevo_feed_negative_feedback
  for update
  using (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  )
  with check (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

revoke all on table aevo_feed_negative_feedback from public, anon, authenticated;
revoke all on table aevo_feed_negative_feedback_idempotency from public, anon, authenticated;

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
values
  (
    'aevo',
    'aevo_feed_negative_feedback',
    'feed-negative-feedback',
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
  ),
  (
    'aevo',
    'aevo_feed_negative_feedback_idempotency',
    'feed-negative-feedback-idempotency',
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

comment on table aevo_feed_negative_feedback is
  'Core-owned authenticated Go Feed hide state. Anonymous feedback is not persisted.';
comment on table aevo_feed_negative_feedback_idempotency is
  'Server-only idempotency ledger for authenticated Go Feed negative feedback mutations.';
