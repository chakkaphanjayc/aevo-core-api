-- MAP-009 allow the explicitly privileged SUPER_ADMIN role to use the
-- canonical Place admin mutation boundary. This is an additive policy repair;
-- public reads remain projection-gated and revision history remains immutable.

do $$
declare
  table_name text;
begin
  foreach table_name in array array[
    'aevo_place_registry',
    'aevo_place_source_links',
    'aevo_place_legacy_mappings',
    'aevo_place_revisions',
    'aevo_place_field_provenance',
    'aevo_place_public_projections',
    'aevo_place_projection_runs'
  ] loop
    execute format('drop policy if exists %I on %I', table_name || '_platform_write_policy', table_name);
    execute format(
      'create policy %I on %I for all using (current_setting(''aevo.platform_role'', true) in (''super_admin'', ''platform_owner'', ''platform_admin'')) with check (current_setting(''aevo.platform_role'', true) in (''super_admin'', ''platform_owner'', ''platform_admin''))',
      table_name || '_platform_write_policy',
      table_name);
  end loop;
end;
$$;
