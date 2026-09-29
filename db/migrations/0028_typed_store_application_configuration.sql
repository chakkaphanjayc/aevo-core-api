-- HUB-011: Core-owned, typed store application configuration.
--
-- Hub may present the configuration contract, but application schemas and
-- values are validated and stored by Core.  The table contains only
-- non-secret, app-scoped settings; provider credentials and tokens belong in
-- the owning application's secret/configuration system.

create table if not exists aevo_store_application_configurations (
  organization_id uuid not null,
  store_id uuid not null,
  app_code text not null references aevo_application_registry(code) on update cascade on delete restrict,
  schema_ref text not null,
  schema_version text not null,
  config jsonb not null default '{}'::jsonb,
  updated_by uuid not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (organization_id, store_id, app_code, schema_ref),
  foreign key (organization_id, store_id)
    references public.stores(organization_id, id)
    on delete cascade,
  constraint aevo_store_application_config_schema_ref_check
    check (schema_ref ~ '^[a-z0-9][a-z0-9.-]{1,63}$'),
  constraint aevo_store_application_config_schema_version_check
    check (schema_version ~ '^[0-9]+$'),
  constraint aevo_store_application_config_object_check
    check (jsonb_typeof(config) = 'object' and octet_length(config::text) <= 16384)
);

create index if not exists aevo_store_application_config_store_idx
  on aevo_store_application_configurations (organization_id, store_id, app_code);

drop trigger if exists aevo_store_application_config_set_updated_at on aevo_store_application_configurations;
create trigger aevo_store_application_config_set_updated_at
before update on aevo_store_application_configurations
for each row execute function public.set_updated_at();

alter table aevo_store_application_configurations enable row level security;

drop policy if exists aevo_store_application_config_read_policy on aevo_store_application_configurations;
create policy aevo_store_application_config_read_policy
  on aevo_store_application_configurations
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_store_application_configurations.organization_id
        and membership.status = 'ACTIVE'
        and current_setting('aevo.organization_id', true) = membership.organization_id::text
        and current_setting('aevo.store_id', true) = aevo_store_application_configurations.store_id::text
    )
  );

drop policy if exists aevo_store_application_config_member_insert_policy on aevo_store_application_configurations;
create policy aevo_store_application_config_member_insert_policy
  on aevo_store_application_configurations
  for insert
  with check (
    current_setting('aevo.app_code', true) = 'HUB'
    and exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_store_application_configurations.organization_id
        and membership.status = 'ACTIVE'
        and current_setting('aevo.organization_id', true) = membership.organization_id::text
        and current_setting('aevo.store_id', true) = aevo_store_application_configurations.store_id::text
    )
  );

drop policy if exists aevo_store_application_config_member_update_policy on aevo_store_application_configurations;
create policy aevo_store_application_config_member_update_policy
  on aevo_store_application_configurations
  for update
  using (
    current_setting('aevo.app_code', true) = 'HUB'
    and exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_store_application_configurations.organization_id
        and membership.status = 'ACTIVE'
        and current_setting('aevo.organization_id', true) = membership.organization_id::text
        and current_setting('aevo.store_id', true) = aevo_store_application_configurations.store_id::text
    )
  )
  with check (
    current_setting('aevo.app_code', true) = 'HUB'
    and organization_id::text = current_setting('aevo.organization_id', true)
    and store_id::text = current_setting('aevo.store_id', true)
  );

drop policy if exists aevo_store_application_config_platform_write_policy on aevo_store_application_configurations;
create policy aevo_store_application_config_platform_write_policy
  on aevo_store_application_configurations
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_store_application_configurations from public, anon, authenticated;

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
  'aevo_store_application_configurations',
  'typed-store-app-configuration',
  '{aevo-core-api}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api}',
  '{aevo-core-api,aevo-hub,aevo-pos,aevo-play}',
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

comment on table aevo_store_application_configurations is
  'HUB-011 Core-owned typed store application configuration. Secret material is not accepted.';
