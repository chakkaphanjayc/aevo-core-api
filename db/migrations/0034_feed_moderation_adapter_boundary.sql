-- DISCOVERY-003: Core-owned moderation adapter boundary.
--
-- TraceDee remains the canonical moderation domain during the transition. Core
-- owns the server-to-server adapter and adds an optimistic expected-status
-- check before delegating to the existing atomic moderation RPC. Browsers
-- never receive a Supabase or service-role credential.

create or replace function public.tracedee_moderate_report_if_status(
  p_actor_id uuid,
  p_report_id uuid,
  p_expected_status text,
  p_action text,
  p_reason text,
  p_idempotency_key text,
  p_request_hash text,
  p_source text default 'aevo-admin',
  p_session_id text default null
)
returns jsonb
language plpgsql
security invoker
set search_path = pg_catalog, public
as $$
declare
  v_expected_status text := upper(trim(coalesce(p_expected_status, '')));
  v_current_status text;
  v_existing record;
  v_scope text := 'tracedee:moderation:' || p_report_id::text;
begin
  if p_actor_id is null or p_report_id is null then
    raise exception using errcode = 'P0001', message = 'MODERATION_INPUT_INVALID';
  end if;
  if v_expected_status not in ('OPEN', 'REVIEWING', 'RESOLVED', 'DISMISSED') then
    raise exception using errcode = 'P0001', message = 'MODERATION_INPUT_INVALID';
  end if;
  if length(trim(coalesce(p_idempotency_key, ''))) < 8 or length(trim(p_idempotency_key)) > 200 then
    raise exception using errcode = 'P0001', message = 'IDEMPOTENCY_KEY_INVALID';
  end if;
  if length(trim(coalesce(p_request_hash, ''))) < 8 or length(trim(p_request_hash)) > 200 then
    raise exception using errcode = 'P0001', message = 'REQUEST_HASH_INVALID';
  end if;

  -- Exact retries return the original result before the optimistic status
  -- check; safe retries must not become false version conflicts.
  select request_hash, response_body
    into v_existing
  from public.tracedee_idempotency_keys
  where actor_id = p_actor_id
    and scope = v_scope
    and key = trim(p_idempotency_key);
  if found then
    if v_existing.request_hash <> trim(p_request_hash) then
      raise exception using errcode = 'P0001', message = 'IDEMPOTENCY_CONFLICT';
    end if;
    if v_existing.response_body is null then
      raise exception using errcode = 'P0001', message = 'IDEMPOTENCY_IN_PROGRESS';
    end if;
    return v_existing.response_body;
  end if;

  select status
    into v_current_status
  from public.tracedee_content_reports
  where id = p_report_id
  for update;
  if not found then
    raise exception using errcode = 'P0001', message = 'REPORT_NOT_FOUND';
  end if;
  if v_current_status <> v_expected_status then
    raise exception using errcode = 'P0001', message = 'MODERATION_VERSION_CONFLICT';
  end if;

  return public.tracedee_moderate_report(
    p_actor_id,
    p_report_id,
    p_action,
    p_reason,
    p_idempotency_key,
    p_request_hash,
    p_source,
    p_session_id);
end;
$$;

revoke all on function public.tracedee_moderate_report_if_status(uuid, uuid, text, text, text, text, text, text, text) from public, anon, authenticated;
grant execute on function public.tracedee_moderate_report_if_status(uuid, uuid, text, text, text, text, text, text, text) to service_role;
