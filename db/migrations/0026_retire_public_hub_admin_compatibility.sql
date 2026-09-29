-- HUB-017: remove the remaining Hub-owned public admin/billing compatibility
-- objects.  Platform roles, plans, usage counters, and impersonation state
-- are not runtime dependencies; Core owns the privileged role/session and
-- entitlement projections instead.

drop view if exists public.admin_query_organizations;
drop view if exists public.admin_query_stores;
drop view if exists public.admin_query_users;

drop table if exists public.impersonation_sessions;
drop table if exists public.usage_counters;
drop table if exists public.platform_users;
drop table if exists public.platform_roles;
drop table if exists public.plans;
drop table if exists public.system_settings;

drop function if exists public.hub_user_navigation_favorites(uuid);
drop function if exists public.hub_user_organizations(uuid);

comment on table aevo_platform_roles is
  'Core-owned privileged platform role assignments. Hub public platform role compatibility has been retired.';
