-- Platform control-plane data used by Aevo Admin and the Aevo Go dashboard.
-- Runtime health is intentionally not seeded: a registry row is not proof that
-- an application is reachable.

create table if not exists aevo_application_registry (
  code text primary key,
  name text not null,
  kind text not null,
  status text not null default 'ACTIVE',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_application_registry_code_check check (code in ('HUB', 'ADMIN', 'GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN')),
  constraint aevo_application_registry_kind_check check (kind in ('CONTROL_PLANE', 'PLATFORM_ADMIN', 'OPERATIONS', 'CONSUMER')),
  constraint aevo_application_registry_status_check check (status in ('ACTIVE', 'DISABLED'))
);

insert into aevo_application_registry (code, name, kind)
values
  ('HUB', 'Aevo Hub', 'CONTROL_PLANE'),
  ('ADMIN', 'Aevo Admin', 'PLATFORM_ADMIN'),
  ('GO', 'Aevo Go', 'CONSUMER'),
  ('PLAY', 'Aevo Play', 'CONSUMER'),
  ('POS', 'Aevo POS', 'OPERATIONS'),
  ('KIOSK', 'Aevo Kiosk', 'OPERATIONS'),
  ('QUEUE', 'Aevo Queue', 'OPERATIONS'),
  ('DIGITAL_SIGN', 'Aevo Digital Sign', 'OPERATIONS')
on conflict (code) do nothing;

create table if not exists aevo_application_connections (
  id uuid primary key default gen_random_uuid(),
  app_code text not null references aevo_application_registry(code),
  environment text not null,
  status text not null default 'not_configured',
  base_url text,
  health_path text not null default '/health',
  checked_at timestamptz,
  latency_ms integer,
  last_error_code text,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (app_code, environment),
  constraint aevo_application_connections_status_check check (status in ('connected', 'degraded', 'not_connected', 'not_configured')),
  constraint aevo_application_connections_latency_check check (latency_ms is null or latency_ms >= 0)
);

create index if not exists aevo_application_connections_status_idx
  on aevo_application_connections (environment, status, app_code);

create table if not exists aevo_go_settings (
  setting_key text primary key,
  config jsonb not null,
  updated_by uuid,
  updated_at timestamptz not null default now(),
  constraint aevo_go_settings_key_check check (setting_key = 'default')
);

insert into aevo_go_settings (setting_key, config)
values (
  'default',
  '{
    "discovery": {"searchEnabled": true, "mapEnabled": true, "defaultRadiusKm": 15, "maxResults": 50},
    "community": {"traceDeeEnabled": true, "commentsEnabled": false, "contributionsEnabled": true, "requireModeration": true},
    "booking": {"enabled": true, "holdMinutes": 10, "maxPartySize": 12},
    "notifications": {"pushEnabled": true, "marketingOptInRequired": true},
    "privacy": {"allowGuestBrowse": true, "requireAccountToSave": true},
    "analytics": {"enabled": true, "retentionDays": 90}
  }'::jsonb
)
on conflict (setting_key) do nothing;

create table if not exists aevo_go_feature_flags (
  flag_key text primary key,
  enabled boolean not null default false,
  rollout_percent integer not null default 0,
  config jsonb not null default '{}'::jsonb,
  updated_by uuid,
  updated_at timestamptz not null default now(),
  constraint aevo_go_feature_flags_key_check check (flag_key in ('mixed_feed', 'trace_journey', 'comments_v2', 'expertise_v1', 'taste_ranking_v1')),
  constraint aevo_go_feature_flags_rollout_check check (rollout_percent between 0 and 100)
);

insert into aevo_go_feature_flags (flag_key, enabled, rollout_percent, config)
values
  ('mixed_feed', true, 100, '{"composition":"trace_first","version":1}'::jsonb),
  ('trace_journey', true, 100, '{"maxStops":20,"version":1}'::jsonb),
  ('comments_v2', false, 0, '{"version":1}'::jsonb),
  ('expertise_v1', false, 0, '{"version":1}'::jsonb),
  ('taste_ranking_v1', false, 0, '{"version":1}'::jsonb)
on conflict (flag_key) do nothing;

create table if not exists aevo_product_events (
  id uuid primary key default gen_random_uuid(),
  app_code text not null references aevo_application_registry(code),
  event_name text not null,
  actor_id uuid,
  organization_id uuid,
  store_id uuid,
  anonymous_id text,
  payload jsonb not null default '{}'::jsonb,
  request_id text,
  idempotency_key text,
  occurred_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  unique (app_code, idempotency_key)
);

create index if not exists aevo_product_events_app_time_idx
  on aevo_product_events (app_code, occurred_at desc);

create index if not exists aevo_product_events_org_time_idx
  on aevo_product_events (organization_id, occurred_at desc)
  where organization_id is not null;

alter table aevo_application_registry enable row level security;
alter table aevo_application_connections enable row level security;
alter table aevo_go_settings enable row level security;
alter table aevo_go_feature_flags enable row level security;
alter table aevo_product_events enable row level security;

create policy aevo_application_registry_platform_read_policy on aevo_application_registry
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

create policy aevo_application_registry_platform_write_policy on aevo_application_registry
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

create policy aevo_application_connections_platform_read_policy on aevo_application_connections
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

create policy aevo_application_connections_platform_write_policy on aevo_application_connections
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

create policy aevo_go_settings_platform_read_policy on aevo_go_settings
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

create policy aevo_go_settings_platform_write_policy on aevo_go_settings
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

create policy aevo_go_feature_flags_platform_read_policy on aevo_go_feature_flags
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

create policy aevo_go_feature_flags_platform_write_policy on aevo_go_feature_flags
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

create policy aevo_product_events_append_policy on aevo_product_events
  for insert
  with check (app_code = current_setting('aevo.app_code', true));

create policy aevo_product_events_platform_read_policy on aevo_product_events
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
