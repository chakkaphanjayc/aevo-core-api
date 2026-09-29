-- HUB-RETENTION-001: store data must pass through an auditable retention
-- window before any purge worker is allowed to remove it.
--
-- The Hub only creates or cancels a request. A background retention worker is
-- the only component that may process due rows after scheduled_purge_at.

create table if not exists aevo_data_deletion_requests (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  resource_type text not null,
  resource_id uuid not null,
  status text not null default 'PENDING',
  requested_by uuid not null,
  requested_at timestamptz not null default now(),
  scheduled_purge_at timestamptz not null,
  cancelled_by uuid,
  cancelled_at timestamptz,
  purged_at timestamptz,
  reason text not null default 'Requested by an organization administrator.',
  request_id text,
  metadata jsonb not null default '{}'::jsonb,
  constraint aevo_data_deletion_resource_type_check
    check (resource_type in ('STORE', 'ORGANIZATION', 'MEMBER', 'STORE_TEMPLATE')),
  constraint aevo_data_deletion_status_check
    check (status in ('PENDING', 'CANCELLED', 'PURGED')),
  constraint aevo_data_deletion_schedule_check
    check (scheduled_purge_at >= requested_at),
  constraint aevo_data_deletion_cancel_state_check
    check ((status = 'CANCELLED') = (cancelled_at is not null and cancelled_by is not null)),
  constraint aevo_data_deletion_purge_state_check
    check ((status = 'PURGED') = (purged_at is not null))
);

create unique index if not exists aevo_data_deletion_pending_resource_idx
  on aevo_data_deletion_requests (organization_id, resource_type, resource_id)
  where status = 'PENDING';

create index if not exists aevo_data_deletion_due_idx
  on aevo_data_deletion_requests (status, scheduled_purge_at)
  where status = 'PENDING';

create index if not exists aevo_data_deletion_org_created_idx
  on aevo_data_deletion_requests (organization_id, requested_at desc);

alter table aevo_data_deletion_requests enable row level security;

drop policy if exists aevo_data_deletion_read_policy on aevo_data_deletion_requests;
create policy aevo_data_deletion_read_policy
  on aevo_data_deletion_requests
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_data_deletion_requests.organization_id
        and membership.status = 'ACTIVE'
        and current_setting('aevo.organization_id', true) = membership.organization_id::text
    )
  );

drop policy if exists aevo_data_deletion_platform_write_policy on aevo_data_deletion_requests;
create policy aevo_data_deletion_platform_write_policy
  on aevo_data_deletion_requests
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_data_deletion_requests from public, anon, authenticated;

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
  'aevo_data_deletion_requests',
  'audited-data-retention-lifecycle',
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

comment on table aevo_data_deletion_requests is
  'Core-owned deletion requests. Data remains recoverable during the retention window; only a retention worker may purge due rows.';
