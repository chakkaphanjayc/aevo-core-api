-- Core owns first-party application handoffs.  Codes are short-lived,
-- single-use credentials and are never returned with an identity-provider
-- bearer token.

create table if not exists aevo_authorization_codes (
  id uuid primary key default gen_random_uuid(),
  code_hash text not null unique,
  user_id uuid not null references aevo_identity_users(id) on delete cascade,
  app_code text not null references aevo_application_registry(code) on update cascade on delete restrict,
  organization_id uuid,
  store_id uuid,
  return_path text not null,
  state_hash text,
  code_challenge text,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  consumed_at timestamptz,
  user_agent text,
  ip_address text,
  constraint aevo_authorization_codes_app_check check (app_code in ('GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN')),
  constraint aevo_authorization_codes_return_path_check check (return_path like '/%' and return_path not like '//%'),
  constraint aevo_authorization_codes_expiry_check check (expires_at > created_at)
);

create index if not exists aevo_authorization_codes_active_idx
  on aevo_authorization_codes (app_code, expires_at, consumed_at)
  where consumed_at is null;

create index if not exists aevo_authorization_codes_user_idx
  on aevo_authorization_codes (user_id, app_code, expires_at);

comment on table aevo_authorization_codes is
  'Single-use first-party application handoffs owned by Core API.';
