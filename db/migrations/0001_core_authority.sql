create extension if not exists pgcrypto;

create table if not exists aevo_app_sessions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null,
  app_code text not null,
  organization_id uuid,
  store_id uuid,
  session_hash text not null unique,
  expires_at timestamptz not null,
  revoked_at timestamptz,
  last_seen_at timestamptz,
  created_at timestamptz not null default now(),
  constraint aevo_app_sessions_app_code_check check (app_code in ('HUB', 'ADMIN', 'GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN'))
);

create index if not exists aevo_app_sessions_lookup_idx
  on aevo_app_sessions (session_hash, expires_at)
  where revoked_at is null;

create table if not exists aevo_application_assignments (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null,
  app_code text not null,
  organization_id uuid,
  store_id uuid,
  permissions text[] not null default '{}',
  status text not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_assignments_app_code_check check (app_code in ('HUB', 'ADMIN', 'GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN')),
  constraint aevo_assignments_status_check check (status in ('active', 'disabled'))
);

create unique index if not exists aevo_assignments_unique_scope_idx
  on aevo_application_assignments (user_id, app_code, coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(store_id, '00000000-0000-0000-0000-000000000000'::uuid));

create table if not exists aevo_platform_roles (
  user_id uuid primary key,
  role_code text not null,
  status text not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_platform_roles_status_check check (status in ('active', 'disabled'))
);

create table if not exists aevo_audit_logs (
  id uuid primary key default gen_random_uuid(),
  actor_id uuid not null,
  actor_email text,
  app_code text not null,
  action text not null,
  target_type text not null,
  target_id text not null,
  reason text not null,
  before_state jsonb,
  after_state jsonb,
  request_id text,
  created_at timestamptz not null default now(),
  constraint aevo_audit_app_code_check check (app_code in ('HUB', 'ADMIN', 'GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN'))
);

create index if not exists aevo_audit_logs_app_time_idx
  on aevo_audit_logs (app_code, created_at desc);

create index if not exists aevo_audit_logs_actor_time_idx
  on aevo_audit_logs (actor_id, created_at desc);

create table if not exists aevo_outbox_events (
  id uuid primary key default gen_random_uuid(),
  event_type text not null,
  aggregate_type text not null,
  aggregate_id text not null,
  payload jsonb not null,
  occurred_at timestamptz not null default now(),
  published_at timestamptz,
  attempts integer not null default 0,
  last_error text
);

create index if not exists aevo_outbox_unpublished_idx
  on aevo_outbox_events (occurred_at)
  where published_at is null;

alter table aevo_application_assignments enable row level security;
alter table aevo_audit_logs enable row level security;

create policy aevo_assignments_scope_read_policy on aevo_application_assignments
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin')
    or user_id::text = current_setting('aevo.user_id', true)
  );

create policy aevo_assignments_platform_write_policy on aevo_application_assignments
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

create policy aevo_audit_platform_read_policy on aevo_audit_logs
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

create policy aevo_audit_append_policy on aevo_audit_logs
  for insert
  with check (actor_id::text = current_setting('aevo.user_id', true));
