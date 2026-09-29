-- HUB-011: make Core template instantiation copy typed app configuration.
--
-- The original function used a column-list ON CONFLICT clause inside a
-- RETURNS TABLE function. PostgreSQL resolves those names against the
-- function output variables as well as the target table, which made template
-- instantiation fail with an ambiguous-column error. Constraint names keep
-- the target unambiguous and the same function now copies validated config
-- snapshots for enabled store applications.

create or replace function public.aevo_core_create_store_from_template(
  p_organization_id uuid,
  p_template_id uuid,
  p_name text,
  p_code text,
  p_created_by uuid,
  p_public_slug text default null
)
returns table (
  id uuid,
  organization_id uuid,
  name text,
  code text,
  timezone text,
  currency text,
  store_mode text,
  address text,
  phone text,
  tax_id text,
  status text
)
language plpgsql
security definer
set search_path = pg_catalog, public
as $function$
declare
  v_template public.store_templates%rowtype;
  v_snapshot jsonb;
  v_store_settings jsonb;
  v_store public.stores%rowtype;
  v_application jsonb;
  v_configuration jsonb;
  v_availability jsonb;
  v_menu jsonb;
  v_menu_item jsonb;
  v_product_id uuid;
  v_variant_id uuid;
  v_menu_id uuid;
  v_profile jsonb;
  v_profile_slug text;
  v_application_code text;
begin
  select * into v_template
  from public.store_templates
  where store_templates.id = p_template_id
    and store_templates.organization_id = p_organization_id;

  if not found then
    raise exception using errcode = 'P0001', message = 'STORE_TEMPLATE_NOT_FOUND';
  end if;

  v_snapshot := coalesce(v_template.snapshot, '{}'::jsonb);
  v_store_settings := coalesce(v_snapshot->'store', '{}'::jsonb);

  insert into public.stores (
    organization_id,
    name,
    code,
    timezone,
    currency,
    store_mode,
    address,
    phone,
    tax_id,
    status
  ) values (
    p_organization_id,
    trim(p_name),
    upper(trim(p_code)),
    coalesce(nullif(trim(v_store_settings->>'timezone'), ''), 'Asia/Bangkok'),
    upper(coalesce(nullif(trim(v_store_settings->>'currency'), ''), 'THB')),
    coalesce(nullif(trim(v_store_settings->>'storeMode'), ''), 'POS'),
    nullif(trim(v_store_settings->>'address'), ''),
    nullif(trim(v_store_settings->>'phone'), ''),
    nullif(trim(v_store_settings->>'taxId'), ''),
    'ACTIVE'
  ) returning * into v_store;

  for v_application in
    select value from jsonb_array_elements(coalesce(v_snapshot->'applications', '[]'::jsonb)) as value
  loop
    v_application_code := upper(trim(v_application->>'applicationCode'));
    if v_application_code in ('PLAY', 'POS', 'KIOSK', 'QUEUE')
       and exists (
         select 1
         from public.aevo_application_registry registry
         where registry.code = v_application_code
           and registry.store_scoped = true
           and registry.owner_repository <> 'aevo-digital-sing'
       ) then
      insert into public.aevo_store_application_bindings (
        organization_id, store_id, app_code, status, source, projection_version, updated_by
      ) values (
        p_organization_id,
        v_store.id,
        v_application_code,
        case when v_application->>'status' = 'ACTIVE' then 'ACTIVE' else 'DISABLED' end,
        'TEMPLATE',
        'store-binding-v1',
        p_created_by
      )
      on conflict on constraint aevo_store_application_bindings_pkey do update
      set status = excluded.status,
          source = excluded.source,
          projection_version = excluded.projection_version,
          updated_by = excluded.updated_by,
          updated_at = timezone('utc', now());
    end if;
  end loop;

  for v_configuration in
    select value from jsonb_array_elements(coalesce(v_snapshot->'applicationConfigurations', '[]'::jsonb)) as value
  loop
    v_application_code := upper(trim(v_configuration->>'applicationCode'));
    if exists (
      select 1
      from public.aevo_application_registry registry
      where registry.code = v_application_code
        and registry.store_scoped = true
        and registry.owner_repository <> 'aevo-digital-sing'
    ) and exists (
      select 1
      from public.aevo_store_application_bindings binding
      where binding.organization_id = p_organization_id
        and binding.store_id = v_store.id
        and binding.app_code = v_application_code
        and binding.status = 'ACTIVE'
    ) then
      insert into public.aevo_store_application_configurations (
        organization_id, store_id, app_code, schema_ref, schema_version, config, updated_by
      ) values (
        p_organization_id,
        v_store.id,
        v_application_code,
        v_configuration->>'schemaRef',
        v_configuration->>'schemaVersion',
        coalesce(v_configuration->'config', '{}'::jsonb),
        p_created_by
      )
      on conflict on constraint aevo_store_application_configurations_pkey do update
      set schema_version = excluded.schema_version,
          config = excluded.config,
          updated_by = excluded.updated_by,
          updated_at = timezone('utc', now());
    end if;
  end loop;

  for v_availability in
    select value from jsonb_array_elements(coalesce(v_snapshot->'availability', '[]'::jsonb)) as value
  loop
    select products.id into v_product_id
    from public.products
    where products.organization_id = p_organization_id
      and products.sku = upper(trim(v_availability->>'sku'));
    if v_product_id is null then
      raise exception using errcode = 'P0001', message = 'STORE_TEMPLATE_PRODUCT_MISSING';
    end if;

    insert into public.product_availability (
      organization_id, store_id, product_id, channel, is_available, sold_out, price_override_minor
    ) values (
      p_organization_id,
      v_store.id,
      v_product_id,
      coalesce(v_availability->>'channel', 'POS'),
      coalesce((v_availability->>'isAvailable')::boolean, true),
      coalesce((v_availability->>'soldOut')::boolean, false),
      (v_availability->>'priceOverrideMinor')::integer
    )
    on conflict on constraint product_availability_pkey do update
    set is_available = excluded.is_available,
        sold_out = excluded.sold_out,
        price_override_minor = excluded.price_override_minor,
        updated_at = timezone('utc', now());
  end loop;

  for v_menu in
    select value from jsonb_array_elements(coalesce(v_snapshot->'menus', '[]'::jsonb)) as value
  loop
    insert into public.menus (
      organization_id, store_id, code, name, channel, status
    ) values (
      p_organization_id,
      v_store.id,
      upper(trim(v_menu->>'code')),
      trim(v_menu->>'name'),
      coalesce(v_menu->>'channel', 'POS'),
      case when v_menu->>'status' = 'INACTIVE' then 'INACTIVE' else 'ACTIVE' end
    ) returning menus.id into v_menu_id;

    for v_menu_item in
      select value from jsonb_array_elements(coalesce(v_menu->'items', '[]'::jsonb)) as value
    loop
      select products.id into v_product_id
      from public.products
      where products.organization_id = p_organization_id
        and products.sku = upper(trim(v_menu_item->>'sku'));
      if v_product_id is null then
        raise exception using errcode = 'P0001', message = 'STORE_TEMPLATE_PRODUCT_MISSING';
      end if;

      v_variant_id := null;
      if nullif(trim(v_menu_item->>'variantCode'), '') is not null then
        select product_variants.id into v_variant_id
        from public.product_variants
        where product_variants.organization_id = p_organization_id
          and product_variants.product_id = v_product_id
          and product_variants.code = upper(trim(v_menu_item->>'variantCode'));
        if v_variant_id is null then
          raise exception using errcode = 'P0001', message = 'STORE_TEMPLATE_VARIANT_MISSING';
        end if;
      end if;

      insert into public.menu_items (
        organization_id, menu_id, product_id, variant_id, price_override_minor,
        sort_order, is_available, sold_out
      ) values (
        p_organization_id,
        v_menu_id,
        v_product_id,
        v_variant_id,
        (v_menu_item->>'priceOverrideMinor')::integer,
        coalesce((v_menu_item->>'sortOrder')::integer, 0),
        coalesce((v_menu_item->>'isAvailable')::boolean, true),
        coalesce((v_menu_item->>'soldOut')::boolean, false)
      );
    end loop;
  end loop;

  v_profile := v_snapshot->'profile';
  v_profile_slug := nullif(lower(trim(coalesce(p_public_slug, concat_ws('-', v_profile->>'publicSlug', p_code)))), '');
  v_profile_slug := trim(both '-' from left(regexp_replace(v_profile_slug, '[^a-z0-9-]', '', 'g'), 63));
  if v_profile_slug is not null and exists (
    select 1 from public.customer_store_profiles
    where customer_store_profiles.public_slug = v_profile_slug
  ) then
    v_profile_slug := trim(both '-' from left(v_profile_slug, 55)) || '-' || substr(replace(v_store.id::text, '-', ''), 1, 7);
  end if;
  if v_profile_slug is not null and v_profile is not null and v_profile <> 'null'::jsonb then
    insert into public.customer_store_profiles (
      store_id, organization_id, public_slug, public_enabled, area, category,
      price_range, availability_label, description, image_url, media_urls,
      facilities, policy_summary, latitude, longitude, rating, review_count
    ) values (
      v_store.id,
      p_organization_id,
      v_profile_slug,
      coalesce((v_profile->>'publicEnabled')::boolean, false),
      coalesce(nullif(trim(v_profile->>'area'), ''), 'Unlisted'),
      coalesce(nullif(trim(v_profile->>'category'), ''), 'General'),
      coalesce(nullif(trim(v_profile->>'priceRange'), ''), '฿฿'),
      nullif(trim(v_profile->>'availabilityLabel'), ''),
      nullif(trim(v_profile->>'description'), ''),
      nullif(trim(v_profile->>'imageUrl'), ''),
      coalesce(v_profile->'mediaUrls', '[]'::jsonb),
      coalesce(v_profile->'facilities', '[]'::jsonb),
      nullif(trim(v_profile->>'policySummary'), ''),
      (v_profile->>'latitude')::numeric,
      (v_profile->>'longitude')::numeric,
      (v_profile->>'rating')::numeric,
      coalesce((v_profile->>'reviewCount')::integer, 0)
    );
  end if;

  return query
  select v_store.id, v_store.organization_id, v_store.name, v_store.code,
    v_store.timezone, v_store.currency, v_store.store_mode, v_store.address,
    v_store.phone, v_store.tax_id, v_store.status;
exception
  when unique_violation then
    raise;
end;
$function$;

revoke all on function public.aevo_core_create_store_from_template(uuid, uuid, text, text, uuid, text) from public, anon, authenticated;
grant execute on function public.aevo_core_create_store_from_template(uuid, uuid, text, text, uuid, text) to service_role;

comment on function public.aevo_core_create_store_from_template(uuid, uuid, text, text, uuid, text) is
  'Core-owned store template instantiation with typed app configuration copy. Excludes aevo-digital-sing.';
