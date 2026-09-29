-- MAP-002/MAP-005: retain the deterministic identity of an approved public
-- projection replay so a deployment retry can be idempotent by run_id.
-- This is operational metadata only; it does not create canonical Place rows
-- and does not make source/provider selection implicit.

alter table aevo_place_projection_runs
  add column if not exists replay_fingerprint text,
  add column if not exists reason text;

create index if not exists aevo_place_projection_runs_fingerprint_idx
  on aevo_place_projection_runs (replay_fingerprint, status, started_at desc)
  where replay_fingerprint is not null;
