# Foundation Layer + IAM Module Design (AST) — Design / Spec

> **Status:** APPROVED 2026-07-02 (brainstorming/design session, not yet coded).
> This is the original version kept in the repo (the reference for the detailing step).
>
> **⚠ UPDATE 2026-07-02 — the IAM data model has been upgraded:** every IAM table now follows the **header+version temporal effective-period model** (identity card + version table, resolved by date, 8-case algebra, strict temporal-FK). Technical source of truth: **`docs/design-effective-period.md`** + skill `rule-effective-period`. §③ (schema) and §④ (base repository) below keep the same intent, but the **concrete schema follows header+version** as detailed in the effective-period document, by the detailing step. Data technology: **Dapper + MySqlConnector** (closed).

## Context — why this work
AST's empty Shell has already been built and runs (Prism.DryIoc + a WPF-UI FluentWindow + a directory-scanning module catalog that scans `Modules/`, with a content region in the shared kernel). No business module exists yet. Before building any business feature, the **Foundation Layer (SharedKernel) + the IAM module's data model** must be designed — because every future module depends on permissions + org-unit data filtering. Getting the foundation wrong means every later module inherits the flaw.

**This round is DESIGN ONLY** (no code, no main-screen UI). The result is a spec to detail in the next step.

Decisions closed through an interview with the requester (non-technical, Vietnamese) in this session:
- Log in with a **Windows/domain (AD) account** — no password, no login screen.
- Org units form a **multi-level parent–child tree**.
- Each user belongs to **exactly 1 org unit + 1 role** at any given time.
- Data scope is attached per **(role x function)**, **4 levels**.
- The root admin is managed via a **list of usernames in a config file** (break-glass), independent of the DB.
- MySQL is **Community** (not Enterprise), and **enabling KDS on the domain controller is difficult**.

## Scope of this round (design only — confirm nothing outside this scope is touched)
- **Only create/edit design documents.** NO code, NO migrations, NO UI.
- Assemblies expected to be touched at the LATER IMPLEMENTATION STEP (not this round): the shared kernel (adding Foundation-Layer contracts), a new module `AST.Modules.IAM` (new), a config file next to the Shell. **Not** touching the Shell app itself (thanks to the directory-scanning module catalog).

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
Which entities carry an effective period at all is decided in `design-temporality-classes.md` (its §5 register is the single home for that answer); the five listed here are Declared and therefore carry the standard columns. A NEW IAM table is not Declared by default — run that document's test before giving it period columns.
**Standard columns of a Declared table** (all 3): `isactive` (1/0); `effective_from` (required); `effective_to` (required, "not yet determined" = `9999-12-31`). Edit = a new record + set `isactive=0` on the old one; delete = set `isactive=0`; the old primary key is never reused (`rule-soft-delete`).
- **Org unit:** `id`, `code`, `name`, `parent_id` (tree) + standard columns.
- **Role:** `id`, `code`, `name` + standard columns.
- **User:** `id`, `username` (login key, `example\` stripped), `sid` (the app **captures it itself the first time the user logs in**, only for cross-check/audit), `display name`, `org_unit_id`, `role_id` + standard columns. The admin only enters the `username` + selects the org unit/role.
- **Role-permission:** `role_id` x `function_key` x `scope_level` (1 of 4) + standard columns.
- **Function catalog:** the source of truth = code; `function_key` = a string constant developers set following the `Module.Entity.Action` convention (e.g. `Transfer.Report.View`), stable across builds. The app syncs it out to a **lookup table**: `function_key` (technical, internal), `business code` (short, for display — e.g. `FX002`), `display name`, `menu group`. The admin does **not** add functions, only **grants permissions**.
  - **Catalog sync ↔ temporal-FK (C2, approved 2026-07-03; re-brainstormed 2026-07-04 — details + rationale: `docs/design-function-catalog-sync.md`):** `function` is the **parent** table of `role_permission` (D8, `docs/design-effective-period.md §5`). When the app syncs from code, the `function` version's `effective_from` is set to a **fixed epoch `2000-01-01`** (open period `9999-12-31`) — so that a permission with an early start date is not wrongly blocked by strict temporal-FK. **Automatic sync ONLY ADDS new functions + EDITS metadata.** A function **absent from code is NOT auto-closed** (because "absent" could be a temporarily-failed/unloaded module — auto-closing would wrongly cut permissions) — the app **flags it as "suspected removed"** for the **admin to confirm** before it is closed. A `function_key` that was removed and later re-added (**re-add**) = **reuses the exact same identity** (one key = one function identity for its whole lifetime, never duplicated); the app flags it as "suspected restored" for the admin to **reopen**, the old permissions stay closed and the admin re-grants them.

## (4) Base repository enforcing "3 conditions" (prevents omission in every future module)
- The shared kernel provides a **shared base repository/query** that forces every "fetch currently-usable data" query to filter **simultaneously**: `isactive=1` **and** within the effective period (`effective_from <= now < effective_to`, with `effective_to=9999-12-31` meaning "not yet determined") **and** within the org-unit scope per the data-scope value. Modules **must not write this filter themselves** → nobody can forget it.
- The "Own org unit + descendants" level needs the full subtree: use a **recursive CTE** (MySQL 8+/9) or a closure table — the technical choice is closed at the implementation step.

## (5) DB connection + Config protection + Root-admin break-glass
**Infrastructure constraints (imposed by the organization):** ACL/policy cannot be adjusted; AD has users only, NO group membership; no KDS; MySQL **Community** (verified: no Windows/Kerberos/LDAP authentication → a connection string must be stored). Architecture chooses **Option 2** (client connects straight to MySQL, 2 layers). Split **2 secrets into 2 independent parts**, both **encrypted files on the share**:
- **File A — DB connection (shared, every user reads it at bootstrap):**
  - **Secret (cannot be truly hidden at these 2 layers):** only **cosmetic encryption** (AES, key shipped with the app) — blocks casual viewing/plaintext leakage, does NOT block a technically savvy user.
  - **Real protection = on the DB side (done by the DBA, without touching org AD/ACL):** a dedicated DB account with **minimum privileges** (`SELECT/INSERT/UPDATE` on the app schema; NO `DELETE/DROP` → forces soft delete at the DB layer; no other schema; not an admin) + **host/subnet restriction** (`ast_app@'subnet.%'` → a leaked connection string is useless outside the app network) + **DB auditing**.
  - Edited via a planned **admin screen**.
- **File B — Root-admin break-glass (special, kept separate):**
  - **Does NOT use DPAPI-NG** (KDS is hard to enable). The file contains a **list of root-admin usernames** (e.g. `alice`); the app compares the **currently logged-in Windows username** → a match = full-authority root admin (Global scope), independent of the DB, **with logging**.
  - **The barrier = the Windows identity** (cannot be impersonated without that person's Windows password).
  - The root admin exists to create the initial org units/roles/users + to rescue the system if DB permissions are locked out by mistake; day-to-day operation uses a **"System Administrator" role in the DB**.
  - ⭐ **The ROOT ORG UNIT is break-glass-only for every write.** Declaring one, adjusting one, and closing or cancelling one are all reserved to a break-glass admin; an ordinary admin, however wide their data scope, may only READ it. Enforced in `OrgUnitDeclarationService` on all three paths — `OrgUnit.RootNotDeclarable` (Add), `OrgUnit.RootNotEditable` (Edit, which includes the "the unit ends on that date" gesture) and `OrgUnit.RootNotClosable` (Close/Cancel) — and each permitted write records a second `audit_log` row (`orgunit-root-add-breakglass` / `orgunit-root-edit-breakglass` / `orgunit-root-close-breakglass`) so that "a normally-forbidden operation was permitted" is a fact a security review can query for rather than infer from `parent_id`. **Consequence:** a fresh database has no root, so its first org unit can only be declared by a break-glass admin — which is what File B's own purpose above already says.
- **Both File A & B are protected against tampering/forgery with a DIGITAL SIGNATURE (instead of an unusable ACL):**
  - Use a **self-generated asymmetric key pair** (RSA/ECDSA via .NET's built-in cryptography libraries) — **free, no CA purchase, no expiry**. **Asymmetric is mandatory** (no HMAC/symmetric, because a symmetric key shipped inside the app could be extracted to forge signatures).
  - IT keeps the **private key offline** (an admin machine, never on the share), using a **small signing tool** to sign the file's content every time it's updated. The app **embeds the public key** (harmless if exposed), **verifies the signature before use**; a wrong/missing signature → **refused** (no DB connection / root admin not recognized). Losing the private key → generate a new pair, re-sign, ship the app with the new public key.
  - ⇒ No ACL/AD group/KDS/server needed: nobody can forge valid content without IT's private key; a sneaky overwrite → the app detects & refuses it; corruption/mistaken edits → restore from the **original copy in source control**.
  - The app **logs** whenever signature verification fails and whenever the root-admin path is used.
  - **A build-time on/off flag:** `RequireConfigSignature` — **Debug = off** (dev runs immediately, no signing needed), **Release = on** (prod). The flag lives **outside the signed file** (tied to the build configuration) → toggling it does not change any logic.
  - **Signing process (overview):** (A) generate **1 key pair** (RSA 3072/ECDSA) offline — `private` kept by IT (backed up, never on the share), `public` embedded in the app's source; (B) IT runs a **separate signing tool** (a small console project, NOT shipped to users) → produces a `.sig` file next to File A/B every time the config is set/changed; (C) the app runs the signature-verification step at startup, and refuses + logs on failure. The key **never expires**; if a key leaks → generate a new pair + rebuild (new public key embedded) + re-sign (lightweight since config rarely changes).
- **To truly hide the DB password** ⇒ the only way is **Option 1 (adding a middle-tier service that holds the password on a server, with the client authenticating via Windows)** — noted as a **future upgrade path**, out of scope for this round.

## (5b) Dev environment vs. production (config changes only, NO code changes)
- **Dev (laptop, client=server):** Debug build (signature OFF) + MySQL `ast_app'@'localhost'` + run the app from a **local folder** (e.g. `D:\ASTDeploy\`) simulating the share. Paths are resolved via the app's base directory (relative) → running from a local folder or a UNC share behaves identically.
- **Going to production — only change things outside the code:** Release build (signature ON); the DBA changes the account host `ast_app'@'localhost'` → `'10.20.30.%'` (`RENAME USER` or create new); change **the connection string in File A** (server localhost → the LAN server IP) via the admin screen; **copy the folder** to `\\server\share\AST\`; IT **signs File A/B once**.
- **Mandatory principle for a lightweight environment switch:** NEVER hardcode a connection string in the code (always in File A); NEVER hardcode an absolute path (always relative to the app's base directory). The signature + subnet restriction can be off in dev, on before going to production — as long as the code reads/checks via configuration, not hardcoded values.

## (6) Menu contribution (a module contributes a leaf into a shared group)
- **Menu group codes** (e.g. `Config.Security`, `Config.Params`) are defined in the **shared kernel** — **owned by no module**.
- Each module declares: `{ leaf (= function_key) + parent group code + required permission + order }`. The Shell gathers **all** declarations, builds the **Configuration** menu tree, shows/hides a leaf per the authorization service.
- ⇒ Module B's leaf can sit under a group that module A also contributes a leaf to, **because both only reference the shared-kernel group code, with no cross-module reference** (per `rule-module-boundary`). Adding/removing a leaf = edited within that same module, without touching the Shell/other modules.

## (7) Function identity (function registry) — feeds authorization + menu + dashboard
Each module registers **once** per function: `{ FunctionKey (technical, internal), BusinessCode (e.g. FX002, for display), DisplayName, MenuGroupCode, NavigationTarget (opens the right screen), RequiredPermission, (icon, order) }`. One declaration serves **3 purposes**:
1. Authorization (the role-permission table's `function_key`).
2. Building the menu (leaf + group + permission).
3. The "5 most-used functions" dashboard + shortcuts (counts usage by `FunctionKey`, reopens via `NavigationTarget`).
Day-to-day UI (menu, dashboard, reports) displays **BusinessCode + DisplayName** (e.g. `FX002 - Money-transfer sales report`); `function_key` is only used internally for matching permissions. Because the identity is standardized from the start, the dashboard won't need to be redesigned later. (The "function-usage log" table is journal data — built alongside the dashboard, DEFERRED for this round.)

**Built (2026-07-11) — purpose 2 (menu):** the "Shell builds the menu from the registry" part is now coded as `ISidebarMenuBuilder`/`SidebarMenuBuilder` (`AST.Shell/Navigation/`), building a 2-level tree from `IFunctionRegistry` ∪ a UI-only placeholder seed ∪ group metadata. Group display metadata (name/icon/order — not in the spec's `MenuGroupCode` alone) is a new `MenuGroup` record (`AST.Core/Iam/`) listed by `MenuGroupCatalog`. Settled model + rendering seam: `docs/sidebar.md`.

---

## (8) Operations & deployment infrastructure (shared kernel) — addition approved 2026-07-03
> Source: `docs/archive/2026-07-03-addendum-proposals.md` (architecture review, approved item-by-item by the requester through Q&A). The items below **FILL OPERATIONAL GAPS**, without reopening decisions D1–D13.

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

**(8.5) Testing (B5).** **xUnit + built-in asserts** (minimal, no extra assert library). **FluentAssertions ≥ v8 is BANNED** (it moved to a commercial license — a trap already recorded in the wpf-dev-pack feedback repo). The effective-period engine must have: unit tests covering all **8 algebra cases** (injecting a fake business-date-provider abstraction) + an integration test against local MySQL for the named lock/recursive CTE (matching `docs/design-effective-period.md §11`).

**(8.6) Transient-connection retry (C4).** One shared policy at the data layer in the shared kernel: timeout + a **short backoff retry** for transient errors (MySqlConnector can classify the exception type); a prolonged failure → raises a **"DB connection status"** signal (admin dashboard, per `AST.md`). Modules must NOT write their own retry logic. **Write safety:** only retry when it's certain the command has NOT yet reached the DB (e.g. an error right when opening the connection); a write cut off mid-flight must **NOT be retried blindly** — report the error so the business flow can check (to avoid a duplicate write).

> **Note on C3 (❌ removed):** the proposal to plan around "MySQL 9.7's support lifecycle" was dropped because its premise was wrong — **MySQL 9.7 is itself the LTS release** (released 2026-04-21). Keeping MySQL 9.7 in the tech stack is standard; no action needed.

---

## Items needing confirmation from the organization's IT (hand this list to IT/DBA)
1. Workstations are **AD domain-joined**; the app can read the currently logged-in Windows `username`.
2. **A dedicated, minimum-privilege DB account for the app** (`SELECT/INSERT/UPDATE` on the app schema, no `DELETE/DROP`) **+ host/subnet restriction** **+ DB auditing** — configured by the DBA on the MySQL side (without touching org AD/ACL). Example: `CREATE USER 'ast_app'@'10.20.30.%' IDENTIFIED BY '<pwd>'; GRANT SELECT,INSERT,UPDATE ON ast_db.* TO 'ast_app'@'10.20.30.%';` (replace `10.20.30.%` with the real workstation IP range; MySQL automatically blocks connections from outside the range, the app just uses a normal connection string). Note NAT/VPN can change the source IP; enabling `skip_name_resolve` means using IPs, not hostnames.
3. Confirmation that **ACL/policy cannot be used** and **AD has no group membership** ⇒ config-file tamper-protection uses a **digital signature** (IT keeps the private key offline + a signing tool; the app embeds the public key to verify).
4. Confirmation of **MySQL Community** (not Enterprise) ⇒ keep Option 2 + the protections above.
5. Where to place File A/B on the share + the recovery process (keep the **signed original** in source control).

## Acceptance criteria (for this design — the basis for independent grading)
- Sufficient definitions: the IAM tables + standard soft-delete/effective-period columns; the authorization-service contract (2 levels) + the 4-level data-scope value; the function-registry contract (metadata sufficient to feed authorization + menu + dashboard); the base repository enforcing 3 conditions; the menu-contribution mechanism via shared-kernel group codes; the DB-connection mechanism + the 2 config files (A/B) + username break-glass.
- No conflict with `rule-soft-delete` (every IAM table applies soft delete + effective period; no hard delete; edit = a new record) and `rule-module-boundary` (IAM is 1 assembly; communication only via the shared kernel; entities stay internal, only interfaces+DTOs are exposed; adding a module never touches the Shell).
- Every decision must trace back to a requester answer; no "assumed on our own" item.

## Next steps (handoff — NOT performed within this design round)
1. The requester approves this spec. ✅ (approved 2026-07-02)
2. Detailing step: the concrete schema (column types, keys, indexes, subtree CTE), contract signatures (the authorization service, the function registry, the base repository, DTOs). ✅ **COMPLETED 2026-07-03 → `docs/design-iam-schema.md`** (closed Q1 org_unit.parent_id strict temporal-FK, Q2 sid in the header, Q3 the scope-level enum). The 2-file (A/B) config structure stays in §5 of this document.
3. Data layer: migrations + the base repository + IAM entities/DTOs. Service layer: the authorization service. Both are graded independently against the Acceptance criteria.
4. Steps 2–3 are a **separate** code phase requiring its own approval, not part of this round.

## Technical points to verify at implementation time (checked provisionally, verify again when coding)
- **Already verified:** MySQL Community has NO Windows/Kerberos/LDAP authentication (Enterprise only) → a connection string must be stored.
- **Already verified:** classic DPAPI is machine-bound; DPAPI-NG (protect-to-SID) can roam but needs a domain key → **DPAPI-NG is dropped**; break-glass = a **username list + a self-generated digital signature** (NOT ACL-based, since the organization won't allow adjusting ACL/AD groups).
- Needs verification when coding: recursive CTE on MySQL 9.7 for the org-unit subtree; the .NET 10 API for reading the logged-in Windows identity/username; how the config file is encrypted/signed/written (using libraries shipped with the app package, not installed on the workstation).
