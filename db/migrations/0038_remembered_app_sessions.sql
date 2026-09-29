-- Persist only the user's session preference. The opaque token remains in
-- the server-owned session store and is never moved to browser storage.

alter table aevo_app_sessions
  add column if not exists remember_me boolean not null default false;

alter table aevo_authorization_codes
  add column if not exists remember_me boolean not null default false;

comment on column aevo_app_sessions.remember_me is
  'Whether Core may issue a persistent browser cookie for this app-scoped session.';

comment on column aevo_authorization_codes.remember_me is
  'Session persistence preference copied from the authenticated source session during a one-time handoff.';
