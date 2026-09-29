-- HUB-004/HUB-009: project legacy store-scoped application grants into Core.
-- Organization-scoped rows remain authoritative when present; store-scoped
-- rows narrow a grant to the listed locations. The public scope tables remain
-- compatibility input until the remaining app readers migrate to Core.

do $$
begin
  if to_regclass('public.member_app_assignments') is not null
     and to_regclass('public.member_app_scopes') is not null
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
        s.scope_ref::uuid,
        coalesce(array_agg(distinct rp.permission_code) filter (where rp.permission_code is not null), '{}'::text[]),
        case when a.status = 'ACTIVE' then 'active' else 'disabled' end,
        a.starts_at,
        a.expires_at,
        a.created_at,
        a.updated_at
      from public.member_app_assignments a
      join public.memberships m on m.id = a.membership_id
      join public.member_app_scopes s on s.assignment_id = a.id
        and upper(s.scope_type) = 'STORE'
        and s.scope_ref ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
      left join public.role_permissions rp on rp.role_id = m.role_id
      group by m.user_id, upper(a.application_code), m.organization_id,
               s.scope_ref::uuid, a.status, a.starts_at, a.expires_at,
               a.created_at, a.updated_at
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
  'Core-owned application assignment projection, including organization and store scopes. Public member_app_assignments/member_app_scopes remain compatibility-only until reconciliation completes.';
