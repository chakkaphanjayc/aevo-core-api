-- HUB-017: retire the Hub-owned compatibility schema after the Core cutover.
--
-- All first-party control-plane readers and writers have moved to Core API:
-- application registry/assignments, store bindings, installations, billing
-- projections, and plan entitlements.  The canonical aevo_* tables remain;
-- this migration removes only the historical public compatibility objects.
-- aevo-digital-sing is not referenced by this retirement migration.

drop trigger if exists memberships_default_hub_application on public.memberships;
drop trigger if exists stores_initialize_application_access on public.stores;

drop function if exists public.assign_default_hub_application();
drop function if exists public.initialize_store_application_access();
drop function if exists public.hub_create_store_from_template(uuid, uuid, text, text, uuid, text);
drop function if exists public.hub_organization_entitlements(uuid);
drop function if exists private.is_app_entitled(uuid, uuid, text);
drop function if exists public.hub_create_store(uuid, text, text, text, text, text, text, text, text);
drop view if exists public.admin_query_subscriptions;

-- Child tables and code tables are removed before their historical parents.
drop table if exists public.app_authorization_codes;
drop table if exists public.member_app_roles;
drop table if exists public.member_app_scopes;
drop table if exists public.member_app_assignments;
drop table if exists public.store_application_access;
drop table if exists public.app_entitlements;
drop table if exists public.app_subscriptions;
drop table if exists public.apps;
drop table if exists public.billing_customers;
drop table if exists public.billing_webhook_events;
drop table if exists public.organization_entitlements;
drop table if exists public.subscriptions;
drop table if exists public.plan_entitlements;

alter table aevo_control_plane_migration_ledger
  drop constraint if exists aevo_migration_ledger_write_mode_check,
  drop constraint if exists aevo_migration_ledger_phase_check,
  drop constraint if exists aevo_migration_ledger_state_check;

alter table aevo_control_plane_migration_ledger
  add constraint aevo_migration_ledger_write_mode_check
    check (current_write_mode in ('CORE_ONLY', 'TRANSITIONAL_CORE_API', 'READ_ONLY_COMPATIBILITY', 'APP_DOMAIN_ONLY', 'RETIRED')),
  add constraint aevo_migration_ledger_phase_check
    check (migration_phase in ('CANONICAL', 'RECONCILE', 'OBSERVE', 'APP_OWNED', 'RETIRED')),
  add constraint aevo_migration_ledger_state_check
    check (transition_state in ('CANONICAL', 'CORE_WRITER_PARTIAL', 'RECONCILE_REQUIRED', 'READ_ONLY_SOURCE', 'APP_OWNED', 'RETIRED'));

update aevo_control_plane_migration_ledger
set current_owner = 'aevo-core-api',
    target_owner = 'aevo-core-api',
    runtime_writers = '{}',
    readers = '{}',
    current_write_mode = 'RETIRED',
    migration_phase = 'RETIRED',
    transition_state = 'RETIRED',
    last_reconciled_at = now(),
    delete_after = 'Removed by Core migration 0024 after canonical projection gates passed',
    updated_at = now()
where schema_name = 'public'
  and table_name in (
    'member_app_assignments',
    'member_app_scopes',
    'member_app_roles',
    'store_application_access',
    'apps',
    'app_subscriptions',
    'billing_customers',
    'billing_webhook_events',
    'subscriptions',
    'plan_entitlements',
    'organization_entitlements'
  );

comment on table aevo_application_registry is
  'Core-owned application registry. Historical public application_registry remains only for the local app-session compatibility table until that fallback is removed.';
