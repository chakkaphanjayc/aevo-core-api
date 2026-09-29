-- Align the Core-owned role permission projection with the Aevo Play contract.
-- Application assignment grants entry to Play; these role permissions grant the
-- resource capabilities Play checks after the app-scoped session is resolved.

insert into public.permissions (code, description)
values
  ('venue.read', 'View Play venues and courts'),
  ('venue.manage', 'Manage Play venues, courts and operating hours'),
  ('booking.read', 'View Play bookings and availability'),
  ('booking.create', 'Create Play booking holds'),
  ('booking.manage', 'Manage Play bookings')
on conflict (code) do update set description = excluded.description;

insert into public.role_permissions (role_id, permission_code)
select r.id, defaults.permission_code
from public.roles r
join (
  values
    ('OWNER', 'venue.read'),
    ('OWNER', 'venue.manage'),
    ('OWNER', 'booking.read'),
    ('OWNER', 'booking.create'),
    ('OWNER', 'booking.manage'),
    ('ADMIN', 'venue.read'),
    ('ADMIN', 'venue.manage'),
    ('ADMIN', 'booking.read'),
    ('ADMIN', 'booking.create'),
    ('ADMIN', 'booking.manage'),
    ('STAFF', 'venue.read'),
    ('STAFF', 'booking.read'),
    ('STAFF', 'booking.create'),
    ('STAFF', 'booking.manage'),
    ('MEMBER', 'venue.read'),
    ('MEMBER', 'booking.read'),
    ('MEMBER', 'booking.create')
) as defaults(role_code, permission_code)
  on defaults.role_code = r.code
join public.permissions p on p.code = defaults.permission_code
on conflict (role_id, permission_code) do nothing;
