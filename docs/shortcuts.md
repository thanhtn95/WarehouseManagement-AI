# Tracked shortcuts

Every shortcut taken during the proof-of-concept phases is recorded here **at
the moment it is taken**, paired with the phase item that replaces it.

Without this list, "hardcoded for now" becomes permanent and a proof of
concept quietly becomes the product by accretion. That is the most common way
a good PoC damages a project.

Format: `- [phase] what was shortcut → what replaces it (design doc §)`

## Open

- [1A] `stock_movement`'s append-only property is documented but **not
  enforced**: the `wms_api` / `wms_worker` database roles don't exist yet,
  so nothing revokes `UPDATE`/`DELETE` on it. Any code can currently mutate
  a ledger row → create the roles and their grants alongside the Compose /
  deployment increment, which is also where C7's per-role connection pools
  and statement timeouts land (design doc §1.3, §8.2)
- [1A] Seeded `permission.description_i18n` carries English only. Reason
  codes — the strings an operator actually reads on a handheld — are
  bilingual, but admin-facing permission descriptions are not, so D11's
  "English and Japanese throughout" is only half met → add `ja` for the
  permission catalogue before the first Japanese-language deployment
  (proposal D11, design doc §8.6)

- [1A] **A refreshed or PIN-resumed access token carries no session claim, so
  ending a session does not revoke it.** `AuthenticationService.RefreshAsync`
  always issues with `sessionId: null` (`FindRefreshSql` never reads a
  session, and `refresh_token` carries no session column), and the PIN-resume
  branch of `OperatorLoginAsync` always passes `device.SessionId`, which
  `ReadDeviceAsync`/`DeviceSql` never populate (they select
  `s.user_id, u.display_name, s.started_at`, never `s.id`).
  `TokenPrincipalResolver.ResolveAsync` only calls `SessionIsOpenAsync` when
  `claims.SessionId is Guid` — a token with no session claim skips that check
  entirely and is judged on `security_stamp` alone, which `LogoutAsync`
  and `device.status = 'lost'` do not bump.
  **Concretely:** operator A signs on to pooled handheld HH-014, the client
  refreshes once (routine within a 15-minute access-token lifetime), A logs
  out and hands the unit to operator B. The device session ends and A's
  refresh tokens are revoked, but A's still-live access token carries no
  session to check — it keeps authorizing, on a device B is now holding,
  attributing every fact B scans to A, for the rest of that token's lifetime.
  The same gap defeats H11's device-`lost` cascade against any already-
  refreshed token. → add `s.id` to `DeviceSql` and populate
  `DeviceState.SessionId`; in `RefreshAsync`, when the refresh token has a
  `device_id`, require an open `device_session` for `(device_id, user_id)`
  and carry its id into the reissued token; then make the invariant
  structural in `TokenPrincipalResolver` — a token carrying a device claim
  but no session claim resolves to no principal (design doc §5.1, §6.1,
  §8.1; review finding H11, extended)
- [1A] **Zone scope is not enforced anywhere, and the scope a principal
  carries can silently merge zones across role-scope rows in the same
  warehouse.** `OperatorPrincipal.CanIn(permission, warehouseId)` checks
  warehouse and permission only; `OperatorScope.ZoneIds` is populated and
  never read by any caller, including `POST /work/leases`
  (`WorkLeaseEndpoints.LeaseAsync`) — the one endpoint `0002__identity.sql`'s
  own schema comment and `0004__tasks.sql`'s own schema comment both name as
  "zone scope is read on every task lease." An operator scoped to one zone of
  a warehouse can lease ready work from *any* zone of it, including
  quarantine or damage, by naming that zone or by omitting `zoneIds`
  (empty means "every zone" to `LeaseSql`).
  **Compounding bug in the same area:** both principal resolvers
  (`TokenPrincipalResolver.ResolveAsync`, `AuthenticationService.ReadPrincipalAsync`,
  and `DevelopmentPrincipalResolver.ResolveAsync`) collapse every
  `user_role_scope` row for one warehouse onto a single `OperatorScope`,
  keyed by warehouse only. A user holding one role warehouse-wide
  (`zone_ids = '{}'`) and a second role scoped to one zone in the *same*
  warehouse gets one merged scope whose `ZoneIds` is whichever row's array
  the query happened to read first — row order is unspecified SQL, so this
  is not just missing a check, it means a zone check built directly on the
  current projection would be wrong in a row-order-dependent way. → track
  permissions against their own zone list per role-scope row rather than
  merging by warehouse (or key `OperatorScope` by `(warehouse, role-scope
  row)` instead of by warehouse alone); add `CanIn(permission, warehouseId,
  zoneId)`; have `LeaseAsync` intersect the requested zones with the
  caller's own rather than trusting the client's `zoneIds` verbatim (design
  doc §2.1, §6.1, §6.8)
- [1A] **`/sync/facts` checks permission but not scope at all.**
  `SyncFactsEndpoint.ApplyAsync` checks only `principal.Can(permission)` per
  fact type — an operator scoped to warehouse A holding `receipt.confirm`
  can still submit a `receipt_confirmed` fact against a receipt line in
  warehouse B. This is structurally different from the command-endpoint gap
  above: the fact payload names a line/task by id, not a warehouse, so the
  check has to resolve the referenced aggregate's warehouse *inside* the
  seven-step fact-confirmation transaction (`ConfirmReceiptFactHandler`,
  `ConfirmPutawayFactHandler`) rather than at the endpoint gate, which is
  concurrency-reviewer ground → wire a `CanIn` check into each fact handler,
  right after the referenced line/task is loaded and before the ledger is
  touched (design doc §2.1, §4.3, §6.1).
  **Partially compensated on both fact paths:** a receipt fact whose
  destination belongs to a different warehouse than its receipt line
  (`ConfirmReceiptFactHandler`) and a putaway fact whose destination belongs
  to a different warehouse than its task
  (`ConfirmPutawayFactHandler`) are both rejected inside their transaction,
  so stock cannot physically land in one site while its discrepancy is
  raised into another's exception queue. That check is *consistency*, not
  authorization: it still does not stop an operator acting in a warehouse
  they were never granted.
- [1A] Failed login against an **unknown** identity or an unknown/inactive
  device writes no `auth_event` at all — `AuthenticationService.RefuseAsync`
  returns `InvalidCredentials` before any write when `candidate is null`,
  and `OperatorLoginAsync` returns `UnknownDevice` the same way. Walking
  `employee_code` values against `/auth/operator/login`, or trying a badge
  known to be on a device already marked `lost`, leaves zero trace — lockout
  can't compensate either, since there is no credential row to count
  against. (Login failure against a *known* identity is fully audited and
  rate-limited; this item is the residual case.) → write `login_failed` with
  `target_user_id = NULL` and `detail.reason = 'unknown_identity'` /
  `'unknown_device'`, on its own connection, using the pattern
  `UserDirectory.RecordDeniedAsync` already establishes (design doc §2.1,
  §8.1)
- [1A] `POST /auth/refresh` never re-checks the user's `status` or validity
  window — `AuthenticationService.RefreshAsync`'s `PrincipalSql` has no
  `status`/`valid_from`/`valid_until` filter, unlike `ResolveSql` and
  `ScopedPermissionsSql`, which both do. A suspended, ended, or
  past-`valid_until` user keeps rotating refresh tokens and receiving `200`s
  for up to 30 days. `TokenPrincipalResolver` still blocks actual use of the
  resulting access token, so this is defence-in-depth rather than a live
  bypass on its own — but combined with the session-claim gap above, it
  widens the same door → filter status and validity in `PrincipalSql`, and
  return `InvalidCredentials` from `RefreshAsync` when the user no longer
  resolves (design doc §5.1, §8.1)
- [1A] Reporting/admin read endpoints (`ReadModelEndpoints`'s balance,
  movement, exception-queue and dashboard routes; `GET /users`) check a bare
  permission and treat `warehouseId` as an optional *client-supplied*
  filter rather than a bound. A Receiver or Auditor scoped to one site who
  omits it reads every warehouse in the deployment; a site-scoped
  `user.manage` holder enumerates the whole staff directory. Same shape as
  the command-endpoint gap this session closed, one tier down in stakes
  → apply the caller's own warehouse scope as a floor under (not a
  replacement for) the client filter (design doc §2.1, §5.9, §5.11)
- [1A] `GET /users/{id}` echoes the target's `security_stamp` in the response
  body (`UserDetail`, returned as-is by `UserEndpoints.GetAsync`) — this is
  in the *documented* §5.11 contract, not a bug introduced by accident, but
  it hands an admin-scoped caller the one non-guessable input a forged
  token needs (`TokenPrincipalResolver` compares it verbatim). It has no
  admin-UI purpose the stamp's own definition requires — an administrator
  needs to know *that* it changed, via `/me`'s own stamp on the affected
  session, not to read *what* another user's current value is → drop it
  from the HTTP response (keep it on the internal `UserDetail` DTO, which
  several `UserDirectoryTests` assert against directly); update the §5.11
  example accordingly, per the "update the design doc first" rule (design
  doc §5.11, §8.1)
- [1A] `DELETE /work/leases/{id}` is now correctly scoped to the caller's own
  lease (see Closed, below), but that closed the only API path that could
  recover an abandoned lease before this phase built its replacement:
  neither `GET /work/leases/expired` nor `POST /work/tasks/{id}/reclaim`
  exists yet (`task.reassign` is seeded as a permission string and nothing
  else). A handheld that drops under a forklift now holds its batch `leased`
  for the full 10-hour `LeaseDuration` with no API able to touch it — manual
  SQL only → ship the two §6.10 endpoints; the reclaim `UPDATE` needs
  `AND status = 'leased' AND lease_expires_at < now()` (reclaiming a *live*
  lease is exactly what produces the duplicate-pick risk §6.10 calls out)
  and an `auth_event` in the same transaction (design doc §5.3, §6.10)
- [1A] No `X-Client-Version` gate, so `/sync/facts` cannot return the `426`
  its contract specifies. The header applies to *every* request per §3.2, so
  it belongs in middleware rather than one endpoint, and building it per
  endpoint now would have to be undone → add the version-gate middleware with
  the auth pipeline (design doc §3.2, §5.3, §7.1)
- [1A] No admin API exists for master data at all — warehouse, zone,
  location, item, or handling unit. Every one of these is only reachable by
  direct SQL today; every integration test that needs one (`UserDirectoryTests`,
  `ConfirmReceiptFactTests`, `ReconciliationServiceTests`,
  `StockLedgerTransferTests`, `ReceiptEndpointTests`, and more) `INSERT`s it
  directly rather than through the API, because there is no other way. The
  Phase 1A scope table promises "real maintenance screens" for exactly this
  data → ship the already-designed §5.11 CRUD endpoints (warehouses, zones,
  locations, items, handling units)
- [1A] No admin API exists for device registration or management either —
  `AuthenticationTests` and `UserDirectoryTests` both `INSERT INTO device`
  directly. "Device registration" is named explicitly in the Phase 1A scope
  table → ship the already-designed `GET/POST /devices`, `PATCH /devices/{id}`
  (design doc §5.11)
- [1A] No way to compose or edit a custom role. `GET /roles` and
  `GET /permissions` (list-only) are the only role/permission endpoints
  registered; `POST /roles` and `PATCH /roles/{id}` don't exist. Custom role
  composition was explicitly pulled forward into Phase 1A earlier in this
  project's history specifically so a site isn't stuck with only the seeded
  system roles, and that's still the case today → ship the already-designed
  endpoints (design doc §5.11)
- [1A] The auth audit log has no read path. `auth_event` rows are written on
  every login, lockout, override, and credential/role change, but nothing
  exposes them — the only way to inspect one today is a direct SQL query
  against a table whose whole purpose is answering "who granted this person
  adjustment-approval rights, and when." "Auth audit log" is named explicitly
  in the Phase 1A scope table → ship a `GET` endpoint (open question already
  flagged in `wms-screen-inventory.md`; needs its own permission per that
  document's finding, not a repurposed existing one)
- [1A] **Supervisor override has no backend mechanism at all — Phase 1A exit
  criterion 6 is unmet.** `POST /auth/elevate` doesn't exist anywhere in the
  API (only referenced in a `SyncFactsEndpoint.cs` code comment), and
  `receipt_confirmed` unconditionally requires only `receipt.confirm` —
  `receipt.over_receive` is never conditionally checked even when a fact
  represents an over-receipt, so there is currently nothing to override →
  ship `POST /auth/elevate` (design doc §6.5) and wire a conditional
  `receipt.over_receive` check into `ConfirmReceiptFactHandler`
- [1A] **The allocation concurrency test the proposal names as "carry
  forward from 1B now" was never written.** §13 of the proposal is explicit:
  two orders, one remaining unit, assert exactly one succeeds, even with no
  picking UI behind it — "allocation is the hardest correctness problem in
  the system... a failing test sitting in the suite is cheap; discovering the
  problem in 1B is not." Nothing named `allocation` exists anywhere in
  `tests/` → write it now, per the proposal's own instruction, not deferred
  to when picking is actually built

## Closed

- [1A] ~~The integration-test fixture applies the migration `.sql` files
  directly rather than running them through DbUp~~ → **closed** by
  `src/Wms.Migrator`. The fixture now calls `MigrationRunner.Run`, the same
  entry point the migrator container uses, and `MigrationJournalTests`
  asserts the journal is populated so a revert to direct file application
  fails the build rather than passing quietly.

- [1A] ~~`inventory_exception.task_id` and `stock_movement.reason_code`
  carry no foreign key, because `task` and `reason_code` are created in
  later migrations~~ → **closed** by `0004__tasks.sql` and
  `0006__platform.sql`, which add both constraints. Verified against a real
  PostgreSQL: the `reason_code` constraint cascades from the partitioned
  parent to all 13 partitions and rejects an unknown code on insert.
  (No commit reference — nothing in this repository is committed yet.)

- [1A] ~~Authentication is a development-only header~~ → **closed** by
  `AuthenticationService` (staff email/password and operator badge/PIN,
  Argon2id, exponential lockout, refresh-token rotation with reuse-family
  revocation, `security_stamp`-based instant revocation) and
  `TokenPrincipalResolver`, which is now the *default* `IOperatorPrincipalResolver`
  — `Program.cs` only registers `DevelopmentPrincipalResolver` when
  `ASPNETCORE_ENVIRONMENT=Development` **and**
  `Wms:Auth:AllowDevelopmentHeaderPrincipal=true` are both set. The header
  path remains, deliberately, as a way to drive the API without seeding
  credentials while the frontend doesn't exist yet — but it is opt-in, not
  the default. (The token this now issues has its own gap on refresh/resume
  — see the new Open item above; that is a narrower, separate problem from
  header-trust.)
- [1A] ~~`POST /users` accepts no credentials~~ → **closed**. Argon2id hashing
  landed with `AuthenticationService`, and `UserDirectory.CreateAsync` now
  hashes and stores every `credentials[]` entry. Immediately superseded by a
  narrower shortcut, since accepting credentials is what turned the next item
  from a future risk into a live one.
- [1A] ~~`POST /users` assigns role scopes and credentials under `user.manage`
  alone~~ → **closed**. `POST /users` now additionally requires `role.manage`
  when `roleScopes` is non-empty and a new `credential.manage` permission
  (`0008__add_credential_manage_permission.sql`) when `credentials` is
  non-empty — the H13 subset check still bounds what a `role.manage` holder
  can actually grant. Without this, `user.manage` alone would have been able
  to create a fully working, loginable identity the moment credentials
  landed (previous item), which is exactly the identity-minting risk this
  entry originally flagged (design doc §5.11).
- [1A] ~~The device on a *fact* is resolved from the `X-Device-Id` header
  against the `device` table, rather than from the caller's session~~ →
  **closed** for the production path, narrowly: `TokenPrincipalResolver`
  takes the device from the validated token's claims
  (`ITokenIssuer.ValidateAsync`), never from the header. This closes
  *attribution* — a caller cannot name a device it wasn't issued a token
  for. It does **not** mean every token is bound to a currently-open
  session; that stronger claim turned out to be false and is now its own
  Open item above. `DevelopmentPrincipalResolver` still reads the header,
  which is expected: it is the dev-only stand-in tracked separately.
- [1A] ~~The `401` path writes no `auth_event` and has no rate limit~~ →
  **closed** for credential guessing against a *known* identity.
  `AuthenticationService.RefuseAsync` writes a `login_failed` `auth_event`
  and applies exponential lockout on every wrong password/PIN/badge, for
  both staff and operator login. Guessing against an *unknown* identity or
  device is a separate, still-open gap — see above. Deliberately *not*
  extended to a rejected bearer token on a protected endpoint
  (`TokenPrincipalResolver` returning `null`): a token is 256 bits of
  server-issued randomness, not a guessable secret, so there is nothing for
  a lockout to protect against there and auditing every malformed
  `Authorization` header on every request would drown the real signal.
- [1A] ~~`OperatorPrincipal` has no `security_stamp`~~ → **closed**. It is
  populated by both resolvers and compared against the user's current stamp
  on every request in `TokenPrincipalResolver`; a mismatch resolves to no
  principal, which is what makes suspending a user or changing their role
  take effect on the *next* request rather than at token expiry (H12).
  (The comparison only runs at all when the token carries a session claim in
  the first place for the *device* dimension — see the new session-claim
  Open item; the stamp check itself is unconditional and correct.)
