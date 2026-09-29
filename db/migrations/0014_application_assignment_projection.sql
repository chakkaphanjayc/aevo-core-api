-- HUB-005/HUB-009: establish the Core-owned assignment projection.
-- The public member_app_assignments table remains a compatibility projection
-- while application writers are switched one boundary at a time.

alter table aevo_application_assignments
  add column if not exists starts_at timestamptz not null default now(),
  add column if not exists expires_at timestamptz;

create index if not exists aevo_assignments_active_scope_idx
  on aevo_application_assignments (app_code, organization_id, store_id, status, starts_at, expires_at);

do $$
begin
  if to_regclass('public.member_app_assignments') is not null
     and to_regclass('public.memberships') is not null then
    execute $migration$
      insert into aevo_application_assignments (
        user_id,
        app_code,
        organization_id,
        store_id,
        permissions,
        status,
        starts_at,
        expires_at,
        created_at,
        updated_at
      )
      select
        m.user_id,
        upper(a.application_code),
        m.organization_id,
        null,
        coalesce(array_agg(distinct rp.permission_code) filter (where rp.permission_code is not null), '{}'::text[]),
        case when a.status = 'ACTIVE' then 'active' else 'disabled' end,
        a.starts_at,
        a.expires_at,
        a.created_at,
        a.updated_at
      from public.member_app_assignments a
      join public.memberships m on m.id = a.membership_id
      left join public.role_permissions rp on rp.role_id = m.role_id
      group by m.user_id, upper(a.application_code), m.organization_id,
               a.status, a.starts_at, a.expires_at, a.created_at, a.updated_at
      on conflict (
        user_id,
        app_code,
        (coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid)),
        (coalesce(store_id, '00000000-0000-0000-0000-000000000000'::uuid))
      ) do update set
        permissions = excluded.permissions,
        status = excluded.status,
        starts_at = excluded.starts_at,
        expires_at = excluded.expires_at,
        updated_at = excluded.updated_at
    $migration$;
  end if;
end;
$$;

comment on table aevo_application_assignments is
  'Core-owned application assignment projection. Public member_app_assignments remains compatibility-only until reconciliation completes.';
