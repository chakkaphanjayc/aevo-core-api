-- HUB-010: Core-owned billing event, subscription, and entitlement projections.
--
-- Provider events are immutable inputs.  These projections are the only
-- tables the Core authorization evaluator may use after this migration.  The
-- historical public tables remain compatibility projections until all legacy
-- readers and writers complete their cutover.

create table if not exists aevo_billing_webhook_events (
  provider text not null,
  event_id text not null,
  event_type text not null,
  organization_id uuid,
  provider_customer_id text,
  provider_subscription_id text,
  payload jsonb not null default '{}'::jsonb,
  status text not null default 'RECEIVED',
  error_code text,
  received_at timestamptz not null default now(),
  processed_at timestamptz,
  constraint aevo_billing_webhook_events_provider_check
    check (provider in ('STRIPE', 'OPN', 'XENDIT', 'MANUAL')),
  constraint aevo_billing_webhook_events_status_check
    check (status in ('RECEIVED', 'APPLIED', 'IGNORED', 'FAILED')),
  primary key (provider, event_id)
);

create index if not exists aevo_billing_webhook_events_org_idx
  on aevo_billing_webhook_events (organization_id, received_at desc)
  where organization_id is not null;

create table if not exists aevo_organization_subscriptions (
  organization_id uuid primary key,
  plan_id text not null,
  provider text not null default 'MANUAL',
  provider_customer_id text,
  provider_subscription_id text,
  status text not null default 'TRIALING',
  trial_start timestamptz,
  trial_end timestamptz,
  current_period_start timestamptz,
  current_period_end timestamptz,
  cancel_at_period_end boolean not null default false,
  canceled_at timestamptz,
  metadata jsonb not null default '{}'::jsonb,
  projection_version text not null default 'billing-v1',
  last_event_id text,
  updated_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_organization_subscriptions_provider_check
    check (provider in ('STRIPE', 'MANUAL', 'ENTERPRISE', 'OPN', 'XENDIT')),
  constraint aevo_organization_subscriptions_status_check
    check (status in ('TRIALING', 'ACTIVE', 'PAST_DUE', 'GRACE_PERIOD', 'CANCELED', 'EXPIRED')),
  constraint aevo_organization_subscriptions_projection_check
    check (projection_version ~ '^billing-v[0-9]+$')
);

create unique index if not exists aevo_organization_subscriptions_provider_subscription_idx
  on aevo_organization_subscriptions (provider, provider_subscription_id)
  where provider_subscription_id is not null;

create table if not exists aevo_organization_entitlements (
  organization_id uuid not null,
  feature_key text not null,
  is_enabled boolean not null default true,
  custom_override boolean not null default false,
  limit_value integer,
  source text not null default 'PLAN',
  projection_version text not null default 'entitlements-v1',
  last_event_id text,
  updated_by uuid,
  updated_at timestamptz not null default now(),
  primary key (organization_id, feature_key),
  constraint aevo_organization_entitlements_source_check
    check (source in ('PLAN', 'OVERRIDE', 'SYSTEM', 'BILLING')),
  constraint aevo_organization_entitlements_projection_check
    check (projection_version ~ '^entitlements-v[0-9]+$')
);

create index if not exists aevo_organization_entitlements_enabled_idx
  on aevo_organization_entitlements (organization_id, is_enabled, feature_key);

-- Backfill the canonical billing projection from the transitional Core-read
-- tables.  This is a copy, not a destructive migration.
insert into aevo_organization_subscriptions (
  organization_id,
  plan_id,
  provider,
  provider_customer_id,
  provider_subscription_id,
  status,
  trial_start,
  trial_end,
  current_period_start,
  current_period_end,
  cancel_at_period_end,
  canceled_at,
  metadata,
  projection_version,
  created_at,
  updated_at
)
select
  subscription.organization_id,
  subscription.plan_id,
  subscription.provider,
  subscription.provider_customer_id,
  subscription.provider_subscription_id,
  subscription.status,
  subscription.trial_start,
  subscription.trial_end,
  subscription.current_period_start,
  subscription.current_period_end,
  subscription.cancel_at_period_end,
  subscription.canceled_at,
  subscription.metadata,
  'billing-v1',
  subscription.created_at,
  subscription.updated_at
from public.subscriptions subscription
on conflict (organization_id) do update set
  plan_id = excluded.plan_id,
  provider = excluded.provider,
  provider_customer_id = excluded.provider_customer_id,
  provider_subscription_id = excluded.provider_subscription_id,
  status = excluded.status,
  trial_start = excluded.trial_start,
  trial_end = excluded.trial_end,
  current_period_start = excluded.current_period_start,
  current_period_end = excluded.current_period_end,
  cancel_at_period_end = excluded.cancel_at_period_end,
  canceled_at = excluded.canceled_at,
  metadata = excluded.metadata,
  projection_version = 'billing-v1',
  updated_at = excluded.updated_at;

insert into aevo_organization_entitlements (
  organization_id,
  feature_key,
  is_enabled,
  custom_override,
  limit_value,
  source,
  projection_version,
  updated_at
)
select
  entitlement.organization_id,
  entitlement.feature_key,
  entitlement.is_enabled,
  entitlement.custom_override,
  entitlement.limit_value,
  case when entitlement.custom_override then 'OVERRIDE' else 'PLAN' end,
  'entitlements-v1',
  entitlement.updated_at
from public.organization_entitlements entitlement
on conflict (organization_id, feature_key) do update set
  is_enabled = excluded.is_enabled,
  custom_override = excluded.custom_override,
  limit_value = excluded.limit_value,
  source = excluded.source,
  projection_version = 'entitlements-v1',
  updated_at = excluded.updated_at;

-- The legacy webhook event table is optional in older development databases;
-- copy it when present without making the compatibility table authoritative.
do $$
begin
  if to_regclass('public.billing_webhook_events') is not null then
    execute $sql$
      insert into aevo_billing_webhook_events (
        provider,
        event_id,
        event_type,
        payload,
        status,
        received_at,
        processed_at
      )
      select
        legacy.provider,
        legacy.id,
        legacy.event_type,
        legacy.payload,
        case when legacy.processed then 'APPLIED' else 'RECEIVED' end,
        legacy.created_at,
        case when legacy.processed then legacy.created_at else null end
      from public.billing_webhook_events legacy
      on conflict (provider, event_id) do nothing
    $sql$;
  end if;
end
$$;

alter table aevo_billing_webhook_events enable row level security;
drop policy if exists aevo_billing_webhook_events_platform_read_policy on aevo_billing_webhook_events;
create policy aevo_billing_webhook_events_platform_read_policy
  on aevo_billing_webhook_events
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

drop policy if exists aevo_billing_webhook_events_platform_write_policy on aevo_billing_webhook_events;
create policy aevo_billing_webhook_events_platform_write_policy
  on aevo_billing_webhook_events
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

alter table aevo_organization_subscriptions enable row level security;
drop policy if exists aevo_organization_subscriptions_read_policy on aevo_organization_subscriptions;
create policy aevo_organization_subscriptions_read_policy
  on aevo_organization_subscriptions
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_organization_subscriptions.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = membership.organization_id::text
        )
    )
  );

drop policy if exists aevo_organization_subscriptions_platform_write_policy on aevo_organization_subscriptions;
create policy aevo_organization_subscriptions_platform_write_policy
  on aevo_organization_subscriptions
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

alter table aevo_organization_entitlements enable row level security;
drop policy if exists aevo_organization_entitlements_read_policy on aevo_organization_entitlements;
create policy aevo_organization_entitlements_read_policy
  on aevo_organization_entitlements
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_organization_entitlements.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = membership.organization_id::text
        )
    )
  );

drop policy if exists aevo_organization_entitlements_platform_write_policy on aevo_organization_entitlements;
create policy aevo_organization_entitlements_platform_write_policy
  on aevo_organization_entitlements
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_billing_webhook_events from public;
revoke all on table aevo_organization_subscriptions from public;
revoke all on table aevo_organization_entitlements from public;

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
    'aevo_billing_webhook_events',
    'billing-webhook-event',
    '{aevo-hub,aevo-pos}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-admin}',
    'CORE_ONLY',
    'CANONICAL',
    'CANONICAL',
    now(),
    null
  ),
  (
    'aevo',
    'aevo_organization_subscriptions',
    'billing-subscription',
    '{aevo-hub,aevo-pos}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-admin,aevo-pos,aevo-play}',
    'CORE_ONLY',
    'CANONICAL',
    'CANONICAL',
    now(),
    null
  ),
  (
    'aevo',
    'aevo_organization_entitlements',
    'entitlement-projection',
    '{aevo-hub,aevo-pos,aevo-play}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-admin,aevo-pos,aevo-play}',
    'CORE_ONLY',
    'CANONICAL',
    'CANONICAL',
    now(),
    null
  ),
  (
    'public',
    'subscriptions',
    'billing-subscription-compatibility',
    '{aevo-hub,aevo-pos}',
    'aevo-core-api transitional compatibility boundary',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-admin,aevo-pos,aevo-play}',
    'TRANSITIONAL_CORE_API',
    'OBSERVE',
    'RECONCILE_REQUIRED',
    now(),
    'After Core billing webhook projection and all readers are verified'
  ),
  (
    'public',
    'organization_entitlements',
    'entitlement-projection-compatibility',
    '{aevo-hub,aevo-pos,aevo-play}',
    'aevo-core-api transitional compatibility boundary',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-admin,aevo-pos,aevo-play}',
    'TRANSITIONAL_CORE_API',
    'OBSERVE',
    'RECONCILE_REQUIRED',
    now(),
    'After Core billing webhook projection and all readers are verified'
  ),
  (
    'public',
    'billing_webhook_events',
    'billing-webhook-event-compatibility',
    '{aevo-hub,aevo-pos}',
    'aevo-pos transitional adapter',
    'aevo-core-api',
    '{aevo-pos}',
    '{aevo-pos}',
    'TRANSITIONAL_CORE_API',
    'OBSERVE',
    'RECONCILE_REQUIRED',
    now(),
    'After Core billing event ingestion is used by the provider adapter'
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

comment on table aevo_billing_webhook_events is
  'Core-owned immutable billing webhook event ledger with replay-safe provider event identity.';
comment on table aevo_organization_subscriptions is
  'Core-owned organization subscription projection. Billing lifecycle is not a member or app assignment.';
comment on table aevo_organization_entitlements is
  'Core-owned organization feature entitlement projection evaluated together with subscription and store binding state.';
