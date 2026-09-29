-- MAP-002 additive canonical Place and public read projection foundation.
-- Existing Store/TraceDee rows are not rewritten by this migration.

create extension if not exists pgcrypto;

create table if not exists aevo_place_registry (
  place_id uuid primary key default gen_random_uuid(),
  slug text not null,
  name text not null,
  localized_names jsonb not null default '[]'::jsonb,
  category jsonb not null default '{}'::jsonb,
  status text not null default 'candidate',
  canonical_geometry jsonb,
  display_longitude double precision,
  display_latitude double precision,
  label_longitude double precision,
  label_latitude double precision,
  centroid_longitude double precision,
  centroid_latitude double precision,
  bounding_geometry jsonb,
  address jsonb,
  parent_place_id uuid references aevo_place_registry(place_id),
  revision bigint not null default 1,
  source_revision text not null default 'unresolved',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  published_at timestamptz,
  constraint aevo_place_registry_slug_check check (char_length(trim(slug)) between 1 and 240),
  constraint aevo_place_registry_name_check check (char_length(trim(name)) between 1 and 500),
  constraint aevo_place_registry_status_check check (status in ('candidate', 'visible', 'limited', 'under_review', 'closed', 'removed', 'merged')),
  constraint aevo_place_registry_geometry_check check (canonical_geometry is null or jsonb_typeof(canonical_geometry) = 'object'),
  constraint aevo_place_registry_bounds_check check (bounding_geometry is null or jsonb_typeof(bounding_geometry) = 'object'),
  constraint aevo_place_registry_display_longitude_check check (display_longitude is null or display_longitude between -180 and 180),
  constraint aevo_place_registry_display_latitude_check check (display_latitude is null or display_latitude between -90 and 90),
  constraint aevo_place_registry_label_longitude_check check (label_longitude is null or label_longitude between -180 and 180),
  constraint aevo_place_registry_label_latitude_check check (label_latitude is null or label_latitude between -90 and 90),
  constraint aevo_place_registry_centroid_longitude_check check (centroid_longitude is null or centroid_longitude between -180 and 180),
  constraint aevo_place_registry_centroid_latitude_check check (centroid_latitude is null or centroid_latitude between -90 and 90)
);

create unique index if not exists aevo_place_registry_active_slug_idx
  on aevo_place_registry (lower(slug)) where status not in ('removed', 'merged');
create index if not exists aevo_place_registry_display_point_idx
  on aevo_place_registry (display_latitude, display_longitude, place_id)
  where status in ('visible', 'limited') and display_latitude is not null and display_longitude is not null;
create index if not exists aevo_place_registry_parent_idx
  on aevo_place_registry (parent_place_id, status) where parent_place_id is not null;

create table if not exists aevo_place_source_links (
  source_link_id uuid primary key default gen_random_uuid(),
  place_id uuid references aevo_place_registry(place_id),
  source_kind text not null,
  namespace text not null,
  external_id text not null,
  source_version text not null default 'unversioned',
  source_url text,
  license text,
  attribution_text text,
  first_observed_at timestamptz not null default now(),
  last_observed_at timestamptz not null default now(),
  source_record_hash text,
  match_status text not null default 'unmatched',
  match_confidence numeric(5, 4),
  raw_snapshot_ref text,
  provenance jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_place_source_kind_check check (source_kind in ('aevo_admin', 'verified_business', 'community', 'booking_domain', 'osm', 'overture', 'government', 'derived')),
  constraint aevo_place_source_status_check check (match_status in ('unmatched', 'candidate', 'linked', 'rejected', 'retired')),
  constraint aevo_place_source_confidence_check check (match_confidence is null or match_confidence between 0 and 1),
  constraint aevo_place_source_id_check check (char_length(trim(external_id)) between 1 and 500)
);
create unique index if not exists aevo_place_source_identity_idx
  on aevo_place_source_links (namespace, external_id, source_version);
create index if not exists aevo_place_source_place_idx
  on aevo_place_source_links (place_id, match_status) where place_id is not null;

create table if not exists aevo_place_legacy_mappings (
  namespace text not null,
  external_id text not null,
  source_version text not null default 'unversioned',
  place_id uuid references aevo_place_registry(place_id),
  match_status text not null default 'unresolved',
  match_reason text,
  match_confidence numeric(5, 4),
  mapping_revision bigint not null default 1,
  observed_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (namespace, external_id, source_version),
  constraint aevo_place_mapping_status_check check (match_status in ('unresolved', 'candidate', 'linked', 'rejected', 'retired')),
  constraint aevo_place_mapping_confidence_check check (match_confidence is null or match_confidence between 0 and 1)
);
create index if not exists aevo_place_mapping_place_idx
  on aevo_place_legacy_mappings (place_id, match_status) where place_id is not null;

create table if not exists aevo_place_revisions (
  revision_id uuid primary key default gen_random_uuid(),
  place_id uuid not null references aevo_place_registry(place_id),
  revision_number bigint not null,
  operation text not null,
  changed_fields jsonb not null default '[]'::jsonb,
  snapshot jsonb not null,
  source_kind text not null,
  source_id text,
  evidence_reference text,
  actor_id uuid,
  created_at timestamptz not null default now(),
  unique (place_id, revision_number),
  constraint aevo_place_revision_operation_check check (operation in ('created', 'updated', 'verified', 'moderated', 'merged', 'split_compensation', 'restored')),
  constraint aevo_place_revision_source_check check (source_kind in ('aevo_admin', 'verified_business', 'community', 'booking_domain', 'osm', 'overture', 'government', 'derived'))
);
create index if not exists aevo_place_revision_place_idx
  on aevo_place_revisions (place_id, revision_number desc);

create table if not exists aevo_place_field_provenance (
  provenance_id uuid primary key default gen_random_uuid(),
  place_id uuid not null references aevo_place_registry(place_id),
  field_path text not null,
  source_kind text not null,
  source_id text,
  source_version text,
  observed_at timestamptz not null,
  approved_by uuid,
  canonical_revision bigint not null,
  method text,
  created_at timestamptz not null default now(),
  constraint aevo_place_provenance_source_check check (source_kind in ('aevo_admin', 'verified_business', 'community', 'booking_domain', 'osm', 'overture', 'government', 'derived'))
);
create index if not exists aevo_place_provenance_lookup_idx
  on aevo_place_field_provenance (place_id, field_path, canonical_revision desc);

create table if not exists aevo_place_public_projections (
  place_id uuid primary key references aevo_place_registry(place_id),
  projection_version text not null,
  source_revision text not null,
  freshness_state text not null default 'unknown',
  observed_at timestamptz,
  expires_at timestamptz,
  payload jsonb not null,
  projection_status text not null default 'active',
  last_known_valid_projection_version text,
  last_known_valid_payload jsonb,
  last_known_valid_at timestamptz,
  invalidation_reason text,
  generated_at timestamptz not null default now(),
  rebuilt_at timestamptz,
  invalidated_at timestamptz,
  constraint aevo_place_projection_freshness_check check (freshness_state in ('fresh', 'stale', 'unknown')),
  constraint aevo_place_projection_status_check check (projection_status in ('active', 'stale', 'rebuilding', 'failed', 'disabled')),
  constraint aevo_place_projection_payload_check check (jsonb_typeof(payload) = 'object')
);
create index if not exists aevo_place_projection_status_idx
  on aevo_place_public_projections (projection_status, freshness_state, generated_at desc);

create table if not exists aevo_place_projection_runs (
  run_id uuid primary key default gen_random_uuid(),
  projection_version text not null,
  source_revision text not null,
  status text not null default 'started',
  rows_seen integer not null default 0,
  rows_published integer not null default 0,
  rows_failed integer not null default 0,
  invalidation_reason text,
  last_known_valid_projection_version text,
  started_at timestamptz not null default now(),
  completed_at timestamptz,
  error_code text,
  error_message text,
  constraint aevo_place_run_status_check check (status in ('started', 'completed', 'failed', 'rolled_back', 'cancelled')),
  constraint aevo_place_run_counts_check check (rows_seen >= 0 and rows_published >= 0 and rows_failed >= 0)
);
create index if not exists aevo_place_run_time_idx
  on aevo_place_projection_runs (started_at desc, run_id desc);

create or replace function aevo_place_revision_rows_immutable()
returns trigger language plpgsql as $$
begin
  raise exception 'Place revisions are immutable';
end;
$$;

do $$
begin
  if not exists (
    select 1 from pg_trigger
    where tgname = 'aevo_place_revisions_immutable_trigger'
      and tgrelid = 'aevo_place_revisions'::regclass
  ) then
    create trigger aevo_place_revisions_immutable_trigger
      before update or delete on aevo_place_revisions
      for each row execute function aevo_place_revision_rows_immutable();
  end if;
end;
$$;

alter table aevo_place_registry enable row level security;
alter table aevo_place_source_links enable row level security;
alter table aevo_place_legacy_mappings enable row level security;
alter table aevo_place_revisions enable row level security;
alter table aevo_place_field_provenance enable row level security;
alter table aevo_place_public_projections enable row level security;
alter table aevo_place_projection_runs enable row level security;

do $$
declare
  table_name text;
begin
  foreach table_name in array array[
    'aevo_place_registry',
    'aevo_place_source_links',
    'aevo_place_legacy_mappings',
    'aevo_place_revisions',
    'aevo_place_field_provenance',
    'aevo_place_public_projections',
    'aevo_place_projection_runs'
  ] loop
    if not exists (select 1 from pg_policies where policyname = table_name || '_internal_read_policy') then
      execute format(
        'create policy %I on %I for select using (current_setting(''aevo.place_access'', true) = ''internal'' or current_setting(''aevo.platform_role'', true) in (''platform_owner'', ''platform_admin'', ''platform_support''))',
        table_name || '_internal_read_policy', table_name);
    end if;
    if not exists (select 1 from pg_policies where policyname = table_name || '_platform_write_policy') then
      execute format(
        'create policy %I on %I for all using (current_setting(''aevo.platform_role'', true) in (''platform_owner'', ''platform_admin'')) with check (current_setting(''aevo.platform_role'', true) in (''platform_owner'', ''platform_admin''))',
        table_name || '_platform_write_policy', table_name);
    end if;
  end loop;
end;
$$;
