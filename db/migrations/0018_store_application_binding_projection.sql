-- HUB-008: Core-canonical store application bindings.
--
-- The public table remains a compatibility projection during the expand and
-- observe window.  This migration backfills the Core projection and creates a
-- database-side initializer for stores created by transitional SQL paths.  It
-- deliberately excludes aevo-digital-sing from Hub-managed app state.

create table if not exists aevo_store_application_bindings (
  organization_id uuid not null,
  store_id uuid not null,
  app_code text not null references aevo_application_registry(code) on update cascade on delete restrict,
  status text not null default 'DISABLED',
  source text not null default 'SYSTEM',
  projection_version text not null default 'store-binding-v1',
  updated_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (organization_id, store_id, app_code),
  constraint aevo_store_application_bindings_status_check
    check (status in ('ACTIVE', 'DISABLED')),
  constraint aevo_store_application_bindings_source_check
    check (source in ('BACKFILL', 'HUB', 'SYSTEM', 'TEMPLATE', 'BILLING_PROJECTION')),
  constraint aevo_store_application_bindings_projection_version_check
    check (projection_version ~ '^store-binding-v[0-9]+$')
);

create index if not exists aevo_store_application_bindings_store_idx
  on aevo_store_application_bindings (organization_id, store_id, status, app_code);

insert into aevo_store_application_bindings (
  organization_id,
  store_id,
  app_code,
  status,
  source,
  projection_version,
  updated_by,
  created_at,
  updated_at
)
select
  legacy.organization_id,
  legacy.store_id,
  legacy.application_code,
  legacy.status,
  'BACKFILL',
  'store-binding-v1',
  legacy.updated_by,
  legacy.created_at,
  legacy.updated_at
from public.store_application_access legacy
join aevo_application_registry registry
  on registry.code = legacy.application_code
 and registry.store_scoped = true
 and registry.owner_repository <> 'aevo-digital-sing'
on conflict (organization_id, store_id, app_code) do update
set status = excluded.status,
    updated_by = excluded.updated_by,
    updated_at = excluded.updated_at;

-- Stores created through the transitional public function still receive an
-- explicit Core row.  The function is only a projection initializer; access
-- remains disabled until Core evaluates entitlement and a Hub action enables
-- the binding.
create or replace function public.aevo_initialize_store_application_bindings()
returns trigger
language plpgsql
security definer
set search_path = pg_catalog, public
as $$
begin
  insert into public.aevo_store_application_bindings (
    organization_id,
    store_id,
    app_code,
    status,
    source,
    projection_version
  )
  select
    new.organization_id,
    new.id,
    registry.code,
    'DISABLED',
    'SYSTEM',
    'store-binding-v1'
  from public.aevo_application_registry registry
  where registry.store_scoped = true
    and registry.owner_repository <> 'aevo-digital-sing'
  on conflict (organization_id, store_id, app_code) do nothing;
  return new;
end;
$$;

do $$
begin
  if to_regclass('public.stores') is not null then
    execute 'drop trigger if exists stores_initialize_aevo_application_bindings on public.stores';
    execute 'create trigger stores_initialize_aevo_application_bindings after insert on public.stores for each row execute function public.aevo_initialize_store_application_bindings()';
  end if;
end;
$$;

alter table aevo_store_application_bindings enable row level security;

drop policy if exists aevo_store_application_bindings_read_policy on aevo_store_application_bindings;
create policy aevo_store_application_bindings_read_policy
  on aevo_store_application_bindings
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_store_application_bindings.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = membership.organization_id::text
        )
        and (
          current_setting('aevo.store_id', true) = ''
          or current_setting('aevo.store_id', true) = aevo_store_application_bindings.store_id::text
        )
    )
  );

drop policy if exists aevo_store_application_bindings_platform_write_policy on aevo_store_application_bindings;
create policy aevo_store_application_bindings_platform_write_policy
  on aevo_store_application_bindings
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_store_application_bindings from public;
revoke all on function public.aevo_initialize_store_application_bindings() from public, anon, authenticated;

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
  'aevo_store_application_bindings',
  'store-app-binding',
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

update aevo_control_plane_migration_ledger
set migration_phase = 'OBSERVE',
    transition_state = 'RECONCILE_REQUIRED',
    last_reconciled_at = now(),
    updated_at = now(),
    delete_after = 'After POS/Play readers migrate to aevo_store_application_bindings and the compatibility projection is retired'
where schema_name = 'public'
  and table_name = 'store_application_access';

comment on table aevo_store_application_bindings is
  'HUB-008 Core-canonical store application binding projection. Entitlement and member assignment remain separate decisions.';

