-- HUB-013: keep password-recovery provider credentials on the Core side.
-- The browser receives only a one-time opaque grant cookie.  The provider
-- access token is encrypted by Core and consumed once after CSRF validation.

create table if not exists aevo_password_recovery_grants (
  grant_hash text primary key,
  user_id uuid not null references aevo_identity_users(id) on delete cascade,
  email text not null,
  provider_access_token_ciphertext text not null,
  expires_at timestamptz not null,
  consumed_at timestamptz,
  request_id text,
  created_at timestamptz not null default now(),
  constraint aevo_password_recovery_grant_email_check
    check (email ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'),
  constraint aevo_password_recovery_grant_expiry_check
    check (expires_at > created_at)
);

create index if not exists aevo_password_recovery_grants_expiry_idx
  on aevo_password_recovery_grants (expires_at)
  where consumed_at is null;

revoke all on table aevo_password_recovery_grants from public, anon, authenticated;

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
  'aevo_password_recovery_grants',
  'password-recovery-session-boundary',
  '{aevo-core-api}',
  'aevo-core-api',
  'aevo-core-api',
  '{aevo-core-api}',
  '{aevo-core-api}',
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

comment on table aevo_password_recovery_grants is
  'HUB-013 one-time server-side password recovery grants; provider credentials are encrypted and never returned to browser code.';
