-- HUB-005: machine-readable authority ledger for the shared control plane.
-- This migration records the transition; it intentionally does not copy or
-- delete application data. Legacy public tables remain read-only/compatibility
-- sources until reconciliation and restore evidence have been recorded.

create table if not exists aevo_control_plane_migration_ledger (
  schema_name text not null,
  table_name text not null,
  semantic_domain text not null,
  historical_migration_repos text[] not null default '{}',
  current_owner text not null,
  target_owner text not null,
  runtime_writers text[] not null default '{}',
  readers text[] not null default '{}',
  current_write_mode text not null,
  migration_phase text not null,
  transition_state text not null,
  last_reconciled_at timestamptz,
  updated_at timestamptz not null default now(),
  delete_after text,
  primary key (schema_name, table_name),
  constraint aevo_migration_ledger_write_mode_check
    check (current_write_mode in ('CORE_ONLY', 'TRANSITIONAL_CORE_API', 'READ_ONLY_COMPATIBILITY', 'APP_DOMAIN_ONLY')),
  constraint aevo_migration_ledger_phase_check
    check (migration_phase in ('CANONICAL', 'RECONCILE', 'OBSERVE', 'APP_OWNED')),
  constraint aevo_migration_ledger_state_check
    check (transition_state in ('CANONICAL', 'CORE_WRITER_PARTIAL', 'RECONCILE_REQUIRED', 'READ_ONLY_SOURCE', 'APP_OWNED'))
);

create index if not exists aevo_migration_ledger_phase_idx
  on aevo_control_plane_migration_ledger (migration_phase, transition_state, schema_name, table_name);

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
  delete_after
)
values
  ('aevo', 'aevo_app_sessions', 'app-scoped-session', '{aevo-core-api}', 'aevo-core-api', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-accounts,aevo-pos,aevo-play,aevo-go}', 'CORE_ONLY', 'CANONICAL', 'CANONICAL', null),
  ('aevo', 'aevo_application_registry', 'application-registry', '{aevo-core-api}', 'aevo-core-api', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-admin,aevo-background-worker}', 'CORE_ONLY', 'CANONICAL', 'CANONICAL', null),
  ('aevo', 'aevo_application_assignments', 'application-assignment', '{aevo-core-api}', 'aevo-core-api', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api}', 'CORE_ONLY', 'RECONCILE', 'RECONCILE_REQUIRED', null),
  ('public', 'organizations', 'organization-control-plane', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play,aevo-go}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After Core organization projection and restore evidence'),
  ('public', 'stores', 'store-location-control-plane', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play,aevo-go}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After Core store projection and restore evidence'),
  ('public', 'memberships', 'organization-membership', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After member lifecycle cutover'),
  ('public', 'membership_stores', 'member-store-scope', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After member-store scope cutover'),
  ('public', 'application_registry', 'legacy-application-registry', '{aevo-hub,aevo-pos,aevo-play}', 'historical app migrations', 'aevo-core-api', '{none after bootstrap}', '{aevo-core-api transitional readers}', 'READ_ONLY_COMPATIBILITY', 'OBSERVE', 'READ_ONLY_SOURCE', 'After all consumers use aevo_application_registry'),
  ('public', 'member_app_assignments', 'application-assignment', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After aevo_application_assignments reconciliation'),
  ('public', 'store_application_access', 'store-app-binding', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos,aevo-play}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After installation/binding projection cutover'),
  ('public', 'subscriptions', 'billing-entitlement', '{aevo-hub,aevo-pos,aevo-play}', 'historical billing projection', 'aevo-core-api', '{aevo-core-api onboarding compatibility path,billing projection pending HUB-010}', '{aevo-core-api,aevo-admin,aevo-hub}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After verified billing projection'),
  ('public', 'organization_entitlements', 'entitlement-projection', '{aevo-hub,aevo-pos,aevo-play}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api onboarding compatibility path,billing projection pending HUB-010}', '{aevo-core-api,aevo-hub,aevo-pos,aevo-play}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After entitlement projection cutover'),
  ('public', 'onboarding_sessions', 'hub-onboarding', '{aevo-hub}', 'aevo-core-api', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-hub}', 'TRANSITIONAL_CORE_API', 'CANONICAL', 'CORE_WRITER_PARTIAL', 'After replacement by Core-native onboarding projection'),
  ('public', 'devices', 'device-identity', '{aevo-hub,aevo-pos}', 'aevo-core-api transitional compatibility boundary', 'aevo-core-api', '{aevo-core-api}', '{aevo-core-api,aevo-pos}', 'TRANSITIONAL_CORE_API', 'RECONCILE', 'RECONCILE_REQUIRED', 'After device identity projection cutover'),
  ('public', 'products', 'pos-catalog', '{aevo-pos}', 'aevo-pos', 'aevo-pos', '{aevo-pos}', '{aevo-pos,aevo-core-api compatibility reads}', 'APP_DOMAIN_ONLY', 'APP_OWNED', 'APP_OWNED', null),
  ('public', 'venues', 'play-booking', '{aevo-play}', 'aevo-play', 'aevo-play', '{aevo-play,aevo-core-api onboarding compatibility path}', '{aevo-play,aevo-core-api onboarding compatibility reads}', 'APP_DOMAIN_ONLY', 'APP_OWNED', 'APP_OWNED', null)
on conflict (schema_name, table_name) do update set
  semantic_domain = excluded.semantic_domain,
  historical_migration_repos = excluded.historical_migration_repos,
  current_owner = excluded.current_owner,
  target_owner = excluded.target_owner,
  runtime_writers = excluded.runtime_writers,
  readers = excluded.readers,
  current_write_mode = excluded.current_write_mode,
  migration_phase = excluded.migration_phase,
  transition_state = excluded.transition_state,
  delete_after = excluded.delete_after,
  updated_at = now();

alter table aevo_control_plane_migration_ledger enable row level security;
revoke all on table aevo_control_plane_migration_ledger from public;

comment on table aevo_control_plane_migration_ledger is
  'HUB-005 authority ledger. It records ownership and transition state; it is not a data-copy or deletion command.';
