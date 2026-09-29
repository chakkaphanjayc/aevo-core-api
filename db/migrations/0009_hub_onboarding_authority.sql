-- Core owns the shared Hub onboarding state.  The public tables referenced by
-- this compatibility boundary are still present during the Supabase-to-Core
-- data transition, but the schema change is now applied by the Core
-- migration-only job rather than by aevo-hub.

create table if not exists public.onboarding_sessions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.user_profiles(id) on delete cascade,
  organization_id uuid references public.organizations(id) on delete set null,
  store_id uuid references public.stores(id) on delete set null,
  current_step text not null default 'REGISTER',
  objectives jsonb not null default '[]'::jsonb,
  completed_steps jsonb not null default '[]'::jsonb,
  is_completed boolean not null default false,
  created_at timestamptz not null default timezone('utc', now()),
  updated_at timestamptz not null default timezone('utc', now())
);

alter table public.onboarding_sessions
  add column if not exists current_step text not null default 'REGISTER',
  add column if not exists objectives jsonb not null default '[]'::jsonb,
  add column if not exists completed_steps jsonb not null default '[]'::jsonb,
  add column if not exists is_completed boolean not null default false,
  add column if not exists created_at timestamptz not null default timezone('utc', now()),
  add column if not exists updated_at timestamptz not null default timezone('utc', now());

create index if not exists onboarding_sessions_user_created_idx
  on public.onboarding_sessions (user_id, created_at desc);

alter table public.onboarding_sessions enable row level security;

comment on table public.onboarding_sessions is
  'Shared Hub onboarding state. Schema ownership belongs to the Core API migration set.';
