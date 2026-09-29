-- HUB-015: Core-owned read model for the Hub overview.  The projection is
-- tenant-scoped and carries its own freshness/source state so the UI can
-- label stale or partial data instead of fabricating a value.

create table if not exists aevo_hub_dashboard_projections (
  organization_id uuid not null,
  store_id uuid,
  scope_key text generated always as (organization_id::text || ':' || coalesce(store_id::text, 'organization')) stored,
  projection_version text not null default 'hub-dashboard-v1',
  projection jsonb not null default '{}'::jsonb,
  freshness_state text not null default 'fresh',
  source_updated_at timestamptz not null default now(),
  generated_at timestamptz not null default now(),
  last_error_code text,
  primary key (scope_key),
  constraint aevo_hub_dashboard_projection_freshness_check
    check (freshness_state in ('fresh', 'stale', 'partial', 'unavailable')),
  constraint aevo_hub_dashboard_projection_object_check
    check (jsonb_typeof(projection) = 'object' and octet_length(projection::text) <= 65536),
  constraint aevo_hub_dashboard_projection_store_fk
    foreign key (organization_id, store_id)
    references public.stores(organization_id, id)
    on delete cascade
);

create index if not exists aevo_hub_dashboard_projection_freshness_idx
  on aevo_hub_dashboard_projections (organization_id, freshness_state, generated_at desc);

alter table aevo_hub_dashboard_projections enable row level security;

drop policy if exists aevo_hub_dashboard_projection_read_policy on aevo_hub_dashboard_projections;
create policy aevo_hub_dashboard_projection_read_policy
  on aevo_hub_dashboard_projections
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_hub_dashboard_projections.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = aevo_hub_dashboard_projections.organization_id::text
        )
        and (
          aevo_hub_dashboard_projections.store_id is null
          or current_setting('aevo.store_id', true) = ''
          or current_setting('aevo.store_id', true) = aevo_hub_dashboard_projections.store_id::text
        )
    )
  );

drop policy if exists aevo_hub_dashboard_projection_platform_write_policy on aevo_hub_dashboard_projections;
create policy aevo_hub_dashboard_projection_platform_write_policy
  on aevo_hub_dashboard_projections
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_hub_dashboard_projections from public, anon, authenticated;

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
  'aevo_hub_dashboard_projections',
  'hub-dashboard-read-model',
  '{aevo-core-api}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api,aevo-background-worker}',
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
  last_reconciled_at = excluded.last_reconciled_at,
  delete_after = excluded.delete_after,
  updated_at = now();

comment on table aevo_hub_dashboard_projections is
  'HUB-015 tenant-scoped dashboard projection with explicit freshness and partial-source state.';
