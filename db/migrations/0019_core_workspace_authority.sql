-- HUB-017: move the shared workspace schema primitives out of Hub migrations.
--
-- Organizations and stores remain public domain tables for the current
-- application data model, but the columns and template snapshot used by the
-- Core-owned control plane are installed by the Core migration authority.
-- This file is deliberately ordered before the installation/billing
-- projections and before 0023 creates the canonical template function.

alter table if exists public.organizations
  add column if not exists legal_name text,
  add column if not exists business_type text default 'GENERAL',
  add column if not exists currency text default 'THB',
  add column if not exists logo_url text,
  add column if not exists contact_email text,
  add column if not exists contact_phone text,
  add column if not exists timezone text not null default 'Asia/Bangkok',
  add column if not exists country text not null default 'TH',
  add column if not exists onboarding_status text default 'COMPLETED',
  add column if not exists owner_user_id uuid references auth.users(id) on delete set null;

alter table if exists public.stores
  add column if not exists store_mode text default 'POS',
  add column if not exists address text,
  add column if not exists phone text,
  add column if not exists tax_id text;

create table if not exists public.store_templates (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete cascade,
  name text not null check (length(trim(name)) between 2 and 120),
  source_store_id uuid,
  created_by uuid not null references auth.users(id) on delete restrict,
  snapshot jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default timezone('utc', now()),
  updated_at timestamptz not null default timezone('utc', now()),
  unique (organization_id, id),
  unique (organization_id, name),
  foreign key (organization_id, source_store_id)
    references public.stores(organization_id, id)
    on delete set null (source_store_id)
);

create index if not exists store_templates_org_created_idx
  on public.store_templates (organization_id, created_at desc);

drop trigger if exists store_templates_set_updated_at on public.store_templates;
create trigger store_templates_set_updated_at
before update on public.store_templates
for each row execute function public.set_updated_at();

alter table public.store_templates enable row level security;
revoke all on table public.store_templates from public, anon, authenticated;
grant all on table public.store_templates to service_role;

comment on table public.store_templates is
  'Core-owned organization store template snapshots. Applications consume this through Core API; Hub does not own the schema or mutation function.';
