# Foundation Layer + IAM Module Design (AST) — Design / Spec

> **Status:** APPROVED. Every IAM table follows the **header+version temporal effective-period model** (identity card + version table, resolved by date, 8-case algebra, strict temporal-FK); technical source of truth: **`docs/design-effective-period.md`**. §③ (schema) and §④ (base repository) below keep their intent; the **concrete schema follows header+version** (`docs/design-iam-schema.md`). Data technology: **Dapper + MySqlConnector** (closed).

## Closed decisions
- Log in with a **Windows/domain (AD) account** — no password, no login screen.
- Org units form a **multi-level parent–child tree**.
- Each user belongs to **exactly 1 org unit + 1 role** at any given time.
- Data scope is attached per **(role x function)**, **4 levels**.
- The root admin is managed via a **list of usernames in a config file** (break-glass), independent of the DB.
- MySQL is **Community** (not Enterprise), and **enabling KDS on the domain controller is difficult**.
- The host registers module list, descriptors and navigation at one composition point.

---

## (1) Authentication
- The app reads the currently logged-in Windows identity (via the .NET Windows-identity API), no password.
- **The user identity key = `username` (samAccountName), stripped of the `example\` prefix, case-insensitive** (unique within 1 domain). **The security identifier (SID) is stored** as a cross-check/audit trail field (in case a username is renamed/reused) — the SID is not the primary key, only metadata.

## (2) Authorization model (2 levels + 4 scopes)
- **Level 1 — Function access:** whether a role may open function Y (yes/no).
- **Level 2 — Data scope:** attached per **(role x function)**, 4 levels:
  1. **Self** — only data created by the user themself.
  2. **Own org unit** — all data belonging to the assigned org unit.
  3. **Own org unit + descendants** — the whole subordinate subtree (auto-narrows to "own org unit" at a leaf org unit).
  4. **Global** — for top-level administration/leadership.
- Shared-kernel contract:
  - An authorization service (`AST.Core/Iam/`): for `(user, functionKey)` → **is it accessible?** + returns a **data-scope value** = `{ Level (1 of 4), root org unit }`.
  - A function registry (`AST.Core/Iam/`): where every module **self-registers its functions** (see (7)).

## (3) IAM data model — the five entities below are all **Declared**
Which entities carry an effective period at all is decided in `design-temporality-classes.md` (its §5 register is the single home for that answer); the five listed here are Declared: org unit, role, user, role-permission, function. A NEW IAM table is not Declared by default — run that document's test before giving it period columns. Concrete columns, types and FKs: `docs/design-iam-schema.md`. Catalog sync semantics (auto add/update only, admin close/reopen, identity reuse, fixed epoch): `docs/design-function-catalog-sync.md`.
- **User identity key** = `username` (samAccountName, `example\` stripped); the app **captures `sid` itself the first time the user logs in**, only for cross-check/audit. The admin only enters the `username` + selects the org unit/role.

## (4) Base repository enforcing "3 conditions" (prevents omission in every future module)
- The shared kernel provides a **shared base repository/query** that forces every "fetch currently-usable data" query to filter **simultaneously**: `isactive=1` **and** within the closed effective period (`effective_from <= D AND D <= effective_to`, with `effective_to=9999-12-31` meaning "not yet determined"; `docs/design-effective-period.md` invariant 2) **and** within the org-unit scope per the data-scope value. Modules **must not write this filter themselves** → nobody can forget it.
- The "Own org unit + descendants" level needs the full subtree: use a **recursive CTE** over the identity tree (`docs/design-effective-period.md` §6).

## (5) DB connection + Config protection + Root-admin break-glass
**Infrastructure constraints (imposed by the organization):** ACL/policy cannot be adjusted; AD has users only, NO group membership; no KDS; MySQL **Community** (verified: no Windows/Kerberos/LDAP authentication → a connection string must be stored). Architecture chooses **Option 2** (client connects straight to MySQL, 2 layers). Split **2 secrets into 2 independent parts**, both **encrypted files on the share**:
- **File A — DB connection (shared, every user reads it at bootstrap):**
  - **Secret (cannot be truly hidden at these 2 layers):** only **cosmetic encryption** (AES, key shipped with the app) — blocks casual viewing/plaintext leakage, does NOT block a technically savvy user.
  - **Real protection = on the DB side (done by the DBA, without touching org AD/ACL):** a dedicated DB account with **minimum privileges** (`SELECT/INSERT/UPDATE` on the app schema; NO `DELETE/DROP` → forces soft delete at the DB layer; no other schema; not an admin) + **host/subnet restriction** (`ast_app@'subnet.%'` → a leaked connection string is useless outside the app network) + **DB auditing**.
  - Edited via the admin connection-declaration screen.
- **File B — Root-admin break-glass (special, kept separate):**
  - **Does NOT use DPAPI-NG** (KDS is hard to enable). The file contains a **list of root-admin usernames** (e.g. `alice`); the app compares the **currently logged-in Windows username** → a match = full-authority root admin (Global scope), independent of the DB, **with logging**.
  - **The barrier = the Windows identity** (cannot be impersonated without that person's Windows password).
  - The root admin exists to create the initial org units/roles/users + to rescue the system if DB permissions are locked out by mistake; day-to-day operation uses a **"System Administrator" role in the DB**.
  - ⭐ **The ROOT ORG UNIT is break-glass-only for declaring, adjusting and closing/cancelling, and is never replaceable.** Declaring one, adjusting one, and closing or cancelling one remain reserved to a break-glass admin; an ordinary admin, however wide their data scope, may only READ it. Enforced in `OrgUnitDeclarationService` by `OrgUnit.RootNotDeclarable` (Add), `OrgUnit.RootNotEditable` (Edit, which includes the "the unit ends on that date" gesture) and `OrgUnit.RootNotClosable` (Close/Cancel), and each permitted write records a second `audit_log` row (`orgunit-root-add-breakglass` / `orgunit-root-edit-breakglass` / `orgunit-root-close-breakglass`) so that "a normally-forbidden operation was permitted" is a fact a security review can query for rather than infer from `parent_id`. ⚠️ **Requester ruling 2026-09-06:** replacing a root is forbidden to every actor, break-glass included; re-declaring the root goes through Close → Add. `OrgUnit.RootNotReplaceable` is an unconditional refusal when the *predecessor* is a root **or** when the *successor* is declared as a root, because the gesture writes both. The other three writes touch one unit each. **Consequence:** a fresh database has no root, so its first org unit can only be declared by a break-glass admin — which is what File B's own purpose above already says.
- **Both File A & B are protected against tampering/forgery with a DIGITAL SIGNATURE (instead of an unusable ACL):**
  - Use a **self-generated asymmetric key pair** (RSA/ECDSA via .NET's built-in cryptography libraries) — **free, no CA purchase, no expiry**. **Asymmetric is mandatory** (no HMAC/symmetric, because a symmetric key shipped inside the app could be extracted to forge signatures).
  - IT keeps the **private key offline** (an admin machine, never on the share), using a **small signing tool** to sign the file's content every time it's updated. The app **embeds the public key** (harmless if exposed), **verifies the signature before use**; a wrong/missing signature → **refused** (no DB connection / root admin not recognized). Losing the private key → generate a new pair, re-sign, ship the app with the new public key.
  - ⇒ No ACL/AD group/KDS/server needed: nobody can forge valid content without IT's private key; a sneaky overwrite → the app detects & refuses it; corruption/mistaken edits → restore from the **original copy in source control**.
  - The app **logs** whenever signature verification fails and whenever the root-admin path is used.
  - **A build-time on/off flag:** `RequireConfigSignature` — **Debug = off** (dev runs immediately, no signing needed), **Release = on** (prod). The flag lives **outside the signed file** (tied to the build configuration) → toggling it does not change any logic.
  - **Signing process (overview):** (A) generate **1 key pair** (RSA 3072/ECDSA) offline — `private` kept by IT (backed up, never on the share), `public` embedded in the app's source; (B) IT runs a **separate signing tool** (a small console project, NOT shipped to users) → produces a `.sig` file next to File A/B every time the config is set/changed; (C) the app runs the signature-verification step at startup, and refuses + logs on failure. The key **never expires**; if a key leaks → generate a new pair + rebuild (new public key embedded) + re-sign (lightweight since config rarely changes).
- **To truly hide the DB password** ⇒ the only way is **Option 1 (adding a middle-tier service that holds the password on a server, with the client authenticating via Windows)** — noted as a **future upgrade path**.

## (5b) Dev environment vs. production (config changes only, NO code changes)
- **Dev (laptop, client=server):** Debug build (signature OFF) + MySQL `ast_app'@'localhost'` + run the app from a **local folder** (e.g. `D:\ASTDeploy\`) simulating the share. Paths are resolved via the app's base directory (relative) → running from a local folder or a UNC share behaves identically.
- **Going to production — only change things outside the code:** Release build (signature ON); the DBA changes the account host `ast_app'@'localhost'` → `'10.20.30.%'` (`RENAME USER` or create new); change **the connection string in File A** (server localhost → the LAN server IP) via the admin screen; **copy the folder** to `\\server\share\AST\`; IT **signs File A/B once**.
- **Mandatory principle for a lightweight environment switch:** NEVER hardcode a connection string in the code (always in File A); NEVER hardcode an absolute path (always relative to the app's base directory). The signature + subnet restriction can be off in dev, on before going to production — as long as the code reads/checks via configuration, not hardcoded values.

## (6) Menu contribution (a module contributes a leaf into a shared group)
- **Menu group codes** (e.g. `Config.Security`, `Config.Params`) are defined in the **shared kernel** — **owned by no module**.
- Each module declares: `{ leaf (= function_key) + parent group code + required permission + order }`. The Shell gathers **all** declarations, builds the **Configuration** menu tree, shows/hides a leaf per the authorization service.
- ⇒ Module B's leaf can sit under a group that module A also contributes a leaf to, **because both only reference the shared-kernel group code, with no cross-module reference**. Adding/removing a leaf = edited within that same module, without touching the Shell/other modules.

## (7) Function identity (function registry) — feeds authorization + menu + dashboard
Each module registers **once** per function: `{ FunctionKey, BusinessCode, DisplayName, MenuGroupCode, NavigationTarget, RequiredPermission, (icon, order) }`. One declaration serves authorization, menu, and the dashboard: the top five most-used functions, counted by `FunctionKey`, reopened via `NavigationTarget`. Day-to-day UI displays **BusinessCode + DisplayName**; `function_key` is only used internally. (The "function-usage log" table is journal data — deferred, built alongside the dashboard.) Settled menu model: `docs/sidebar.md`. Catalog sync: `docs/design-function-catalog-sync.md`.

---

## (8) Operations & deployment infrastructure (shared kernel)
These items fill operational gaps without reopening decisions D1–D13 of `docs/design-effective-period.md`.

**(8.1) Schema migration (B1) — run manually via DBeaver + a version gate.**
- **Numbered** SQL scripts in the repo, `migrations/V001__*.sql`, `V002__…`; a header comment states the conditions/order; the requester runs them one by one using a **DBA account** (no runner tool).
- A `schema_version` table (the last line of every script does `INSERT INTO schema_version(version, applied_at, applied_by)`): the DB records for itself how far it has been applied.
- The app knows which schema version it needs (a build-time constant); at startup it reads `schema_version`, and a mismatch → **BLOCKS + reports** "The app needs schema V00X, the DB is on V00Y — contact an administrator." This avoids a new app running against an old DB (or vice versa) when 30 users share the same deployment.
- The app account `ast_app` has NO DDL/DELETE (per §5); migrations use a separate, higher-privilege account — `ast_app`'s privileges are never widened "for convenience".

**(8.2) Packaging & updating from the share (B2).**
- **Publish self-contained** (bundling the .NET 10 runtime), **NOT single-file** — workstations have nothing preinstalled, and the directory-scanning module catalog needs to scan loose module DLLs under `Modules/`.
- **A versioned folder** `\\share\AST\v1.2.3\` + a launcher/shortcut pointing to the latest version — Windows locks a DLL that's currently running, so it can't be overwritten in place; a user opening the app later goes straight into the new version, a user with it already open gets a notice to close.
- An `app_control` table: the admin writes a command + a close-by deadline; the app polls (sharing (8.3)), shows the notice, and auto-closes at the deadline.

**(8.3) "Realtime" = polling standardized in one place (B3).** One shared service in the shared kernel (e.g. a polling service), with an **admin-configurable** interval (default 30–60s), fetching deltas by `recorded_at`/id. The dashboard, the notification list, and the app-close command (8.2) all go through this. Modules must NOT build their own ad hoc polling. (30 users x 1 lightweight query / 30s = negligible load.)

**(8.4) Logging & audit (B4).**
- **Business/security audit** (login, break-glass, signature failures, permission changes) → a DB table `audit_log`, **append-only** (`ast_app` has no DELETE ⇒ self-protecting against deletion; centralized lookup).
- **Technical logging** (exceptions/traces) → **Serilog** (Apache-2.0 license) writing to a local file `%LOCALAPPDATA%\AST\logs\` on each machine; technical logs must NOT be written to the share (avoids contention/lock-ups when 30 users write concurrently).

**(8.5) Testing (B5).** **xUnit**; FluentAssertions 7 for new or changed assertions. **FluentAssertions ≥ v8 is BANNED** (commercial license). The effective-period engine must have: unit tests covering all **8 algebra cases** (injecting a fake business-date-provider abstraction) + an integration test against local MySQL for the named lock/recursive CTE.

**(8.6) Transient-connection retry (C4).** One shared policy at the data layer in the shared kernel: timeout + a **short backoff retry** for transient errors (MySqlConnector can classify the exception type); a prolonged failure → raises a **"DB connection status"** signal (admin dashboard, per `AST.md`). Modules must NOT write their own retry logic. **Write safety:** only retry when it's certain the command has NOT yet reached the DB (e.g. an error right when opening the connection); a write cut off mid-flight must **NOT be retried blindly** — report the error so the business flow can check (to avoid a duplicate write).

> **MySQL 9.7 is an LTS release**; no support-lifecycle plan is needed.

---

## Items needing confirmation from the organization's IT (hand this list to IT/DBA)
1. Workstations are **AD domain-joined**; the app can read the currently logged-in Windows `username`.
2. **A dedicated, minimum-privilege DB account for the app** (`SELECT/INSERT/UPDATE` on the app schema, no `DELETE/DROP`) **+ host/subnet restriction** **+ DB auditing** — configured by the DBA on the MySQL side (without touching org AD/ACL). Example: `CREATE USER 'ast_app'@'10.20.30.%' IDENTIFIED BY '<pwd>'; GRANT SELECT,INSERT,UPDATE ON ast_db.* TO 'ast_app'@'10.20.30.%';` (replace `10.20.30.%` with the real workstation IP range; MySQL automatically blocks connections from outside the range, the app just uses a normal connection string). Note NAT/VPN can change the source IP; enabling `skip_name_resolve` means using IPs, not hostnames.
3. Confirmation that **ACL/policy cannot be used** and **AD has no group membership** ⇒ config-file tamper-protection uses a **digital signature** (IT keeps the private key offline + a signing tool; the app embeds the public key to verify).
4. Confirmation of **MySQL Community** (not Enterprise) ⇒ keep Option 2 + the protections above.
5. Where to place File A/B on the share + the recovery process (keep the **signed original** in source control).

---

## (9) Org-unit gestures and replacement (Thay thế)

### (9.1) The five gestures
Requester ruling 2026-09-04, amended for Sửa by the requester on 2026-09-09. Each gesture writes rows carrying exactly one `VersionOperationKind`; the five gestures and the five values map 1:1.

| Gesture | What it changes | Kind |
|---|---|---|
| Thêm | declares a new org unit | `Add` |
| Sửa | content, as a new version over the same period (in-place correction); never the org code, never the parent. To hold two periods with different data on one identity, Sửa moves effective-from later and leaves a head remnant; a period change makes the reason mandatory. Not yet enforced by the code. | `Edit` |
| Đóng | ends an operating org unit; last effective day ≥ `today - 1` | `Close` |
| Hủy | withdraws a version that has not completed one effective day | `Cancel` |
| **Thay thế** | **replaces one org unit wholly** with a corrected declaration | `Replace` |

- **Đóng** says *"this unit existed, and now it ends."* The rows stay `normal`; the history is true.
- **Thay thế** says *"this unit was never right as recorded; this is the corrected declaration."* It declares a **new** identity and marks the old one's rows `replaced`. It is the only gesture that can give a unit a different parent or org code.
- The note (`Ghi chú`) carries *why* a Sửa was made — a real change or a data-entry fix. No kind separates the two and the confirm answer is not persisted (requester 2026-08-22). History therefore cannot filter corrections; this is a ruling, not a defect.

### (9.2) Scope of a replacement
- **One predecessor per gesture** (requester 2026-09-04). No predecessor set, no merge.
- **The predecessor must be EMPTY** (requester 2026-08-22: *"Chức năng thay thế chỉ áp dụng cho đơn vị rỗng, không mở rộng."*). No child org unit and no user may reference it over any of its coverage. A closed scope, not a deferral; only a new requester decision reopens it. With no dependent, nothing can be stranded, so the whole predecessor is replaced and no slice is preserved.
- **A root is never replaceable** (§(5), requester ruling 2026-09-06); re-declaring a root goes through Đóng then Thêm.
- **The successor declares its own parent** (requester 2026-09-04). Parent immutability is not relaxed (requester 2026-08-21: *"Closing and recreating keeps the declared history intact, which is the same reason the effective-period model refuses to erase a declared tail."*). Sửa cannot move a parent; Thay thế declares a new identity whose parent is stated fresh, exactly as Thêm does.

The operator supplies: the predecessor's org-unit id; the corrected effective period (unrelated to the predecessor's); the org code (may differ); the parent org-unit id; full and short VN names; supplemental fields; the reason (stored on the successor's first version). The actor, the authorization scope and every `VersionOperationKind` are derived server-side.

### (9.3) Row operations
For **every** active version row of the predecessor, the mark, then the stamp:

```sql
-- [mark] taken out of force, so the probes that follow do not see the predecessor blocking itself
UPDATE org_unit_version SET isactive = 0 WHERE id = @rowId AND isactive = 1

-- [stamp] applied to exactly the rows the mark affected, AFTER the successor identity exists
UPDATE org_unit_version
   SET status = 'replaced', replaced_by_org_unit_id = @successorOrgUnitId
 WHERE id = @rowId
```

Then one `INSERT`: the successor identity's first version, `operation_kind = 'Replace'`, `status = 'normal'`, `isactive = 1`.

- `operation_kind` is **not** touched on predecessor rows: an operation labels only the rows it writes, never the rows it takes out of force.
- **Two statements, not one:** `replaced_by_org_unit_id` is a foreign key MySQL checks immediately, so the link cannot be written before the successor identity exists; the mark must still run before the probes (§(9.4)), and the identity is minted only after every replacement guard passes.
- The intermediate shape — `isactive = 0`, `status = 'normal'`, no link — is a legal superseded row that `chk_ouv_status` admits; it exists only inside the transaction.
- No inactive INSERT and no new row operation: flipping status while deactivating is the shape `CancelVersionAsync` already uses. Nothing is hard-deleted.

### (9.4) Ordering inside the single transaction
The whole gesture is one `CompositeWrite` in `OrgUnitDeclarationService.ReplaceOrgUnitDeclarationAsync`. The order is an invariant. Step labels are stable; do not renumber them.

```
[1]   authorize
[2]   scope gate -- Global (OrgUnit.ReplaceRequiresGlobalScope)
[3]   enlist, up front in a fixed order: the predecessor identity, then request.ParentId when supplied.
      The successor identity is minted inside and takes no lock of its own.
[4]   ONE transaction:
      [4a]  read the predecessor's ACTIVE rows under the lock -> ids, periods, parent id(s)
      [4b]  no active row                                      -> OrgUnit.PredecessorMarksNothing
      [4c]  distinct parent count > 1 (null counts)            -> OrgUnit.ParentNotWellDefined
      [4d]  ROOT GATE: stored parent null, or successor root   -> OrgUnit.RootNotReplaceable
      [4d2] requested parent is the predecessor or in its identity subtree -> OrgUnit.ParentWithinPredecessor
      [4e]  MARK every row read at [4a]    <-- BEFORE every probe that reads isactive
      [4f]  predecessor-empty probe                            -> OrgUnit.PredecessorNotEmpty
      [4g]  N1 root-overlap probe when request.ParentId is null -> OrgUnit.RootPeriodOverlaps
      [4h]  mint the successor identity
      [4i]  write the successor's first version, kind Replace
            <-- the shared engine runs P6 (code in use) and D8 (parent coverage) here, inside the upsert
      [4j]  STAMP status = 'replaced' + the successor link, on exactly the ids marked at [4e]
      [4k]  the audit row
```

- **`[4b]` → `[4c]` → `[4d]` is the Sửa order**, and each step exists because the one before would otherwise swallow it. `[4b]` is decided from the read, not from the mark's affected count; a closed predecessor has an empty parent set and would otherwise be reported as a parent problem. `[4c]` runs before the root gate because `null` is a real element of the parent set — root gate first would report a `{null, P}` predecessor as `RootNotReplaceable` and make `ParentNotWellDefined` unreachable. `[4d]` therefore sees a singleton set.
- **`[4d]` stays inside the transaction:** the stored parent is a value a concurrent writer can change, so the gate is sound only under the lock.
- **`[4d2]` reads identity history, not `isactive`**, so it keeps `[4e]` ahead of every `isactive`-reading probe.
- **`[4g]` is unreachable while `[4d]` refuses a root successor**; it stays in the code.
- The mark's affected count is compared with `[4a]`'s as a technical consistency check — a violated invariant, not a business code — and rolls the transaction back.
- **`[4e]` before `[4g]` and `[4i]` is load-bearing.** N1 reads `isactive = 1` on this transaction's view, and `FindCodeInUseAsync` excludes only the identity being written — the successor. With the mark first, the predecessor's rows neither block a root period nor collide on the code. **P6 needs no predecessor exemption; do not add one "for safety"** — two mechanisms for one fact drift.
- **P6 and D8 are inherited, not replacement guards,** and run at `[4i]`, after the mint — the same as Thêm, which also mints before its upsert. Do not add pre-mint copies: a second code-availability query is a second home for one rule, and moving `ValidateChildCoverage` earlier changes the shared `VersionedRepository`.
- One business date for the whole gesture, read once from the injected provider, never inside the transaction delegate.

### (9.5) Guards
| Step | Fires when | Code |
|---|---|---|
| `[2]` | the actor's scope is not Global | `OrgUnit.ReplaceRequiresGlobalScope` |
| `[4b]` | the predecessor has no active version (already closed, cancelled or replaced) | `OrgUnit.PredecessorMarksNothing` |
| `[4c]` | the predecessor's active rows carry more than one distinct parent, counting `null` | `OrgUnit.ParentNotWellDefined` |
| `[4d]` | the predecessor's single parent is `null`, or the successor is declared as a root — every actor, break-glass included | `OrgUnit.RootNotReplaceable` |
| `[4d2]` | the requested parent is the predecessor or an identity in its subtree | `OrgUnit.ParentWithinPredecessor` |
| `[4f]` | a child org unit or a user still depends on the predecessor's coverage | `OrgUnit.PredecessorNotEmpty` |
| `[4g]` | the successor is a root and its period overlaps another active root | `OrgUnit.RootPeriodOverlaps` |
| `[4i]` P6 | another identity holds the successor's code over its span | `OrgUnit.CodeInUse` (`OrgUnitRepository.FindCodeInUseAsync`, private, called by `UpsertAsync`) |
| `[4i]` D8 | the successor's parent does not cover the successor's period | `TemporalFk.ParentGap` (`ValidateChildCoverage`, inside `VersionedRepository.ApplyUpsertPlanAsync`) |

- **`[4c]` is not hypothetical:** `OrgUnitRepository.GetActiveParentIdsAsync` returns a set because a mixed-parent active history is representable. Refuse it; never take an unordered first row.
- **`[4f]` needs no new mechanism:** a total replacement is the fully closed parent case of `ITemporalFkValidator.ValidateParentChange` — pass an empty remaining-coverage list and it blocks with `TemporalFk.DependentsUncovered`, mapped to `OrgUnit.PredecessorNotEmpty`. The number it reports counts uncovered dependent version PERIODS, not distinct units or users; an operator message must say what it counts or say nothing numeric.
- **Enforced by the database** (`chk_ouv_status`): no `replaced` row with `isactive = 1`, and no `replaced` row with a null successor link. Test that the database rejects them.
- **Deliberately absent:** the successor need not carry the predecessor's org code; uniqueness is not narrowed by exempting the predecessor (the ordering carries it); "the successor is not the predecessor and is not already replaced" holds by construction for a freshly minted identity and becomes a real guard only if the successor is ever an existing identity; "the successor covers what is marked" is not required and not automatic — a successor `[2030-01-01, 2030-12-31]` replacing `[2020-01-01, ∞)` is permitted.

### (9.6) Audit
One `audit_log` row inside the transaction: action `orgunit-replace`, target `org_unit_version:{successor's first version id}`, detail = predecessor org-unit id · successor org-unit id · the marked version ids · the reason. Reusing Thêm's `orgunit-add` action is a defect: an audit query could not tell a replacement from a declaration or connect predecessor to successor. What the gesture owes the future `operation` table is in `docs/design-operation-history.md` §1.3, §4.1 and §5.

### (9.7) Invariants
- **R1.** After a successful replacement the predecessor has no active version row.
- **R2.** Every predecessor row that was active carries `status = 'replaced'`, `isactive = 0` and `replaced_by_org_unit_id` = the successor's identity id.
- **R3.** No predecessor row's `operation_kind` or business columns change.
- **R4.** The successor has exactly one version row, `operation_kind = 'Replace'`, over the period the operator supplied.
- **R5.** The successor's parent is the one the operator supplied, which may differ from the predecessor's.
- **R6.** No failure leaves a durable row. A replacement guard fails before the mint, so no row survives and no `org_unit` auto-increment value is consumed — but `[4f]` and `[4g]` fire after the mark, so an UPDATE ran and was rolled back; never claim "nothing was written". An inherited check fails after the mint: the transaction rolls back, but InnoDB does not reuse the allocated id — the same as Thêm. Identity ids are not gapless and are never shown as a business number.
- **R7.** Totality holds at the moment of replacement, not permanently: a later Đóng or Thay thế of the successor legitimately ends coverage of that code.

### (9.8) Recorded data and future transactions
- **Frozen history** (requester 2026-09-04): data already recorded is past data, preserved and not affected by later changes. A recorded transaction references a version row, which a replacement only flips (`isactive`, `status`) and never deletes or rewrites, so frozen history reads what it read before. That is why the successor may carry a different org code.
- **Rulings binding the transaction module** (requester 2026-08-22), to carry into its design:
  - A transaction references an org unit by `org_unit_id`, never by `org_code`.
  - At most one holder of an org code is effective at any instant; only history holds more than one.
  - Adjustment: transaction x stays unchanged, bound to its identity, labelled `Adjusted`, and is never adjusted again. A copy y (`Adjusting`, dated at adjustment completion, adjustable later) binds to the holder of x's org code effective at x's date that is **not marked replaced** — a closed holder keeps its claim, a replaced one loses it. y records that it was re-bound and from which identity.
  - Every printed document carries its transaction code, which is the lookup key; date, status and org code together are a search aid, not a key.
  - The app keeps the full change history of an org code. The data exists (every version row keeps its code); a read listing who held a code and when does not.
- **Open, for the requester when the transaction module is designed:** a report of "everything belonging to this unit" across a replacement must follow `replaced_by_org_unit_id`, not the live org code.

### (9.9) Concurrency posture (E1)
Requester 2026-08-22: *"Nguyên tắc tại một thời điểm chỉ một admin được thao tác trên app luôn đúng. Các chức năng sau này sẽ củng cố và khóa chặt nguyên tắc này."* E1 — one active administrator at a time — is what makes the window between reading a unit's state and writing a replacement acceptable; no code-scoped serialization enters this design. E1 is load-bearing for data correctness: an unattended import resolving an org code depends on it. If E1 is ever relaxed, revisit this design before the relaxation ships.

### (9.10) Not covered
- No other entity: `replaced_by_org_unit_id` is org-unit-only; `chk_rv_status` and `chk_rpv_status` do not admit `'replaced'`. Nothing here may reach `VersionedRepository`'s public or protected surface.
- No change to the 8-case algebra, the P11 auto-cut or the reverse-FK validator; they are called, never modified.
- The import zero-match error shape belongs to the transaction module.
