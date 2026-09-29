-- HUB-008/HUB-012: Core-owned installation and provisioning boundary.
--
-- Store application bindings remain the store-scoped decision.  This
-- projection records the organization-level installation state and the
-- durable operation boundary used by Hub mutations.  Neither table grants
-- an entitlement, membership assignment, or permission by itself.

create table if not exists aevo_application_installations (
  organization_id uuid not null,
  app_code text not null references aevo_application_registry(code) on update cascade on delete restrict,
  status text not null default 'DISABLED',
  source text not null default 'SYSTEM',
  plan_code text,
  provider text,
  provider_customer_id text,
  provider_subscription_id text,
  trial_ends_at timestamptz,
  current_period_starts_at timestamptz,
  current_period_ends_at timestamptz,
  grace_period_ends_at timestamptz,
  projection_version text not null default 'installation-v1',
  last_event_id text,
  updated_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (organization_id, app_code),
  constraint aevo_application_installations_status_check
    check (status in ('PENDING', 'TRIALING', 'ACTIVE', 'PAST_DUE', 'GRACE_PERIOD', 'CANCELED', 'EXPIRED', 'DISABLED', 'ERROR')),
  constraint aevo_application_installations_source_check
    check (source in ('BACKFILL', 'BILLING', 'HUB', 'SYSTEM', 'TEMPLATE')),
  constraint aevo_application_installations_projection_version_check
    check (projection_version ~ '^installation-v[0-9]+$')
);

create index if not exists aevo_application_installations_status_idx
  on aevo_application_installations (organization_id, status, app_code);

create table if not exists aevo_provisioning_operations (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null,
  store_id uuid,
  app_code text not null references aevo_application_registry(code) on update cascade on delete restrict,
  operation_type text not null,
  idempotency_key varchar(128) not null,
  request_hash text not null,
  status text not null default 'PENDING',
  response jsonb not null default '{}'::jsonb,
  error_code text,
  created_by uuid,
  created_at timestamptz not null default now(),
  completed_at timestamptz,
  constraint aevo_provisioning_operations_type_check
    check (operation_type in ('STORE_APPLICATION_BINDING', 'APPLICATION_INSTALLATION')),
  constraint aevo_provisioning_operations_status_check
    check (status in ('PENDING', 'SUCCEEDED', 'FAILED')),
  constraint aevo_provisioning_operations_key_check
    check (char_length(trim(idempotency_key)) between 8 and 128),
  constraint aevo_provisioning_operations_hash_check
    check (request_hash ~ '^[a-f0-9]{64}$'),
  unique (organization_id, idempotency_key)
);

create index if not exists aevo_provisioning_operations_target_idx
  on aevo_provisioning_operations (organization_id, store_id, app_code, created_at desc);

-- Start with a deterministic disabled row for every Hub-managed application.
-- The DIGITAL_SIGN owner repository is deliberately outside this boundary.
insert into aevo_application_installations (organization_id, app_code, status, source, projection_version)
select organizations.id, registry.code, 'DISABLED', 'SYSTEM', 'installation-v1'
from public.organizations organizations
cross join aevo_application_registry registry
where registry.owner_repository <> 'aevo-digital-sing'
on conflict (organization_id, app_code) do nothing;

-- The organization subscription is the historical billing projection for the
-- Hub application itself.  Keep its lifecycle visible as an installation
-- state, but do not use this row as an authorization shortcut.
update aevo_application_installations installation
set status = case
               when subscription.status in ('TRIALING', 'ACTIVE', 'PAST_DUE', 'GRACE_PERIOD', 'CANCELED', 'EXPIRED')
                 then subscription.status
               else 'DISABLED'
             end,
    source = 'BACKFILL',
    plan_code = subscription.plan_id,
    provider = subscription.provider,
    provider_customer_id = subscription.provider_customer_id,
    provider_subscription_id = subscription.provider_subscription_id,
    trial_ends_at = subscription.trial_end,
    current_period_starts_at = subscription.current_period_start,
    current_period_ends_at = subscription.current_period_end,
    projection_version = 'installation-v1',
    updated_at = now()
from public.subscriptions subscription
where installation.app_code = 'HUB'
  and installation.organization_id = subscription.organization_id;

-- Backfill app-level subscriptions when the historical table is present.  A
-- booking app subscription maps to the Core PLAY application code.  The
-- update is intentionally limited to rows still owned by this migration so a
-- later Core billing projection cannot be overwritten by a repeated script.
do $$
begin
  if to_regclass('public.app_subscriptions') is not null then
    execute $sql$
      with normalized as (
        select distinct on (legacy.organization_id, registry.code)
          legacy.organization_id,
          registry.code as app_code,
          case upper(legacy.status)
            when 'TRIAL' then 'TRIALING'
            when 'TRIALING' then 'TRIALING'
            when 'ACTIVE' then 'ACTIVE'
            when 'PAST_DUE' then 'PAST_DUE'
            when 'GRACE_PERIOD' then 'GRACE_PERIOD'
            when 'CANCELED' then 'CANCELED'
            when 'CANCELLED' then 'CANCELED'
            when 'EXPIRED' then 'EXPIRED'
            else 'DISABLED'
          end as status,
          legacy.plan_code,
          legacy.trial_ends_at,
          legacy.current_period_starts_at,
          legacy.current_period_ends_at,
          legacy.grace_period_ends_at
        from public.app_subscriptions legacy
        join aevo_application_registry registry
          on registry.code = case lower(legacy.app_id)
            when 'booking' then 'PLAY'
            else upper(legacy.app_id)
          end
         and registry.owner_repository <> 'aevo-digital-sing'
        order by legacy.organization_id,
                 registry.code,
                 case upper(legacy.status)
                   when 'ACTIVE' then 0
                   when 'TRIAL' then 1
                   when 'TRIALING' then 1
                   when 'GRACE_PERIOD' then 2
                   when 'PAST_DUE' then 3
                   else 4
                 end,
                 legacy.updated_at desc,
                 legacy.created_at desc
      )
      update aevo_application_installations installation
      set status = normalized.status,
          source = 'BACKFILL',
          plan_code = normalized.plan_code,
          trial_ends_at = normalized.trial_ends_at,
          current_period_starts_at = normalized.current_period_starts_at,
          current_period_ends_at = normalized.current_period_ends_at,
          grace_period_ends_at = normalized.grace_period_ends_at,
          projection_version = 'installation-v1',
          updated_at = now()
      from normalized
      where installation.organization_id = normalized.organization_id
        and installation.app_code = normalized.app_code
        and installation.source in ('SYSTEM', 'BACKFILL');
    $sql$;
  end if;
end
$$;

-- Existing active store bindings prove that the application was installed for
-- at least one store.  This only updates installation metadata; entitlement
-- evaluation still requires the billing and feature projections separately.
update aevo_application_installations installation
set status = 'ACTIVE',
    source = 'BACKFILL',
    projection_version = 'installation-v1',
    updated_at = now()
where exists (
  select 1
  from aevo_store_application_bindings binding
  where binding.organization_id = installation.organization_id
    and binding.app_code = installation.app_code
    and binding.status = 'ACTIVE'
)
and installation.status = 'DISABLED';

alter table aevo_application_installations enable row level security;
drop policy if exists aevo_application_installations_read_policy on aevo_application_installations;
create policy aevo_application_installations_read_policy
  on aevo_application_installations
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_application_installations.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = membership.organization_id::text
        )
    )
  );

drop policy if exists aevo_application_installations_platform_write_policy on aevo_application_installations;
create policy aevo_application_installations_platform_write_policy
  on aevo_application_installations
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

alter table aevo_provisioning_operations enable row level security;
drop policy if exists aevo_provisioning_operations_read_policy on aevo_provisioning_operations;
create policy aevo_provisioning_operations_read_policy
  on aevo_provisioning_operations
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_provisioning_operations.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = membership.organization_id::text
        )
    )
  );

drop policy if exists aevo_provisioning_operations_platform_write_policy on aevo_provisioning_operations;
create policy aevo_provisioning_operations_platform_write_policy
  on aevo_provisioning_operations
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_application_installations from public;
revoke all on table aevo_provisioning_operations from public;

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
    'aevo_application_installations',
    'application-installation',
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
    'aevo',
    'aevo_provisioning_operations',
    'app-provisioning-operation',
    '{aevo-hub,aevo-pos,aevo-play}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-pos,aevo-play}',
    'CORE_ONLY',
    'CANONICAL',
    'CANONICAL',
    now(),
    null
  ),
  (
    'public',
    'app_subscriptions',
    'application-installation-compatibility',
    '{aevo-hub,aevo-pos,aevo-play}',
    'aevo-core-api transitional compatibility boundary',
    'aevo-core-api',
    '{aevo-core-api,aevo-pos,aevo-play}',
    '{aevo-hub,aevo-pos,aevo-play}',
    'TRANSITIONAL_CORE_API',
    'OBSERVE',
    'RECONCILE_REQUIRED',
    now(),
    'After POS/Play subscription readers and billing writers use aevo_application_installations'
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

comment on table aevo_application_installations is
  'Core-owned organization installation projection. It never grants entitlement or member access.';
comment on table aevo_provisioning_operations is
  'Core-owned idempotent provisioning operation ledger for app/store mutations and outbox events.';
