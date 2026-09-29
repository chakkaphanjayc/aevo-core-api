-- Core-owned Feed configuration control plane.
--
-- This migration deliberately stores only validated runtime configuration. It
-- does not create a second Supabase authority, expose infrastructure settings,
-- or persist per-user experiment assignments.

create extension if not exists pgcrypto;

create table if not exists aevo_feed_config_revisions (
  revision_id uuid primary key default gen_random_uuid(),
  version bigint generated always as identity unique,
  schema_version text not null,
  status text not null default 'DRAFT',
  config jsonb not null,
  content_hash text not null,
  source_revision_id uuid,
  author_id uuid,
  author_app_code text not null default 'ADMIN',
  reason text not null,
  created_at timestamptz not null default now(),
  constraint aevo_feed_config_revisions_status_check
    check (status in ('DRAFT', 'ACTIVE')),
  constraint aevo_feed_config_revisions_schema_check
    check (schema_version = '1'),
  constraint aevo_feed_config_revisions_hash_check
    check (content_hash ~ '^[0-9a-f]{64}$'),
  constraint aevo_feed_config_revisions_app_check
    check (author_app_code = 'ADMIN'),
  constraint aevo_feed_config_revisions_reason_check
    check (char_length(reason) between 3 and 500)
);

create index if not exists aevo_feed_config_revisions_created_idx
  on aevo_feed_config_revisions (created_at desc, version desc);

create table if not exists aevo_feed_config_validation_results (
  validation_id uuid primary key default gen_random_uuid(),
  revision_id uuid not null references aevo_feed_config_revisions(revision_id) on delete cascade,
  validator_version text not null,
  valid boolean not null,
  errors jsonb not null default '[]'::jsonb,
  warnings jsonb not null default '[]'::jsonb,
  validated_by uuid,
  validated_at timestamptz not null default now(),
  unique (revision_id, validator_version),
  constraint aevo_feed_config_validation_version_check
    check (char_length(validator_version) between 1 and 64)
);

create index if not exists aevo_feed_config_validation_revision_idx
  on aevo_feed_config_validation_results (revision_id, valid, validated_at desc);

create table if not exists aevo_feed_config_publications (
  scope_key text primary key,
  active_revision_id uuid not null references aevo_feed_config_revisions(revision_id),
  active_version bigint not null,
  pointer_version bigint not null default 1,
  previous_revision_id uuid references aevo_feed_config_revisions(revision_id),
  updated_by uuid,
  updated_at timestamptz not null default now(),
  propagation_status text not null default 'PENDING',
  last_propagated_version bigint,
  last_propagated_at timestamptz,
  last_error_code text,
  constraint aevo_feed_config_publications_scope_check
    check (scope_key = 'GO_PUBLIC_FEED'),
  constraint aevo_feed_config_publications_status_check
    check (propagation_status in ('PENDING', 'HEALTHY', 'DEGRADED', 'FALLBACK', 'NOT_REPORTED')),
  constraint aevo_feed_config_publications_pointer_check
    check (pointer_version > 0)
);

create table if not exists aevo_feed_config_audit_events (
  event_id uuid primary key default gen_random_uuid(),
  actor_id uuid not null,
  app_code text not null default 'ADMIN',
  action text not null,
  revision_id uuid references aevo_feed_config_revisions(revision_id),
  before_version bigint,
  after_version bigint,
  reason text not null,
  request_id text not null,
  idempotency_key text not null,
  diff_metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  unique (app_code, idempotency_key),
  constraint aevo_feed_config_audit_app_check
    check (app_code = 'ADMIN'),
  constraint aevo_feed_config_audit_action_check
    check (action in ('DRAFT_CREATED', 'VALIDATED', 'PUBLISHED', 'ROLLED_BACK', 'MISMATCH_DETECTED', 'FALLBACK_USED')),
  constraint aevo_feed_config_audit_reason_check
    check (char_length(reason) between 3 and 500)
);

create index if not exists aevo_feed_config_audit_created_idx
  on aevo_feed_config_audit_events (created_at desc, event_id desc);

create index if not exists aevo_feed_config_audit_revision_idx
  on aevo_feed_config_audit_events (revision_id, created_at desc)
  where revision_id is not null;

create table if not exists aevo_feed_config_propagations (
  runtime_name text primary key,
  observed_revision_id uuid references aevo_feed_config_revisions(revision_id),
  observed_version bigint,
  status text not null default 'NOT_REPORTED',
  last_checked_at timestamptz,
  latency_ms integer,
  last_error_code text,
  metadata jsonb not null default '{}'::jsonb,
  constraint aevo_feed_config_propagations_status_check
    check (status in ('HEALTHY', 'DEGRADED', 'FALLBACK', 'PENDING', 'NOT_REPORTED')),
  constraint aevo_feed_config_propagations_latency_check
    check (latency_ms is null or latency_ms >= 0)
);

create or replace function aevo_feed_config_revisions_immutable()
returns trigger
language plpgsql
as $$
begin
  raise exception 'Feed config revisions are immutable';
end;
$$;

do $$
begin
  if not exists (
    select 1
    from pg_trigger
    where tgname = 'aevo_feed_config_revisions_immutable_trigger'
      and tgrelid = 'aevo_feed_config_revisions'::regclass
  ) then
    create trigger aevo_feed_config_revisions_immutable_trigger
      before update or delete on aevo_feed_config_revisions
      for each row execute function aevo_feed_config_revisions_immutable();
  end if;
end;
$$;

-- The deterministic baseline is the code-defined last resort and the initial
-- active revision. The application validator uses the same shape and bounds.
insert into aevo_feed_config_revisions
  (revision_id, schema_version, status, config, content_hash, author_id, author_app_code, reason)
select
  '00000000-0000-0000-0000-000000000001'::uuid,
  '1',
  'ACTIVE',
  '{"schemaVersion":"1","enabled":true,"killSwitch":false,"candidateSources":{"trace":{"enabled":true,"budget":100,"minimum":1},"place":{"enabled":true,"budget":100,"minimum":1}},"ranking":{"mode":"DETERMINISTIC","version":"deterministic-v1","freshnessWindowHours":168,"weights":{"quality":0.4,"freshness":0.2,"proximity":0.2,"taste":0.2}},"diversity":{"maxConsecutiveSameSource":2,"maxSourceRatio":0.75,"explorationQuota":0.1},"geo":{"maxCoarseRadiusMeters":50000},"rollout":{"percent":100,"experimentId":null,"salt":"feed-v1","variants":[]},"budgets":{"pageSize":24,"cacheTtlSeconds":30},"safety":{"guardrailsEnabled":true,"requireModerationProjection":true,"maxEligibilityAgeSeconds":60},"analytics":{"enabled":true,"samplePercent":100,"retentionDays":90}}'::jsonb,
  '980c4fa41dbc86a0dcd70d59c4196d81f93d739787f8e66a16de6eb681d9ce02',
  null,
  'ADMIN',
  'Deterministic Feed baseline';

insert into aevo_feed_config_validation_results
  (revision_id, validator_version, valid, errors, warnings, validated_at)
select
  revision_id,
  'feed-config-v1',
  true,
  '[]'::jsonb,
  '[]'::jsonb,
  now()
from aevo_feed_config_revisions
where revision_id = '00000000-0000-0000-0000-000000000001'::uuid
on conflict (revision_id, validator_version) do nothing;

insert into aevo_feed_config_publications
  (scope_key, active_revision_id, active_version, pointer_version, propagation_status, last_propagated_version, last_propagated_at)
select
  'GO_PUBLIC_FEED',
  revision_id,
  version,
  1,
  'HEALTHY',
  version,
  now()
from aevo_feed_config_revisions
where revision_id = '00000000-0000-0000-0000-000000000001'::uuid
on conflict (scope_key) do nothing;

insert into aevo_feed_config_propagations
  (runtime_name, observed_revision_id, observed_version, status, last_checked_at)
select
  'core-api',
  active_revision_id,
  active_version,
  'HEALTHY',
  now()
from aevo_feed_config_publications
where scope_key = 'GO_PUBLIC_FEED'
on conflict (runtime_name) do nothing;

alter table aevo_feed_config_revisions enable row level security;
alter table aevo_feed_config_validation_results enable row level security;
alter table aevo_feed_config_publications enable row level security;
alter table aevo_feed_config_audit_events enable row level security;
alter table aevo_feed_config_propagations enable row level security;

do $$
begin
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_revisions_platform_read_policy') then
    create policy aevo_feed_config_revisions_platform_read_policy on aevo_feed_config_revisions
      for select
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_revisions_platform_write_policy') then
    create policy aevo_feed_config_revisions_platform_write_policy on aevo_feed_config_revisions
      for insert
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_validation_platform_read_policy') then
    create policy aevo_feed_config_validation_platform_read_policy on aevo_feed_config_validation_results
      for select
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_validation_platform_insert_policy') then
    create policy aevo_feed_config_validation_platform_insert_policy on aevo_feed_config_validation_results
      for insert
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_validation_platform_update_policy') then
    create policy aevo_feed_config_validation_platform_update_policy on aevo_feed_config_validation_results
      for update
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_publications_platform_read_policy') then
    create policy aevo_feed_config_publications_platform_read_policy on aevo_feed_config_publications
      for select
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_publications_platform_write_policy') then
    create policy aevo_feed_config_publications_platform_write_policy on aevo_feed_config_publications
      for update
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_audit_platform_read_policy') then
    create policy aevo_feed_config_audit_platform_read_policy on aevo_feed_config_audit_events
      for select
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_audit_platform_write_policy') then
    create policy aevo_feed_config_audit_platform_write_policy on aevo_feed_config_audit_events
      for insert
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_propagations_platform_read_policy') then
    create policy aevo_feed_config_propagations_platform_read_policy on aevo_feed_config_propagations
      for select
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_propagations_platform_insert_policy') then
    create policy aevo_feed_config_propagations_platform_insert_policy on aevo_feed_config_propagations
      for insert
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
  if not exists (select 1 from pg_policies where policyname = 'aevo_feed_config_propagations_platform_update_policy') then
    create policy aevo_feed_config_propagations_platform_update_policy on aevo_feed_config_propagations
      for update
      using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
      with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));
  end if;
end;
$$;

comment on table aevo_feed_config_revisions is
  'Immutable Core-owned Feed runtime configuration revisions; validated drafts are published through the active pointer.';
comment on table aevo_feed_config_publications is
  'Single active Feed configuration pointer with optimistic pointer version and propagation health.';
comment on table aevo_feed_config_audit_events is
  'Structured, idempotent Feed configuration lifecycle audit events. No per-user experiment assignments are stored.';
