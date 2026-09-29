-- FEED-007 Core-owned serving metrics projection.
--
-- Canonical TraceDee/public-discovery tables remain authoritative. This
-- additive read model moves global count and latest-quality work out of the
-- public Feed hot path while retaining canonical eligibility checks there.
-- Runs are versioned so rebuild, dual-read observation, and rollback are
-- explicit. The tables are backend-only and never exposed to browser roles.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_projection_runs (
  run_id uuid primary key default gen_random_uuid(),
  projection_name text not null,
  projection_version text not null,
  source_cutoff_at timestamptz not null,
  status text not null default 'started',
  rows_seen integer not null default 0,
  rows_published integer not null default 0,
  rows_failed integer not null default 0,
  started_at timestamptz not null default now(),
  completed_at timestamptz,
  error_code text,
  error_message text,
  constraint aevo_feed_projection_runs_name_check
    check (projection_name in ('feed-item-metrics')),
  constraint aevo_feed_projection_runs_version_check
    check (projection_version ~ '^feed-item-metrics-v[0-9]+$'),
  constraint aevo_feed_projection_runs_status_check
    check (status in ('started', 'completed', 'failed', 'rolled_back', 'cancelled')),
  constraint aevo_feed_projection_runs_counts_check
    check (rows_seen >= 0 and rows_published >= 0 and rows_failed >= 0)
);

create unique index if not exists aevo_feed_projection_runs_version_idx
  on aevo_feed_projection_runs (projection_name, projection_version, run_id);
create index if not exists aevo_feed_projection_runs_time_idx
  on aevo_feed_projection_runs (projection_name, started_at desc, run_id desc);

create table if not exists aevo_feed_projection_control (
  projection_name text primary key,
  active_run_id uuid references aevo_feed_projection_runs(run_id),
  previous_run_id uuid references aevo_feed_projection_runs(run_id),
  max_age_seconds integer not null default 300,
  last_error_code text,
  last_error_message text,
  updated_at timestamptz not null default now(),
  constraint aevo_feed_projection_control_name_check
    check (projection_name in ('feed-item-metrics')),
  constraint aevo_feed_projection_control_age_check
    check (max_age_seconds between 1 and 86400)
);

create table if not exists aevo_feed_item_metrics_projection (
  run_id uuid not null references aevo_feed_projection_runs(run_id) on delete cascade,
  item_type text not null,
  item_id uuid not null,
  stop_count integer not null default 0,
  save_count bigint not null default 0,
  follow_count bigint not null default 0,
  completion_count bigint not null default 0,
  open_count bigint not null default 0,
  interaction_save_count bigint not null default 0,
  share_count bigint not null default 0,
  quality_score numeric(10,4),
  quality_confidence numeric(10,4),
  source_cutoff_at timestamptz not null,
  generated_at timestamptz not null default now(),
  freshness_state text not null default 'fresh',
  primary key (run_id, item_type, item_id),
  constraint aevo_feed_item_metrics_type_check
    check (item_type in ('TRACE', 'PLACE')),
  constraint aevo_feed_item_metrics_counts_check
    check (
      stop_count >= 0
      and save_count >= 0
      and follow_count >= 0
      and completion_count >= 0
      and open_count >= 0
      and interaction_save_count >= 0
      and share_count >= 0
    ),
  constraint aevo_feed_item_metrics_quality_check
    check (
      (quality_score is null or quality_score between 0 and 1)
      and (quality_confidence is null or quality_confidence between 0 and 1)
    ),
  constraint aevo_feed_item_metrics_freshness_check
    check (freshness_state in ('fresh', 'stale', 'unknown'))
);

create index if not exists aevo_feed_item_metrics_lookup_idx
  on aevo_feed_item_metrics_projection (item_type, item_id, run_id);
create index if not exists aevo_feed_item_metrics_freshness_idx
  on aevo_feed_item_metrics_projection (run_id, freshness_state, generated_at desc);

alter table aevo_feed_projection_runs enable row level security;
alter table aevo_feed_projection_control enable row level security;
alter table aevo_feed_item_metrics_projection enable row level security;

revoke all on table aevo_feed_projection_runs from public;
revoke all on table aevo_feed_projection_control from public;
revoke all on table aevo_feed_item_metrics_projection from public;

insert into aevo_feed_projection_control (projection_name, max_age_seconds)
values ('feed-item-metrics', 300)
on conflict (projection_name) do nothing;
