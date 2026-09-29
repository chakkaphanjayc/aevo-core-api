-- Complete the app-scoped session contract after the additive lifecycle
-- migrations. This migration is safe to run repeatedly and deliberately
-- revokes rows that cannot satisfy the CSRF requirement instead of guessing a
-- token that was never persisted.

update aevo_app_sessions
set revoked_at = coalesce(revoked_at, now())
where csrf_token_hash is null;

alter table aevo_app_sessions
  alter column csrf_token_hash set not null;

do $$
begin
  if to_regclass('aevo_application_registry') is not null
     and not exists (
       select 1
       from pg_constraint
       where conname = 'aevo_app_sessions_app_code_fkey'
         and conrelid = 'aevo_app_sessions'::regclass
     ) then
    alter table aevo_app_sessions
      add constraint aevo_app_sessions_app_code_fkey
      foreign key (app_code) references aevo_application_registry(code)
      on update cascade on delete restrict;
  end if;
end;
$$;

create index if not exists aevo_app_sessions_user_app_activity_idx
  on aevo_app_sessions (user_id, app_code, last_seen_at desc)
  where revoked_at is null;

comment on table aevo_app_sessions is
  'Opaque, app-scoped sessions. Browser clients never receive provider bearer tokens.';
