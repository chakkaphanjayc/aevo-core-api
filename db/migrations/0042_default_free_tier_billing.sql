-- BILLING-001: establish the Core-owned default Free tier.
--
-- The historical starter plan is the development/product name used by the
-- existing organization creation flow.  It is now explicitly catalogued as
-- the permanent Free tier and carries a single, organization-wide
-- store-application binding quota.  The quota is deliberately separate from
-- the per-application feature entitlements so a Free organization may choose
-- any one first-party store application, but cannot activate a second app or
-- another store binding without upgrading.

create table if not exists aevo_billing_plans (
  plan_id text primary key,
  display_name text not null,
  description text not null default '',
  billing_interval text not null default 'NONE',
  price_minor integer not null default 0,
  currency text not null default 'THB',
  status text not null default 'ACTIVE',
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_billing_plans_interval_check
    check (billing_interval in ('NONE', 'MONTHLY', 'ANNUAL')),
  constraint aevo_billing_plans_price_check
    check (price_minor >= 0),
  constraint aevo_billing_plans_currency_check
    check (currency ~ '^[A-Z]{3}$'),
  constraint aevo_billing_plans_status_check
    check (status in ('ACTIVE', 'ARCHIVED'))
);

insert into aevo_billing_plans (
  plan_id,
  display_name,
  description,
  billing_interval,
  price_minor,
  currency,
  status
)
values (
  'starter',
  'Free',
  'Start with one enabled application on one store. Upgrade when the organization needs more application surfaces or store bindings.',
  'NONE',
  0,
  'THB',
  'ACTIVE'
)
on conflict (plan_id) do update set
  display_name = excluded.display_name,
  description = excluded.description,
  billing_interval = excluded.billing_interval,
  price_minor = excluded.price_minor,
  currency = excluded.currency,
  status = excluded.status,
  updated_at = now();

-- Commercial application entitlements describe which product surfaces the
-- plan may use.  The aggregate binding entitlement below is the quota that
-- limits the number of active store/application pairs.
insert into aevo_plan_entitlements (plan_id, feature_key, is_enabled, limit_value)
select 'starter', feature_key, true, null
from unnest(array['booking', 'pos', 'kiosk', 'queue']::text[]) as features(feature_key)
on conflict (plan_id, feature_key) do update set
  is_enabled = excluded.is_enabled,
  limit_value = excluded.limit_value,
  updated_at = now();

insert into aevo_plan_entitlements (plan_id, feature_key, is_enabled, limit_value)
values ('starter', 'store_application_bindings', true, 1)
on conflict (plan_id, feature_key) do update set
  is_enabled = excluded.is_enabled,
  limit_value = excluded.limit_value,
  updated_at = now();

alter table aevo_billing_plans enable row level security;
drop policy if exists aevo_billing_plans_platform_read_policy on aevo_billing_plans;
create policy aevo_billing_plans_platform_read_policy
  on aevo_billing_plans
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

drop policy if exists aevo_billing_plans_platform_write_policy on aevo_billing_plans;
create policy aevo_billing_plans_platform_write_policy
  on aevo_billing_plans
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_billing_plans from public, anon, authenticated;

-- Existing organizations created before this migration receive the same
-- permanent Free subscription when they have no subscription yet.  Existing
-- local/manual starter trials are normalized to Free instead of expiring and
-- breaking the default development experience.  Provider-managed paid rows
-- are not changed.
insert into aevo_organization_subscriptions (
  organization_id,
  plan_id,
  provider,
  status,
  current_period_start,
  current_period_end,
  cancel_at_period_end,
  projection_version
)
select
  organization.id,
  'starter',
  'MANUAL',
  'ACTIVE',
  timezone('utc', now()),
  null,
  false,
  'billing-v1'
from public.organizations organization
on conflict (organization_id) do nothing;

update aevo_organization_subscriptions
set status = 'ACTIVE',
    trial_start = null,
    trial_end = null,
    current_period_start = coalesce(current_period_start, timezone('utc', now())),
    current_period_end = null,
    cancel_at_period_end = false,
    canceled_at = null,
    updated_at = now()
where plan_id = 'starter'
  and provider = 'MANUAL'
  and provider_subscription_id is null;

-- Resolve the plan into organization entitlements for both existing and new
-- organizations.  Organization overrides remain authoritative and are never
-- overwritten by this default-plan backfill.
insert into aevo_organization_entitlements (
  organization_id,
  feature_key,
  is_enabled,
  custom_override,
  limit_value,
  source,
  projection_version
)
select
  subscription.organization_id,
  plan_entitlement.feature_key,
  plan_entitlement.is_enabled,
  false,
  plan_entitlement.limit_value,
  'PLAN',
  'entitlements-v1'
from aevo_organization_subscriptions subscription
join aevo_plan_entitlements plan_entitlement
  on plan_entitlement.plan_id = subscription.plan_id
where subscription.plan_id = 'starter'
on conflict (organization_id, feature_key) do update set
  is_enabled = excluded.is_enabled,
  limit_value = excluded.limit_value,
  source = excluded.source,
  projection_version = excluded.projection_version,
  updated_at = now()
where not aevo_organization_entitlements.custom_override;

comment on table aevo_billing_plans is
  'Core-owned commercial plan catalog. Billing providers update subscriptions through verified webhook projections.';
comment on table aevo_plan_entitlements is
  'Core-owned plan defaults. Organization entitlements are the resolved authorization projection.';
comment on column aevo_plan_entitlements.limit_value is
  'A server-enforced resource quota. For store_application_bindings this is the maximum active organization-wide store/app pairs.';

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
  'aevo_billing_plans',
  'billing-plan-catalog',
  '{aevo-core-api}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api}',
  '{aevo-core-api,aevo-hub,aevo-admin}',
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
  last_reconciled_at = now(),
  delete_after = excluded.delete_after,
  updated_at = now();
