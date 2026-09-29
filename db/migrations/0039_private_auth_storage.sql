-- Session rows and one-time handoff codes are Core-private security material.
-- Keep the database role used by Core as the only application path; the
-- Supabase Data API roles must not be able to read or write these tables.

alter table aevo_app_sessions enable row level security;
alter table aevo_authorization_codes enable row level security;

revoke all on table aevo_app_sessions from public, anon, authenticated;
revoke all on table aevo_authorization_codes from public, anon, authenticated;
