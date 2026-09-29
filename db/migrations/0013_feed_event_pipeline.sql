-- FEED-006 Core-owned event intake, durable outbox/inbox, and bounded stats.
--
-- Feed events are accepted by Core after server-side session/item-token
-- validation. The tables are intentionally server-only: browser roles do not
-- receive grants or policies, and the public API never exposes these rows.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_event_intake (
  principal_binding text not null,
  event_id text not null,
  app_code text not null default 'GO',
  actor_id uuid,
  schema_version text not null,
  event_name text not null,
  feed_session_id text not null,
  item_token text not null,
  item_type text not null,
  item_id text not null,
  item_position integer,
  source text,
  config_version text not null,
  ranking_version text not null,
  experiment_variant text,
  metadata jsonb not null default '{}'::jsonb,
  event_hash text not null,
  occurred_at timestamptz not null,
  received_at timestamptz not null default now(),
  status text not null default 'ACCEPTED',
  constraint aevo_feed_event_intake_pk primary key (principal_binding, event_id),
  constraint aevo_feed_event_intake_binding_check check (principal_binding ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_event_intake_event_id_check check (char_length(event_id) between 1 and 128),
  constraint aevo_feed_event_intake_app_check check (app_code = 'GO'),
  constraint aevo_feed_event_intake_schema_check check (schema_version = '1'),
  constraint aevo_feed_event_intake_event_name_check check (event_name in (
    'impression', 'engaged_view', 'open', 'click', 'save', 'share',
    'trace_start', 'trace_complete', 'place_open', 'booking_click'
  )),
  constraint aevo_feed_event_intake_type_check check (item_type in ('TRACE', 'PLACE')),
  constraint aevo_feed_event_intake_item_id_check check (char_length(item_id) between 1 and 128),
  constraint aevo_feed_event_intake_position_check check (item_position is null or item_position between 0 and 10000),
  constraint aevo_feed_event_intake_metadata_check check (jsonb_typeof(metadata) = 'object'),
  constraint aevo_feed_event_intake_hash_check check (event_hash ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_event_intake_status_check check (status in ('ACCEPTED', 'SAMPLED_OUT'))
);

create index if not exists aevo_feed_event_intake_time_idx
  on aevo_feed_event_intake (occurred_at desc, event_id);
create index if not exists aevo_feed_event_intake_name_time_idx
  on aevo_feed_event_intake (event_name, occurred_at desc);
create index if not exists aevo_feed_event_intake_principal_time_idx
  on aevo_feed_event_intake (principal_binding, occurred_at desc);

create table if not exists aevo_feed_event_outbox (
  outbox_id uuid primary key default gen_random_uuid(),
  principal_binding text not null,
  event_id text not null,
  status text not null default 'PENDING',
  attempts integer not null default 0,
  available_at timestamptz not null default now(),
  locked_until timestamptz,
  last_error text,
  processed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (principal_binding, event_id),
  foreign key (principal_binding, event_id)
    references aevo_feed_event_intake (principal_binding, event_id)
    on delete cascade,
  constraint aevo_feed_event_outbox_status_check check (status in ('PENDING', 'PROCESSING', 'PROCESSED', 'FAILED', 'DEAD_LETTER')),
  constraint aevo_feed_event_outbox_attempts_check check (attempts >= 0)
);

create index if not exists aevo_feed_event_outbox_pending_idx
  on aevo_feed_event_outbox (status, available_at, created_at)
  where status in ('PENDING', 'FAILED');
create index if not exists aevo_feed_event_outbox_locked_idx
  on aevo_feed_event_outbox (status, locked_until)
  where status = 'PROCESSING';

create table if not exists aevo_feed_event_inbox (
  consumer_name text not null,
  principal_binding text not null,
  event_id text not null,
  status text not null default 'PROCESSED',
  processed_at timestamptz not null default now(),
  last_error text,
  primary key (consumer_name, principal_binding, event_id),
  constraint aevo_feed_event_inbox_status_check check (status in ('PROCESSED', 'FAILED'))
);

create index if not exists aevo_feed_event_inbox_processed_idx
  on aevo_feed_event_inbox (processed_at desc);

create table if not exists aevo_feed_event_stats_daily (
  bucket_date date not null,
  item_type text not null,
  item_id text not null,
  event_name text not null,
  event_count bigint not null default 0,
  last_occurred_at timestamptz not null,
  updated_at timestamptz not null default now(),
  primary key (bucket_date, item_type, item_id, event_name),
  constraint aevo_feed_event_stats_type_check check (item_type in ('TRACE', 'PLACE')),
  constraint aevo_feed_event_stats_item_id_check check (char_length(item_id) between 1 and 128),
  constraint aevo_feed_event_stats_name_check check (event_name in (
    'impression', 'engaged_view', 'open', 'click', 'save', 'share',
    'trace_start', 'trace_complete', 'place_open', 'booking_click'
  )),
  constraint aevo_feed_event_stats_count_check check (event_count >= 0)
);

create index if not exists aevo_feed_event_stats_item_idx
  on aevo_feed_event_stats_daily (item_type, item_id, bucket_date desc);

alter table aevo_feed_event_intake enable row level security;
alter table aevo_feed_event_outbox enable row level security;
alter table aevo_feed_event_inbox enable row level security;
alter table aevo_feed_event_stats_daily enable row level security;

revoke all on table aevo_feed_event_intake from public;
revoke all on table aevo_feed_event_outbox from public;
revoke all on table aevo_feed_event_inbox from public;
revoke all on table aevo_feed_event_stats_daily from public;
