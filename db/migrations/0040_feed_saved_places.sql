-- DISCOVERY-007 controlled development slice: Core-owned authenticated Go
-- Feed save state for canonical Places. This is an interaction projection,
-- not the Customer favorites source of truth and it does not rewrite legacy
-- store-favorite rows.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_saved_places (
  actor_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null default 'GO',
  place_id uuid not null references aevo_place_registry(place_id) on delete cascade,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (actor_id, app_code, place_id),
  constraint aevo_feed_saved_places_app_check
    check (app_code = 'GO')
);

create index if not exists aevo_feed_saved_places_actor_lookup_idx
  on aevo_feed_saved_places (actor_id, app_code, created_at desc, place_id);

create table if not exists aevo_feed_saved_places_idempotency (
  actor_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null default 'GO',
  idempotency_key text not null,
  request_hash text not null,
  response_body jsonb not null,
  created_at timestamptz not null default now(),
  primary key (actor_id, app_code, idempotency_key),
  constraint aevo_feed_saved_places_idempotency_app_check
    check (app_code = 'GO'),
  constraint aevo_feed_saved_places_idempotency_key_check
    check (char_length(trim(idempotency_key)) between 8 and 200),
  constraint aevo_feed_saved_places_idempotency_hash_check
    check (request_hash ~ '^[0-9a-f]{64}$')
);

alter table aevo_feed_saved_places enable row level security;
alter table aevo_feed_saved_places_idempotency enable row level security;

drop policy if exists aevo_feed_saved_places_owner_read_policy on aevo_feed_saved_places;
create policy aevo_feed_saved_places_owner_read_policy
  on aevo_feed_saved_places
  for select
  using (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

drop policy if exists aevo_feed_saved_places_owner_insert_policy on aevo_feed_saved_places;
create policy aevo_feed_saved_places_owner_insert_policy
  on aevo_feed_saved_places
  for insert
  with check (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

drop policy if exists aevo_feed_saved_places_owner_delete_policy on aevo_feed_saved_places;
create policy aevo_feed_saved_places_owner_delete_policy
  on aevo_feed_saved_places
  for delete
  using (
    current_setting('aevo.app_code', true) = 'GO'
    and actor_id::text = nullif(current_setting('aevo.user_id', true), '')
  );

revoke all on table aevo_feed_saved_places from public, anon, authenticated;
revoke all on table aevo_feed_saved_places_idempotency from public, anon, authenticated;

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
    'aevo_feed_saved_places',
    'feed-saved-places',
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
    'aevo_feed_saved_places_idempotency',
    'feed-saved-places-idempotency',
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

comment on table aevo_feed_saved_places is
  'Core-owned authenticated Go Feed save projection keyed by canonical Place ID; not the Customer favorites source of truth.';
comment on table aevo_feed_saved_places_idempotency is
  'Server-only idempotency ledger for authenticated Go Feed canonical Place save mutations.';
