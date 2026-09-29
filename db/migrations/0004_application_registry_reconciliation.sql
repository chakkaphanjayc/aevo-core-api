-- Keep the registry compatible with the canonical application contract when
-- migration 0002 was already applied before Digital Sign was registered.

alter table aevo_application_registry
  drop constraint if exists aevo_application_registry_code_check;

alter table aevo_application_registry
  add constraint aevo_application_registry_code_check
  check (code in ('HUB', 'ADMIN', 'GO', 'PLAY', 'POS', 'KIOSK', 'QUEUE', 'DIGITAL_SIGN'));

insert into aevo_application_registry (code, name, kind)
values ('DIGITAL_SIGN', 'Aevo Digital Sign', 'OPERATIONS')
on conflict (code) do nothing;
