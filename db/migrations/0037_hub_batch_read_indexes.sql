-- HUB-PERF-001: support the Core-owned Hub read paths without scanning the
-- entire assignment projection for every settings navigation.

create index if not exists aevo_assignments_org_user_app_scope_idx
  on aevo_application_assignments (organization_id, user_id, app_code, status, store_id);

create index if not exists aevo_assignments_user_app_active_idx
  on aevo_application_assignments (user_id, app_code, organization_id, status, starts_at, expires_at)
  where status = 'active';
