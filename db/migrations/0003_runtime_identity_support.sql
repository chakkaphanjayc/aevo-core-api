-- Runtime support for the Core API adapter. Identity Platform remains the
-- authentication provider; this table is only the server-side projection
-- required to resolve an opaque app session into a safe user summary.

create table if not exists aevo_identity_users (
  id uuid primary key,
  email text not null,
  display_name text,
  status text not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint aevo_identity_users_status_check check (status in ('active', 'disabled'))
);

create unique index if not exists aevo_identity_users_email_idx
  on aevo_identity_users (lower(email));

-- Accounts owns issuing app-scoped sessions. The Core API only verifies the
-- hash and application binding. A nullable CSRF hash keeps the migration
-- additive for sessions created before the CSRF field was introduced.
alter table aevo_app_sessions
  add column if not exists csrf_token_hash text;

create index if not exists aevo_app_sessions_user_app_idx
  on aevo_app_sessions (user_id, app_code, expires_at)
  where revoked_at is null;

alter table aevo_identity_users enable row level security;

create policy aevo_identity_users_platform_read_policy on aevo_identity_users
  for select
  using (current_setting('aevo.platform_role', true) in ('platform_owner', 'platform_admin', 'platform_support'));
