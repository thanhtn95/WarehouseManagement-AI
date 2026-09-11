-- =====================================================================
--  0008__add_credential_manage_permission.sql
--  Duration: instant
--  Backward-compatible with: n/a — no deployment predates this
--
--  `POST /users` accepts credentials[] under `user.manage` alone, which
--  makes `user.manage` an identity-minting capability: create a user, hand
--  it a role, set its password, all under one permission. This gives
--  credential-setting its own permission, gating it the same way
--  role-scope assignment is already gated by `role.manage` (design doc
--  §5.11).
--
--  A prior draft of this migration edited 0007's seed INSERT directly.
--  That was wrong even though nothing had been deployed yet: a migration
--  that has been applied anywhere is immutable (sql-migrations skill), and
--  DbUp journals by filename, not content — an environment that already
--  ran 0007 would never receive an edit made to it after the fact.
-- =====================================================================

INSERT INTO permission (code, category, description_i18n) VALUES
    ('credential.manage', 'identity', '{"en":"Set or reset a user login credential"}')
ON CONFLICT (code) DO NOTHING;

-- 0007's grant of the whole catalogue to System Administrator, and of
-- everything but warehouse.manage to Warehouse Manager, was each a
-- one-time snapshot taken when that migration ran — not a standing rule.
-- Nothing re-runs it, so a permission added later must be backfilled
-- explicitly here or it becomes permanently ungrantable under the H13
-- subset check (sql-migrations skill: "a migration that adds a permission
-- must backfill System Administrator").
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000001', code
  FROM permission WHERE code = 'credential.manage'
ON CONFLICT DO NOTHING;

INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000002', code
  FROM permission WHERE code = 'credential.manage'
ON CONFLICT DO NOTHING;
