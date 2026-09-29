-- HUB-005: record the development reconciliation checkpoint after the Core
-- assignment projection and store-scope projection were backfilled and the
-- Hub writer was switched to Core-first dual-write.

update aevo_control_plane_migration_ledger
set migration_phase = 'CANONICAL',
    transition_state = 'CANONICAL',
    last_reconciled_at = now(),
    updated_at = now()
where schema_name = 'aevo'
  and table_name = 'aevo_application_assignments';

update aevo_control_plane_migration_ledger
set migration_phase = 'OBSERVE',
    transition_state = 'RECONCILE_REQUIRED',
    last_reconciled_at = now(),
    updated_at = now(),
    delete_after = 'After POS/Play readers migrate to aevo_application_assignments and compatibility projection is retired'
where schema_name = 'public'
  and table_name = 'member_app_assignments';
