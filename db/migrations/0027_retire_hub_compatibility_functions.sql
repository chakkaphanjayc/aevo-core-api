-- HUB-017: retire the remaining function-level Hub compatibility surface.
-- The Core API now performs these reads and writes directly through its
-- canonical data stores; no application invokes the public Hub helper RPCs.

drop function if exists public.hub_create_organization(uuid, text, text, text, text, text, text, text, text, text, text);
drop function if exists public.hub_organization_overview_metrics(uuid, timestamptz, timestamptz);
drop function if exists public.hub_store_overview_metrics(uuid, uuid, timestamptz);
drop function if exists public.hub_user_navigation_favorites(uuid);
drop function if exists public.hub_user_organizations(uuid);

comment on table aevo_application_registry is
  'Core-owned application registry. Historical public application_registry and app-session compatibility objects are retired.';
