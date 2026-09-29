-- HUB-017: move the remaining Hub catalog/plan inputs into Core before
-- retiring the historical public compatibility projections.
--
-- This migration is additive. It deliberately does not drop the historical
-- public tables until the application writers/readers have been removed and
-- the retirement migration has passed its zero-reader checks.

alter table aevo_application_registry
  add column if not exists pricing_model text,
  add column if not exists base_price_monthly_minor integer,
  add column if not exists features jsonb;

update aevo_application_registry
set pricing_model = coalesce(nullif(trim(pricing_model), ''), 'PER_ORGANIZATION'),
    base_price_monthly_minor = coalesce(base_price_monthly_minor, 0),
    features = case when jsonb_typeof(features) = 'array' then features else '[]'::jsonb end,
    updated_at = now();

alter table aevo_application_registry
  alter column pricing_model set default 'PER_ORGANIZATION',
  alter column pricing_model set not null,
  alter column base_price_monthly_minor set default 0,
  alter column base_price_monthly_minor set not null,
  alter column features set default '[]'::jsonb,
  alter column features set not null;

do $$
begin
  if to_regclass('public.apps') is not null then
    update aevo_application_registry registry
    set pricing_model = coalesce(nullif(app.pricing_model, ''), registry.pricing_model),
        base_price_monthly_minor = coalesce(app.base_price_monthly_minor, registry.base_price_monthly_minor),
        features = case when jsonb_typeof(app.features) = 'array' then app.features else registry.features end,
        updated_at = now()
    from public.apps app
    where lower(app.id) = case when registry.code = 'PLAY' then 'booking' else lower(registry.code) end;
  end if;
end $$;

create table if not exists aevo_plan_entitlements (
  plan_id text not null,
  feature_key text not null,
  is_enabled boolean not null default true,
  limit_value integer,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (plan_id, feature_key)
);

do $$
begin
  if to_regclass('public.plan_entitlements') is not null then
    insert into aevo_plan_entitlements (plan_id, feature_key, is_enabled, limit_value)
    select plan_id, feature_key, is_enabled, limit_value
    from public.plan_entitlements
    on conflict (plan_id, feature_key) do update set
      is_enabled = excluded.is_enabled,
      limit_value = excluded.limit_value,
      updated_at = now();
  end if;
end $$;

alter table aevo_plan_entitlements enable row level security;
drop policy if exists aevo_plan_entitlements_platform_read_policy on aevo_plan_entitlements;
create policy aevo_plan_entitlements_platform_read_policy
  on aevo_plan_entitlements
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

drop policy if exists aevo_plan_entitlements_platform_write_policy on aevo_plan_entitlements;
create policy aevo_plan_entitlements_platform_write_policy
  on aevo_plan_entitlements
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_plan_entitlements from public, anon, authenticated;

comment on table aevo_plan_entitlements is
  'Core-owned plan feature projection. public.plan_entitlements is historical compatibility input only.';
comment on column aevo_application_registry.features is
  'Core-owned commercial catalog metadata; this does not grant access.';
