-- =====================================================================
--  0009__task_lease_id_index.sql
--  Duration: instant (table is empty in every fresh deployment; on a
--    populated one this is a CONCURRENTLY build, see below)
--  Backward-compatible with: n/a — no deployment predates this
--
--  `DELETE /work/leases/{id}` (LeaseService.ReleaseAsync) matches on
--  `lease_id`, which carries no index — only `task_ready_idx` (scoped to
--  status = 'ready') and `task_lease_expiry_idx` exist. A release
--  therefore sequentially scans the whole `task` table, which accumulates
--  every completed task indefinitely. That lands directly on invariant 10:
--  the wms_api pool is reserved precisely so a scan confirmation never
--  queues behind a slow query.
--
--  Partial and cheap: leased rows are always a small minority of `task`.
-- =====================================================================

CREATE INDEX IF NOT EXISTS task_lease_id_idx ON task (lease_id)
    WHERE status = 'leased';
