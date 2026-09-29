-- MAP-006 through MAP-010 additive workflow foundations.
-- These tables hold links, claims, submissions, and redirects. They do not
-- rewrite existing Store/TraceDee identities or publish pending changes.

create table if not exists aevo_place_relationships (
  relationship_id uuid primary key default gen_random_uuid(),
  place_id uuid not null references aevo_place_registry(place_id),
  relationship_type text not null,
  organization_id uuid,
  business_id uuid,
  branch_id uuid,
  store_id uuid,
  venue_id uuid,
  is_primary boolean not null default false,
  status text not null default 'proposed',
  verification_status text not null default 'unverified',
  source_kind text not null default 'aevo_admin',
  source_id text,
  created_by uuid,
  approved_by uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  revoked_at timestamptz,
  constraint aevo_place_relationship_type_check check (relationship_type in ('business', 'branch', 'store', 'venue')),
  constraint aevo_place_relationship_status_check check (status in ('proposed', 'active', 'revoked')),
  constraint aevo_place_relationship_verification_check check (verification_status in ('unverified', 'pending', 'verified', 'revoked')),
  constraint aevo_place_relationship_source_check check (source_kind in ('aevo_admin', 'verified_business', 'community', 'booking_domain', 'derived')),
  constraint aevo_place_relationship_target_check check (
    (business_id is not null)::integer
    + (branch_id is not null)::integer
    + (store_id is not null)::integer
    + (venue_id is not null)::integer = 1
  )
);
create index if not exists aevo_place_relationship_place_idx
  on aevo_place_relationships (place_id, status, relationship_type);
create index if not exists aevo_place_relationship_org_idx
  on aevo_place_relationships (organization_id, status) where organization_id is not null;
create unique index if not exists aevo_place_relationship_primary_branch_idx
  on aevo_place_relationships (branch_id) where branch_id is not null and is_primary and status = 'active';
create unique index if not exists aevo_place_relationship_primary_store_idx
  on aevo_place_relationships (store_id) where store_id is not null and is_primary and status = 'active';

create table if not exists aevo_place_redirects (
  source_place_id uuid primary key references aevo_place_registry(place_id),
  target_place_id uuid not null references aevo_place_registry(place_id),
  status text not null default 'active',
  reason text not null,
  evidence jsonb not null default '{}'::jsonb,
  created_by uuid,
  created_at timestamptz not null default now(),
  restored_at timestamptz,
  constraint aevo_place_redirect_status_check check (status in ('active', 'restored', 'revoked')),
  constraint aevo_place_redirect_no_self_check check (source_place_id <> target_place_id)
);
create index if not exists aevo_place_redirect_target_idx
  on aevo_place_redirects (target_place_id, status);

create table if not exists aevo_place_claims (
  claim_id uuid primary key default gen_random_uuid(),
  place_id uuid not null references aevo_place_registry(place_id),
  organization_id uuid not null,
  business_id uuid,
  branch_id uuid,
  requested_fields jsonb not null default '[]'::jsonb,
  status text not null default 'pending',
  evidence_status text not null default 'private_pending',
  submitted_by uuid not null,
  reviewer_id uuid,
  review_reason text,
  submitted_at timestamptz not null default now(),
  reviewed_at timestamptz,
  expires_at timestamptz,
  constraint aevo_place_claim_status_check check (status in ('pending', 'approved', 'rejected', 'needs_info', 'revoked', 'expired')),
  constraint aevo_place_claim_evidence_status_check check (evidence_status in ('private_pending', 'accepted', 'rejected', 'withdrawn')),
  constraint aevo_place_claim_target_check check (business_id is not null or branch_id is not null)
);
create index if not exists aevo_place_claim_place_status_idx
  on aevo_place_claims (place_id, status, submitted_at desc);
create index if not exists aevo_place_claim_org_status_idx
  on aevo_place_claims (organization_id, status, submitted_at desc);

create table if not exists aevo_place_claim_evidence (
  evidence_id uuid primary key default gen_random_uuid(),
  claim_id uuid not null references aevo_place_claims(claim_id),
  evidence_type text not null,
  storage_ref text,
  private_payload jsonb not null default '{}'::jsonb,
  submitted_by uuid not null,
  created_at timestamptz not null default now(),
  revoked_at timestamptz,
  constraint aevo_place_claim_evidence_type_check check (evidence_type in ('business_registration', 'domain_control', 'phone_control', 'address_document', 'other'))
);
create index if not exists aevo_place_claim_evidence_claim_idx
  on aevo_place_claim_evidence (claim_id, created_at desc);

create table if not exists aevo_place_submissions (
  submission_id uuid primary key default gen_random_uuid(),
  place_id uuid references aevo_place_registry(place_id),
  submission_type text not null,
  status text not null default 'pending',
  proposed_changes jsonb not null default '{}'::jsonb,
  evidence jsonb not null default '{}'::jsonb,
  submitter_id uuid not null,
  idempotency_key text not null,
  reviewer_id uuid,
  review_reason text,
  submitted_at timestamptz not null default now(),
  reviewed_at timestamptz,
  applied_revision bigint,
  constraint aevo_place_submission_type_check check (submission_type in ('new_place', 'edit', 'duplicate', 'closure', 'report')),
  constraint aevo_place_submission_status_check check (status in ('pending', 'needs_info', 'approved', 'rejected', 'withdrawn', 'applied')),
  constraint aevo_place_submission_key_check check (char_length(trim(idempotency_key)) between 8 and 200)
);
create unique index if not exists aevo_place_submission_idempotency_idx
  on aevo_place_submissions (submitter_id, idempotency_key);
create index if not exists aevo_place_submission_review_idx
  on aevo_place_submissions (status, submitted_at desc);
create index if not exists aevo_place_submission_place_idx
  on aevo_place_submissions (place_id, status, submitted_at desc) where place_id is not null;

create table if not exists aevo_place_workflow_events (
  event_id uuid primary key default gen_random_uuid(),
  aggregate_type text not null,
  aggregate_id uuid not null,
  event_type text not null,
  actor_id uuid,
  request_id text,
  reason text,
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  constraint aevo_place_workflow_aggregate_check check (aggregate_type in ('relationship', 'redirect', 'claim', 'submission'))
);
create index if not exists aevo_place_workflow_aggregate_idx
  on aevo_place_workflow_events (aggregate_type, aggregate_id, created_at desc);

create or replace function aevo_place_workflow_events_immutable()
returns trigger language plpgsql as $$
begin
  raise exception 'Place workflow events are immutable';
end;
$$;

do $$
begin
  if not exists (
    select 1 from pg_trigger
    where tgname = 'aevo_place_workflow_events_immutable_trigger'
      and tgrelid = 'aevo_place_workflow_events'::regclass
  ) then
    create trigger aevo_place_workflow_events_immutable_trigger
      before update or delete on aevo_place_workflow_events
      for each row execute function aevo_place_workflow_events_immutable();
  end if;
end;
$$;

alter table aevo_place_relationships enable row level security;
alter table aevo_place_redirects enable row level security;
alter table aevo_place_claims enable row level security;
alter table aevo_place_claim_evidence enable row level security;
alter table aevo_place_submissions enable row level security;
alter table aevo_place_workflow_events enable row level security;

do $$
declare
  table_name text;
begin
  foreach table_name in array array[
    'aevo_place_relationships',
    'aevo_place_redirects',
    'aevo_place_claims',
    'aevo_place_claim_evidence',
    'aevo_place_submissions',
    'aevo_place_workflow_events'
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
