-- HUB-007: declarative, versioned application manifest metadata.
--
-- The registry remains the immutable application-code authority. This
-- migration only adds descriptive metadata and safe launch/protocol hints;
-- it does not grant entitlements, assignments, or permissions. Origins that
-- vary by environment remain in aevo_application_connections.base_url.

alter table aevo_application_registry
  add column if not exists manifest_version text,
  add column if not exists owner_repository text,
  add column if not exists contract_version text,
  add column if not exists audience text,
  add column if not exists install_scope text,
  add column if not exists store_scoped boolean,
  add column if not exists launch_path text,
  add column if not exists capabilities jsonb,
  add column if not exists config_schema_refs jsonb,
  add column if not exists lifecycle_status text;

update aevo_application_registry
set manifest_version = coalesce(nullif(trim(manifest_version), ''), 'v1'),
    owner_repository = coalesce(nullif(trim(owner_repository), ''), 'unknown'),
    contract_version = coalesce(nullif(trim(contract_version), ''), 'v1'),
    audience = coalesce(nullif(trim(audience), ''), lower(code)),
    install_scope = coalesce(nullif(trim(install_scope), ''), 'ORGANIZATION'),
    store_scoped = coalesce(store_scoped, false),
    launch_path = coalesce(nullif(trim(launch_path), ''), '/'),
    capabilities = case when jsonb_typeof(capabilities) = 'array' then capabilities else '[]'::jsonb end,
    config_schema_refs = case when jsonb_typeof(config_schema_refs) = 'array' then config_schema_refs else '[]'::jsonb end,
    lifecycle_status = coalesce(nullif(trim(lifecycle_status), ''), case when status = 'DISABLED' then 'DEPRECATED' else 'ACTIVE' end),
    updated_at = now();

alter table aevo_application_registry
  alter column manifest_version set default 'v1',
  alter column manifest_version set not null,
  alter column owner_repository set default 'unknown',
  alter column owner_repository set not null,
  alter column contract_version set default 'v1',
  alter column contract_version set not null,
  alter column audience set not null,
  alter column install_scope set default 'ORGANIZATION',
  alter column install_scope set not null,
  alter column store_scoped set default false,
  alter column store_scoped set not null,
  alter column launch_path set default '/',
  alter column launch_path set not null,
  alter column capabilities set default '[]'::jsonb,
  alter column capabilities set not null,
  alter column config_schema_refs set default '[]'::jsonb,
  alter column config_schema_refs set not null,
  alter column lifecycle_status set default 'ACTIVE',
  alter column lifecycle_status set not null;

alter table aevo_application_registry
  drop constraint if exists aevo_application_registry_manifest_version_check,
  drop constraint if exists aevo_application_registry_install_scope_check,
  drop constraint if exists aevo_application_registry_lifecycle_status_check,
  drop constraint if exists aevo_application_registry_manifest_arrays_check,
  add constraint aevo_application_registry_manifest_version_check
    check (manifest_version ~ '^v[0-9]+$'),
  add constraint aevo_application_registry_install_scope_check
    check (install_scope in ('ORGANIZATION', 'STORE', 'USER')),
  add constraint aevo_application_registry_lifecycle_status_check
    check (lifecycle_status in ('ACTIVE', 'BETA', 'DEPRECATED', 'RETIRED')),
  add constraint aevo_application_registry_manifest_arrays_check
    check (jsonb_typeof(capabilities) = 'array' and jsonb_typeof(config_schema_refs) = 'array');

update aevo_application_registry
set owner_repository = manifest.owner_repository,
    contract_version = manifest.contract_version,
    audience = manifest.audience,
    install_scope = manifest.install_scope,
    store_scoped = manifest.store_scoped,
    launch_path = manifest.launch_path,
    capabilities = manifest.capabilities::jsonb,
    config_schema_refs = manifest.config_schema_refs::jsonb,
    lifecycle_status = manifest.lifecycle_status,
    updated_at = now()
from (
  values
    ('HUB', 'aevo-hub', 'v1', 'aevo-hub', 'ORGANIZATION', false, '/modern', '["organization.workspace", "store.workspace", "member.access"]', '[]', 'ACTIVE'),
    ('ADMIN', 'aevo-admin', 'v1', 'aevo-admin', 'ORGANIZATION', false, '/', '["platform.registry", "platform.audit", "platform.operations"]', '[]', 'ACTIVE'),
    ('GO', 'aevo-go', 'v1', 'aevo-go', 'USER', false, '/', '["discovery", "booking", "tracedee", "customer.profile"]', '[]', 'ACTIVE'),
    ('PLAY', 'aevo-play', 'v1', 'aevo-play', 'STORE', true, '/modern', '["booking", "availability", "venue", "customer.booking"]', '["booking.v1"]', 'ACTIVE'),
    ('POS', 'aevo-pos', 'v1', 'aevo-pos', 'STORE', true, '/', '["catalog", "orders", "payments", "cash.sessions", "devices"]', '["pos.catalog.v1", "pos.orders.v1"]', 'ACTIVE'),
    ('KIOSK', 'aevo-pos', 'v1', 'aevo-kiosk', 'STORE', true, '/', '["catalog", "self.service", "devices"]', '["pos.catalog.v1"]', 'BETA'),
    ('QUEUE', 'aevo-pos', 'v1', 'aevo-queue', 'STORE', true, '/', '["queue", "display", "devices"]', '["pos.queue.v1"]', 'BETA'),
    ('DIGITAL_SIGN', 'aevo-digital-sing', 'v1', 'aevo-digital-sign', 'STORE', true, '/', '["digital.signage"]', '["digital-sign.v1"]', 'ACTIVE')
) as manifest(code, owner_repository, contract_version, audience, install_scope, store_scoped, launch_path, capabilities, config_schema_refs, lifecycle_status)
where aevo_application_registry.code = manifest.code;

comment on column aevo_application_registry.capabilities is
  'Declarative capability identifiers only; this metadata never grants access.';
comment on column aevo_application_registry.config_schema_refs is
  'Versioned app-owned configuration schema references; secret values are prohibited.';
comment on column aevo_application_registry.audience is
  'Server-resolved launch/handoff audience, not a browser-controlled redirect target.';
