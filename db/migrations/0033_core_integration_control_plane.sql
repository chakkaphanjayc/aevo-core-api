-- HUB-016: the first concrete integration lifecycle.  Core stores only the
-- manifest and a reference to a secret held by the deployment platform; it
-- never receives or returns a LINE token value through the Hub browser.

create table if not exists aevo_integration_manifests (
  provider_code text primary key,
  display_name text not null,
  description text not null,
  owner_repository text not null,
  manifest_version text not null,
  scopes text[] not null default '{}',
  secret_ref_required boolean not null default true,
  lifecycle_status text not null default 'ACTIVE',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_integration_manifest_status_check
    check (lifecycle_status in ('ACTIVE', 'BETA', 'DEPRECATED', 'RETIRED'))
);

create table if not exists aevo_organization_integrations (
  organization_id uuid not null references public.organizations(id) on delete cascade,
  store_id uuid,
  scope_key text generated always as (organization_id::text || ':' || coalesce(store_id::text, 'organization')) stored,
  provider_code text not null references aevo_integration_manifests(provider_code) on update cascade on delete restrict,
  status text not null default 'ACTIVE',
  secret_ref text,
  consented_at timestamptz,
  consented_by uuid references aevo_identity_users(id) on delete set null,
  connected_at timestamptz,
  revoked_at timestamptz,
  last_error_code text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (scope_key, provider_code),
  constraint aevo_organization_integration_store_fk
    foreign key (organization_id, store_id)
    references public.stores(organization_id, id)
    on delete cascade,
  constraint aevo_organization_integration_status_check
    check (status in ('ACTIVE', 'REVOKED', 'ERROR')),
  constraint aevo_organization_integration_secret_ref_check
    check (secret_ref is null or (length(secret_ref) between 1 and 256 and secret_ref !~ '[[:space:]]'))
);

create index if not exists aevo_organization_integrations_org_idx
  on aevo_organization_integrations (organization_id, provider_code, status, updated_at desc);

alter table aevo_integration_manifests enable row level security;
alter table aevo_organization_integrations enable row level security;

drop policy if exists aevo_integration_manifest_platform_read_policy on aevo_integration_manifests;
create policy aevo_integration_manifest_platform_read_policy
  on aevo_integration_manifests
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));

drop policy if exists aevo_organization_integrations_read_policy on aevo_organization_integrations;
create policy aevo_organization_integrations_read_policy
  on aevo_organization_integrations
  for select
  using (
    current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support')
    or exists (
      select 1
      from public.memberships membership
      where membership.user_id::text = current_setting('aevo.user_id', true)
        and membership.organization_id = aevo_organization_integrations.organization_id
        and membership.status = 'ACTIVE'
        and (
          current_setting('aevo.organization_id', true) = ''
          or current_setting('aevo.organization_id', true) = aevo_organization_integrations.organization_id::text
        )
        and (
          aevo_organization_integrations.store_id is null
          or current_setting('aevo.store_id', true) = ''
          or current_setting('aevo.store_id', true) = aevo_organization_integrations.store_id::text
        )
    )
  );

drop policy if exists aevo_organization_integrations_platform_write_policy on aevo_organization_integrations;
create policy aevo_organization_integrations_platform_write_policy
  on aevo_organization_integrations
  for all
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'))
  with check (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin'));

revoke all on table aevo_integration_manifests from public, anon, authenticated;
revoke all on table aevo_organization_integrations from public, anon, authenticated;

insert into aevo_integration_manifests (
  provider_code,
  display_name,
  description,
  owner_repository,
  manifest_version,
  scopes,
  secret_ref_required,
  lifecycle_status
)
values (
  'LINE_OFFICIAL_ACCOUNT',
  'LINE Official Account',
  'Send tenant-approved customer notifications through the isolated LINE adapter.',
  'aevo-pos',
  'v1',
  '{messages:write,webhook:read}',
  true,
  'BETA'
)
on conflict (provider_code) do update set
  display_name = excluded.display_name,
  description = excluded.description,
  owner_repository = excluded.owner_repository,
  manifest_version = excluded.manifest_version,
  scopes = excluded.scopes,
  secret_ref_required = excluded.secret_ref_required,
  lifecycle_status = excluded.lifecycle_status,
  updated_at = now();

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
    'aevo_integration_manifests',
    'integration-manifest',
    '{aevo-hub}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api}',
    '{aevo-core-api,aevo-hub,aevo-admin}',
    'CORE_ONLY',
    'CANONICAL',
    'CANONICAL',
    now(),
    null
  ),
  (
    'aevo',
    'aevo_organization_integrations',
    'organization-integration-lifecycle',
    '{aevo-hub}',
    'aevo-core-api',
    'aevo-core-api',
    '{aevo-core-api,aevo-hub,aevo-admin}',
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

comment on table aevo_integration_manifests is
  'Core-owned versioned integration manifests; adapters remain in the owning application.';
comment on table aevo_organization_integrations is
  'Tenant-scoped integration lifecycle. secret_ref is a deployment secret reference, never a credential value.';
