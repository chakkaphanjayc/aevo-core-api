-- Keep the Core authorization boundary aligned with the Hub permission contract.
-- These permissions were present in the application contract but were missing
-- from some older Hub databases, which caused valid owner actions to fail closed.
insert into public.permissions (code, description)
values
  ('store.create', 'Create stores'),
  ('store.delete', 'Deactivate stores')
on conflict (code) do update set description = excluded.description;

insert into public.role_permissions (role_id, permission_code)
select r.id, p.code
from public.roles r
cross join public.permissions p
where r.code in ('OWNER', 'ADMIN')
  and p.code in ('store.create', 'store.delete')
on conflict do nothing;
