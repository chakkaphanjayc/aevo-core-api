-- Core now owns every application session. Remove the final local Supabase
-- session/catalog compatibility objects after Play and POS have cut over to
-- Core session resolution.

drop table if exists public.app_sessions;
drop table if exists public.application_registry;

comment on table aevo_app_sessions is
  'Core-owned opaque, app-scoped sessions. Accounts issues them and applications resolve them through Core; browser clients never receive provider bearer tokens.';
