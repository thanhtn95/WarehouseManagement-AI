-- =====================================================================
--  0007__seed_reference_data.sql
--  Duration: instant
--  Backward-compatible with: n/a — initial schema
--
--  The permission catalogue, the seeded system roles, and the Phase 1A
--  reason codes. Every statement is ON CONFLICT DO NOTHING: this runs
--  unattended across a fleet and must be safely re-runnable.
--
--  SCOPE. Only Phase 1A permissions are seeded. The proposal is explicit
--  that Phase 2 "extends the permission catalogue to cover features added
--  in this phase" — seeding pick/pack/wave/count permissions now would
--  create rights for endpoints that do not exist.
--
--  Likewise only roles that have Phase 1A permissions are seeded. Picker,
--  Packer and Returns Processor arrive with the phases that give them
--  something to do; a seeded role granting nothing is a footgun, because
--  assigning it looks like granting access and isn't. Client User is not
--  seeded at all — D1 resolved to own-operator, so the 3PL client portal
--  is out of scope entirely.
--
--  Role ids are fixed, not generated: user_role_scope references them, and
--  every deployment in the fleet must agree on what SUPERVISOR means.
-- =====================================================================

-- --------------------------------------------------------------------
--  Permission catalogue (Phase 1A surface only)
-- --------------------------------------------------------------------
--  description_i18n carries English only for now. These are admin-facing
--  strings; the operator-facing reason codes below are bilingual, which is
--  where D11 actually bites. Tracked in docs/shortcuts.md.

INSERT INTO permission (code, category, description_i18n) VALUES
    -- Receiving
    ('receipt.create',            'receiving',  '{"en":"Open a receipt"}'),
    ('receipt.read',              'receiving',  '{"en":"View receipts"}'),
    ('receipt.confirm',           'receiving',  '{"en":"Confirm received quantities"}'),
    ('receipt.complete',          'receiving',  '{"en":"Close a receipt as received"}'),
    ('receipt.over_receive',      'receiving',  '{"en":"Authorise receiving more than expected"}'),
    -- Putaway and work
    ('putaway.execute',           'work',       '{"en":"Execute a putaway task"}'),
    ('task.lease',                'work',       '{"en":"Lease tasks from a zone"}'),
    ('task.reassign',             'work',       '{"en":"Reclaim a task from an expired lease"}'),
    ('task.cancel',               'work',       '{"en":"Cancel a task"}'),
    -- Inventory
    ('inventory.read',            'inventory',  '{"en":"View balances and movements"}'),
    ('inventory.adjust',          'inventory',  '{"en":"Raise a stock adjustment"}'),
    ('inventory.adjust.approve',  'inventory',  '{"en":"Approve a stock adjustment"}'),
    ('exception.read',            'inventory',  '{"en":"View the exception queue"}'),
    ('exception.resolve',         'inventory',  '{"en":"Resolve an inventory exception"}'),
    -- Master data
    ('item.write',                'master_data','{"en":"Create and edit items"}'),
    ('location.write',            'master_data','{"en":"Create and edit locations"}'),
    ('warehouse.read',            'master_data','{"en":"View warehouses"}'),
    ('warehouse.manage',          'master_data','{"en":"Create and edit warehouses"}'),
    ('zone.read',                 'master_data','{"en":"View zones"}'),
    ('zone.manage',               'master_data','{"en":"Create and edit zones"}'),
    ('handlingunit.read',         'master_data','{"en":"View handling units"}'),
    ('handlingunit.write',        'master_data','{"en":"Register handling units"}'),
    -- Identity and devices
    ('user.read',                 'identity',   '{"en":"View users"}'),
    ('user.manage',               'identity',   '{"en":"Create, edit and deactivate users"}'),
    ('role.manage',               'identity',   '{"en":"Assign roles and compose custom roles"}'),
    ('device.claim',              'identity',   '{"en":"Claim and release a handheld"}'),
    ('device.manage',             'identity',   '{"en":"Register, retire and mark devices lost"}'),
    -- Reporting
    ('report.read',               'reporting',  '{"en":"View dashboards and reports"}'),
    ('report.export',             'reporting',  '{"en":"Export a report"}')
ON CONFLICT (code) DO NOTHING;

-- --------------------------------------------------------------------
--  Seeded system roles
-- --------------------------------------------------------------------

INSERT INTO role (id, code, name_i18n, is_system, is_active, created_at, updated_at) VALUES
    ('10000000-0000-0000-0000-000000000001', 'SYSTEM_ADMINISTRATOR',
     '{"en":"System Administrator","ja":"システム管理者"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000002', 'WAREHOUSE_MANAGER',
     '{"en":"Warehouse Manager","ja":"倉庫管理者"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000003', 'SUPERVISOR',
     '{"en":"Supervisor","ja":"スーパーバイザー"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000004', 'INVENTORY_CONTROLLER',
     '{"en":"Inventory Controller","ja":"在庫管理担当"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000005', 'RECEIVER',
     '{"en":"Receiver","ja":"入荷担当"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000006', 'PUTAWAY_OPERATOR',
     '{"en":"Putaway Operator","ja":"格納担当"}', true, true, now(), now()),
    ('10000000-0000-0000-0000-000000000007', 'AUDITOR',
     '{"en":"Auditor","ja":"監査担当"}', true, true, now(), now())
ON CONFLICT (id) DO NOTHING;

-- --------------------------------------------------------------------
--  Role → permission
-- --------------------------------------------------------------------

-- System Administrator holds the entire catalogue. This is also what makes
-- the grant-what-you-hold check (H13) a constraint on delegation rather than
-- on top-level administration.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000001', code FROM permission
ON CONFLICT DO NOTHING;

-- Warehouse Manager: runs a site. Everything operational plus site-level
-- administration, but not warehouse.manage — creating a site is a
-- system-administration act, not a site-management one.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000002', code FROM permission
 WHERE code <> 'warehouse.manage'
ON CONFLICT DO NOTHING;

-- Supervisor: oversight and the authority to approve exceptions. Holds
-- receipt.over_receive, which the Receiver deliberately does not — that
-- gap is what the in-place elevation flow (§6.5) exists to bridge.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000003', code FROM permission
 WHERE code IN ('receipt.create', 'receipt.read', 'receipt.confirm', 'receipt.complete',
                'receipt.over_receive',
                'putaway.execute', 'task.lease', 'task.reassign', 'task.cancel',
                'inventory.read', 'inventory.adjust', 'inventory.adjust.approve',
                'exception.read', 'exception.resolve',
                'handlingunit.read', 'handlingunit.write',
                'warehouse.read', 'zone.read',
                'device.claim', 'device.manage',
                'report.read')
ON CONFLICT DO NOTHING;

-- Inventory Controller: owns stock accuracy. Can adjust and approve
-- adjustments, but note the domain layer still forbids approving one's own
-- (segregation of duties is enforced per-transaction, not per-role).
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000004', code FROM permission
 WHERE code IN ('inventory.read', 'inventory.adjust', 'inventory.adjust.approve',
                'exception.read', 'exception.resolve',
                'item.write', 'location.write',
                'handlingunit.read', 'handlingunit.write',
                'warehouse.read', 'zone.read',
                'report.read', 'report.export')
ON CONFLICT DO NOTHING;

-- Receiver: counts what arrives. Deliberately WITHOUT receipt.over_receive
-- — an over-receipt requires a supervisor's badge, which is Phase 1A exit
-- criterion 6.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000005', code FROM permission
 WHERE code IN ('receipt.create', 'receipt.read', 'receipt.confirm', 'receipt.complete',
                'task.lease', 'putaway.execute',
                'inventory.read', 'handlingunit.read', 'handlingunit.write',
                'device.claim')
ON CONFLICT DO NOTHING;

-- Putaway Operator: moves received stock into storage. Nothing else.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000006', code FROM permission
 WHERE code IN ('task.lease', 'putaway.execute', 'inventory.read', 'device.claim')
ON CONFLICT DO NOTHING;

-- Auditor: read-only, by design. No permission here mutates anything.
INSERT INTO role_permission (role_id, permission_code)
SELECT '10000000-0000-0000-0000-000000000007', code FROM permission
 WHERE code IN ('inventory.read', 'receipt.read', 'exception.read',
                'warehouse.read', 'zone.read', 'handlingunit.read',
                'user.read', 'report.read', 'report.export')
ON CONFLICT DO NOTHING;

-- --------------------------------------------------------------------
--  Reason codes (Phase 1A categories only)
-- --------------------------------------------------------------------
--  pick_discrepancy arrives with picking in 1B; count_variance and
--  disposition with Phase 3. Bilingual here because these are the strings
--  an operator reads on a handheld.
--
--  Receipt discrepancies are NOT shrinkage: that loss sits with the
--  supplier and is recoverable through a claim. Marking them shrinkage
--  would inflate apparent internal loss and point investigations at the
--  wrong place. The photo requirements exist because a claim without
--  evidence is a write-off in practice.

INSERT INTO reason_code (code, category, label_i18n, applies_to,
                         requires_note, requires_photo, requires_approval,
                         is_shrinkage, sort_order) VALUES
    ('RCV_SUPPLIER_OVER',   'receipt_discrepancy',
     '{"en":"Supplier over-shipped","ja":"仕入先の過剰納品"}',        '{receipt}',
     false, false, true,  false, 110),
    ('RCV_SUPPLIER_SHORT',  'receipt_discrepancy',
     '{"en":"Supplier short-shipped","ja":"仕入先の納品不足"}',       '{receipt}',
     false, true,  false, false, 120),
    ('RCV_DAMAGED_IN_TRANSIT', 'receipt_discrepancy',
     '{"en":"Damaged in transit","ja":"輸送中の破損"}',               '{receipt}',
     false, true,  false, false, 130),
    ('RCV_WRONG_ITEM',      'receipt_discrepancy',
     '{"en":"Wrong item delivered","ja":"誤品の納品"}',               '{receipt}',
     true,  true,  false, false, 140),
    ('RCV_COUNT_CORRECTION','receipt_discrepancy',
     '{"en":"Recount correction","ja":"再カウントによる訂正"}',       '{receipt}',
     true,  false, false, false, 150),

    ('PUT_LOCATION_FULL',   'admin_correction',
     '{"en":"Directed location full","ja":"指定ロケーション満杯"}',   '{putaway}',
     false, false, false, false, 210),
    ('PUT_LOCATION_BLOCKED','admin_correction',
     '{"en":"Directed location blocked","ja":"指定ロケーション使用不可"}', '{putaway}',
     false, false, false, false, 220),

    ('DMG_HANDLING',        'damage',
     '{"en":"Damaged in handling","ja":"作業中の破損"}',              '{adjustment}',
     true,  true,  true,  true,  310),
    ('DMG_UNKNOWN',         'damage',
     '{"en":"Damage, cause unknown","ja":"破損（原因不明）"}',        '{adjustment}',
     true,  true,  true,  true,  320),

    ('ADJ_OPENING',         'admin_correction',
     '{"en":"Opening balance","ja":"期首在庫"}',                      '{adjustment}',
     false, false, false, false, 410),
    ('ADJ_REVERSAL',        'admin_correction',
     '{"en":"Reversal of an erroneous entry","ja":"誤登録の取消"}',   '{adjustment}',
     true,  false, true,  false, 420),

    -- The catch-all, deliberately expensive to use and monitored: alert
    -- when it exceeds 5% of coded events, which means the catalogue is
    -- missing something the operation actually does. Categorised as
    -- admin_correction because the design's seven categories have no
    -- general bucket; it is the closest fit, not an obvious one.
    ('GEN_OTHER',           'admin_correction',
     '{"en":"Other (describe)","ja":"その他（要記入）"}',
     '{receipt,putaway,adjustment,move}',
     true,  true,  true,  false, 900)
ON CONFLICT (code) DO NOTHING;
