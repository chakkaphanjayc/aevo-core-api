-- Add the lifecycle fields required for an app-scoped session with both an
-- inactivity window and a hard absolute upper bound. Existing sessions keep
-- their current expiry as a safe migration baseline.

alter table aevo_app_sessions
  add column if not exists idle_expires_at timestamptz,
  add column if not exists absolute_expires_at timestamptz;

update aevo_app_sessions
set idle_expires_at = coalesce(idle_expires_at, expires_at),
    absolute_expires_at = coalesce(absolute_expires_at, expires_at)
where idle_expires_at is null
   or absolute_expires_at is null;

alter table aevo_app_sessions
  alter column idle_expires_at set not null,
  alter column absolute_expires_at set not null;

create index if not exists aevo_app_sessions_lifecycle_idx
  on aevo_app_sessions (app_code, idle_expires_at, absolute_expires_at)
  where revoked_at is null;
