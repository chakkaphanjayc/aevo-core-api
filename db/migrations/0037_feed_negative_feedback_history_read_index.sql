-- DISCOVERY-006A: support the private authenticated history read path.
-- This is an index-only additive migration; it does not change the public
-- Feed response or expose raw tokens, request hashes, or anonymous identity.

create index if not exists aevo_feed_negative_feedback_history_actor_created_idx
  on aevo_feed_negative_feedback_history (actor_id, app_code, created_at desc, history_id desc);
