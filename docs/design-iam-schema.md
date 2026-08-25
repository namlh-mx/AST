# Detailed Design: IAM Module Schema + Effective-Period Engine Contracts (AST)

> **Status:** DESIGN DOCUMENT (design-only), detailed from 2 approved specs; 3 ambiguous points (Q1–Q3) were settled in the 2026-07-03 session. **NOT YET coded, NOT YET migrated.**
> **2026-07-03: PASSED WITH CONDITIONS** — no violation of the hard invariants / D1–D13 / rule-*. Issue #1 (the `function` metadata-sync rule) was resolved in §1.4. R1–R5 were merged into this document (§3, §1.3, §4) or into the code task's acceptance criteria (notes at the end of the file).
> **Technical source of truth:** `docs/design-effective-period.md` (D1–D13, §1–§12). **IAM spec:** `docs/design-iam-foundation.md` (①–⑧). **Original business requirements:** `docs/effective-period-requirements.md`. **Terminology:** `docs/glossary.md`.
> **Anti-drift:** this file POINTS to the canonical documents; it does NOT copy business rules.

## Scope (confirming nothing outside scope is touched)
- This round's deliverable = **1 design document**. NO C# code, NO migration scripts, NO UI. No source files under `AST/`, `AST.Core/` are touched.
- Assemblies that WILL be touched in a **later code phase (separately approved)**, not this round:
  - `AST.Core` — add the effective-period engine contracts + shared IAM contracts (SharedKernel).
  - `AST.Modules.IAM` (new module) — IAM Repository/Entity/DTO/Service; communicates outward only via SharedKernel + Region/EventAggregator (`rule-module-boundary`).
  - `migrations/` — numbered SQL scripts creating the tables (written in the later phase).
- Contracts live in `AST.Core` per `rule-module-boundary` (Interface + DTO in SharedKernel; Entity/Impl inside the module).

## Three ambiguous points SETTLED in the 2026-07-03 session (no unstated assumptions)
- **Q1 — `org_unit.parent_id` IS a STRICT temporal-FK edge.** ✅ The requester settled: a child org unit's effective period **must** be continuously covered by the parent's period end-to-end; declaring a child beyond the parent's period → **BLOCKED**. This edge is registered in the temporal-FK edge registry (multi-level, consistent with D8 "multi-level check").
- **Q2 — `sid` is placed on the IDENTITY table `user` (header), not on the version.** ✅ Settled for a technical reason: `sid` is a **stable, unchanging** identifier of a person; "capture on first login" = a single write into an already-existing record; if it were on the version, the capture operation would be an UPDATE of a business column → **violating hard invariant #1** (append-only). The header satisfies both spec ① and the append-only rule.
- **Q3 — the shared scope-level enum keeps a short name (no leading "Data").** ✅ `docs/glossary.md` updated to match.

---

## 1. DDL — 5 IAM table pairs (header + version) per the §1 template
Common convention for every table: `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci`. The `..._ai_ci` collation = accent-insensitive + **case-insensitive** → satisfies "username is case-insensitive" (spec ①) right at the comparison layer, no manual `lower()` needed.

**Reserved word:** `user` and `function` are MySQL keywords → **backticks are mandatory** everywhere (DDL + queries). `role`, `role_permission`, `org_unit` are safe but are backticked for consistency.

Every `_version` table carries exactly the standard effective-period + audit column set from §1: `effective_from`, `effective_to` (DEFAULT `'9999-12-31'` = open period), `isactive` (DEFAULT 1), `recorded_at`, `recorded_by`, `reason`; `CHECK(effective_from <= effective_to)`; resolution index `(<name>_id, isactive, effective_from, effective_to)`.

### 1.1 `org_unit` — org unit (anchor template §1)
```sql
CREATE TABLE `org_unit` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `org_unit_version` (
  id                       BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  org_unit_id              BIGINT UNSIGNED NOT NULL,       -- FK -> org_unit(id) (IDENTITY)
  org_code                 VARCHAR(8)   NOT NULL,          -- business code / natural key (P6); app: 4-8, letters+digits, ALL CAPS
  org_name_full_vn         VARCHAR(100) NOT NULL,          -- full VN name (legal profile)
  org_name_short_vn        VARCHAR(100) NOT NULL,          -- short VN name (internal management)
  parent_id                BIGINT UNSIGNED NULL,           -- FK -> org_unit(id) (parent IDENTITY); resolved by date; NULL = root
  -- supplemental (optional; declaration-screens spec §2.4) --
  org_business_number      VARCHAR(14)  NULL,
  org_addr_line_vn         VARCHAR(255) NULL,
  org_addr_line_en         VARCHAR(255) NULL,
  org_addr_ward_vn         VARCHAR(255) NULL,
  org_addr_ward_en         VARCHAR(255) NULL,
  org_addr_district_vn     VARCHAR(255) NULL,
  org_addr_district_en     VARCHAR(255) NULL,
  org_addr_province_vn     VARCHAR(255) NULL,
  org_addr_province_en     VARCHAR(255) NULL,
  org_admin_division_level TINYINT      NOT NULL DEFAULT 2, -- 2 or 3; excluded from the x/y supplemental progress
  org_name_full_en         VARCHAR(100) NULL,
  org_name_short_en        VARCHAR(100) NULL,
  org_phone                VARCHAR(15)  NULL,
  org_fax                  VARCHAR(15)  NULL,
  org_email                VARCHAR(255) NULL,
  org_reserve_1            VARCHAR(255) NULL,
  org_reserve_2            VARCHAR(255) NULL,
  org_reserve_3            VARCHAR(255) NULL,
  -- durable lifecycle marker (V010, replaced a `cancelled` TINYINT). AST.Core.Data.VersionLifecycleStatus.
  -- COLLATE _as_cs is load-bearing: under the tables' default _ai_ci, `status = 'cancelled'` also matches
  -- 'Cancelled' and 'cancelléd', which pass the CHECK and then fail to materialize as an enum name.
  status                   VARCHAR(10)  COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal',
  -- the successor IDENTITY; set only on a `replaced` row and required there (chk_ouv_status below).
  replaced_by_org_unit_id  BIGINT UNSIGNED NULL,
  -- per-row action recorded on write (Add/Edit/Close/Cancel/Replace) -- Phase 4d history-grid read; nullable,
  -- no backfill for pre-4d rows; enum persisted verbatim via ToString()/Enum.Parse (no other enum-to-column
  -- precedent exists). NO CHECK constraint -- VARCHAR(10) is the only limit.
  operation_kind           VARCHAR(10)  NULL,
  effective_from           DATE NOT NULL,
  effective_to             DATE NOT NULL DEFAULT '9999-12-31',
  isactive                 TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at              DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by              VARCHAR(100) NOT NULL,
  reason                   VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_ouv_res    (org_unit_id, isactive, effective_from, effective_to),
  KEY idx_ouv_code   (org_code, isactive, effective_from, effective_to),   -- backs the P6 overlapping-code lookup
  KEY idx_ouv_parent (parent_id, isactive, effective_from, effective_to),  -- subtree CTE + parent temporal-FK
  CONSTRAINT fk_ouv_ou     FOREIGN KEY (org_unit_id) REFERENCES `org_unit`(id),
  CONSTRAINT fk_ouv_parent FOREIGN KEY (parent_id)   REFERENCES `org_unit`(id),
  -- ⚠ InnoDB auto-creates a supporting index for this FK -- `KEY fk_ouv_replaced_by
  -- (replaced_by_org_unit_id)` -- because no index above left-prefixes that column. VERIFIED by
  -- SHOW CREATE TABLE on MySQL 9.7.1 (AI Agent AST-CONSULT-147/F-05). The blocks in this file are the
  -- DECLARED DDL, not a physical-schema dump; that index is the one thing the two differ by.
  CONSTRAINT fk_ouv_replaced_by FOREIGN KEY (replaced_by_org_unit_id) REFERENCES `org_unit`(id),
  CONSTRAINT chk_ouv_period CHECK (effective_from <= effective_to),
  -- the lifecycle invariant, enforced by the DATABASE and not by a promise a write path makes:
  -- the status domain, `cancelled|replaced => isactive = 0`, and successor-link coherence in BOTH
  -- directions (a `replaced` row has a link; nothing else may carry one).
  CONSTRAINT chk_ouv_status CHECK (
       (status = 'normal'    AND replaced_by_org_unit_id IS NULL)
    OR (status = 'cancelled' AND isactive = 0 AND replaced_by_org_unit_id IS NULL)
    OR (status = 'replaced'  AND isactive = 0 AND replaced_by_org_unit_id IS NOT NULL)
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
> **Field set finalized 2026-07-22** for the org-unit declaration screen (rename `ma→org_code`, `ten→org_name_full_vn`, add `org_name_short_vn` + the supplemental catalog + the `cancelled` marker). Source of the requirement: the IAM declaration-screens business analysis §2.2/§2.4/§2.6. Applied to `migrations/V001__org_unit.sql`. `role` was renamed too (`ma→role_code`, `ten→role_name`, 2026-08-08, see §1.2) — `user`/`function` still keep `ma`/`ten` until their own screens.
> **V010 (2026-08-24) replaced the `cancelled` TINYINT with a `status` VARCHAR(10) on all three version tables**, plus `replaced_by_org_unit_id` on `org_unit_version` only. One column, not two: a boolean beside an enum carrying the same value admits rows that are both and rows that are neither, with no rule saying which wins (design spec §14.3). The invariant `cancelled|replaced ⟹ isactive = 0` is now a row-level `CHECK` (`chk_ouv_status` / `chk_rv_status` / `chk_rpv_status`), **not a promise made by a write path** — that distinction is the whole point of the migration (spec §15.2). `'replaced'` is admitted by `chk_ouv_status` ONLY; role and role-permission versions cannot legally carry it in v1. The `status` columns carry **`COLLATE utf8mb4_0900_as_cs`**, without which the tables' accent- and case-insensitive default would let `'Cancelled'`/`'cancelléd'` satisfy the CHECK and then fail to parse as an enum name (AI Agent `144`/F-02, proven by a run). V010 is arranged in **four phases** — legacy-domain gate, add, backfill + CHECK, and only then destroy — because MySQL implicitly COMMITs each DDL statement, so an abort in any earlier phase must leave `cancelled` intact on all three tables (AI Agent `144`/F-01). ⚠️ DESTRUCTIVE — see V010's own header for the shutdown requirement and the manual recovery path.
> **`operation_kind` added 2026-07-31 (Phase 4d)** — per-row action (Add/Edit/Close/Cancel; **`Replace` added 2026-08-24**, five values) so the history grid can show WHICH action produced each version row, without heuristically inferring it from the 8-case algebra outcome. See 2026-07-31 and `AST.Core.Data.VersionOperationKind`. Base-repo opt-in flag: `VersionedRepository.RecordsOperationKind` (mirrors `SupportsCancellation`'s pattern) — `org_unit_version`, `role_version`, and `role_permission_version` have this column (2026-08-08).
- FK points to the **identity**: both `org_unit_id` and `parent_id` → `org_unit(id)` (a live link, D3/§2). `parent_id` NULL = the tree's root unit.
- `parent_id` sits on the **version** (per the §1 template) → changing the parent = a new version (re-parenting over time). This is why the subtree must resolve the parent by date (§4).
- **[Q1 settled] `parent_id` is a STRICT temporal-FK edge:** a child `org_unit_version`'s period must be continuously covered end-to-end by parent org-unit versions; the edge is registered in the temporal-FK edge registry (§1.6, §3). The root unit (`parent_id IS NULL`) is exempt from the check.

### 1.2 `role` — role
```sql
CREATE TABLE `role` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `role_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  role_id        BIGINT UNSIGNED NOT NULL,       -- FK -> role(id) (IDENTITY)
  role_code      VARCHAR(20)  NOT NULL,          -- natural key (P6)
  role_name      VARCHAR(100) NOT NULL,
  is_admin_role  TINYINT(1) NOT NULL DEFAULT 0,  -- version-level; <=1 admin-flag role active per day (N-14)
  -- lifecycle marker (V010, replaced a `cancelled` TINYINT). AST.Core.Data.VersionLifecycleStatus.
  -- COLLATE _as_cs is load-bearing here for the same reason as on org_unit_version (§1.1).
  status         VARCHAR(10) COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal',
  operation_kind VARCHAR(10) NULL,               -- Add/Edit/Close/Cancel/Replace (Phase 4d history)
  effective_from DATE NOT NULL,
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,
  reason         VARCHAR(255) NULL,              -- optional "Ghi chú"; deliberately nullable (Screen A parity)
  PRIMARY KEY (id),
  KEY idx_rv_res   (role_id, isactive, effective_from, effective_to),
  KEY idx_rv_code  (role_code, isactive, effective_from, effective_to),
  KEY idx_rv_admin (is_admin_role, isactive, effective_from, effective_to),
  CONSTRAINT fk_rv_role     FOREIGN KEY (role_id) REFERENCES `role`(id),
  CONSTRAINT chk_rv_period  CHECK (effective_from <= effective_to),
  -- no `replaced` value: replacement is org-unit-scoped in v1, so this CHECK makes it unrepresentable
  -- on a role version rather than merely unwritten.
  CONSTRAINT chk_rv_status  CHECK (
       status = 'normal'
    OR (status = 'cancelled' AND isactive = 0)
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
> **Field set updated 2026-08-08** — rename `ma`→`role_code`, `ten`→`role_name`; add `is_admin_role`, `cancelled`, `operation_kind` (mirrors org_unit's cancellation/history columns). Applied to `migrations/V002__role.sql`.

**Admin-flag write path (brief 059 + Fix Round 1/2):** `RoleRepository.AdminFlagLockKey` (`iam:role:admin-flag-singleton`) is the Seam-2 named lock a composite-write caller must `Enlist` before `ExecuteAsync` whenever composing an admin-flag change. `RoleRepository` has only ONE `UpsertAsync` — the composite-context overload (2026-08-16, backlog 0.3): the plain (non-composite) overload was deleted, and with it the `Role.AdminFlagRequiresCompositeWrite` guard that used to reject `isAdminRole: true` on that path. "Only a composite write can create a role version" is now a property of the type surface, not a runtime rejection; `AST.Meta.Tests/RoleWritePathAbsenceTests` is what keeps it that way. The surviving overload requires `adminFlagChangeAuthorized` (no default) for **either direction** of the flag: granting (`isAdminRole: true`) or revoking an existing admin flag on any active version that **overlaps the upsert period** (`isAdminRole: false` when such an overlapping admin version exists) returns `Role.AdminFlagChangeNotAuthorized` when unauthorized. The repository does **not** Enlist the lock itself (it does not own `CompositeWrite`). `RoleDeclarationService` (`AST.Modules.IAM`, closing OPEN-B2, see `docs/shared-components.md` §⑦) Enlists this key unconditionally on every Save, closing the race for that path; any other caller composing an `isAdminRole: true` Upsert directly is on its own honesty, backstopped only by the N-14 integrity sweep. **Contract-surface decision (settled 2026-08-08, extended 2026-08-16):** `IRoleRepository`/`IRolePermissionRepository` deliberately keep the composite-context overloads OFF the public interface — and `IRoleRepository` went further: it is now **read-only** (`UpsertAsync`, `CreateIdentityAsync`, `DeleteEmptyIdentityAsync` all removed), so role **version creation** is reachable only through `IRoleDeclarationService`. The narrow claim matters: `RoleRepository.CancelPlanAsync` and the inherited `CloseVersionAsync` remain public, so this is not "all role writes go through the service". `IRolePermissionRepository` and the other four IAM repositories keep their plain write overloads (extended 2026-08-17 TWICE: `IRolePermissionRepository` no longer declares `CreateIdentityAsync`/`DeleteEmptyIdentityAsync` — grant identities are minted inside the composite transaction that writes their first version, `design-effective-period.md` §7, so the interface can no longer produce or need to compensate a zero-version header; its plain `UpsertAsync` is unaffected; and `IOrgUnitRepository` lost the same two members for the same reason when org-unit **Add** moved behind `IOrgUnitDeclarationService` — backlog 0.4b, closing the last production mint that ran ahead of its own transaction. The org-unit claim was narrower than the role one until 2026-08-21, when **Edit moved behind `IOrgUnitDeclarationService` too (backlog 0.7)**: `IOrgUnitRepository` now declares NO write member, so what holds is the full claim — every org-unit version write goes only through that service — and the caller-selected `VersionOperationKind` residue went with it, since the kind is derived server-side on all three use-cases. `AST.Meta.Tests/OrgUnitWritePathAbsenceTests` guards the boundary on three legs) — the asymmetry is deliberate, Role Save owns the aggregate/admin-flag-lock behaviour that made an unlocked plain writer hazardous — a business-layer caller instead depends on the dedicated use-case contract `IRoleDeclarationService`, whose `internal` implementation depends on the CONCRETE repository classes.

### 1.3 `user` — user
`sid` is placed on the **header** (Q2 settled). The login key `username` is on the version (renaming = a new version).
```sql
CREATE TABLE `user` (
  id  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  sid VARCHAR(100) NULL,                    -- [Q2] stable metadata, captured once on first login, immutable thereafter
  PRIMARY KEY (id),
  UNIQUE KEY uq_user_sid (sid)              -- SID (if present) is unique; NULL does not count toward UNIQUE in MySQL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  user_id        BIGINT UNSIGNED NOT NULL,   -- FK -> user(id) (IDENTITY)
  username       VARCHAR(100) NOT NULL,      -- samAccountName, domain prefix ALREADY stripped; case-insensitive via collation
  display_name   VARCHAR(255) NOT NULL,
  org_unit_id    BIGINT UNSIGNED NOT NULL,   -- FK -> org_unit(id) (IDENTITY) — temporal-FK parent
  role_id        BIGINT UNSIGNED NOT NULL,   -- FK -> role(id) (IDENTITY)    — temporal-FK parent
  effective_from DATE NOT NULL,
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,
  reason         VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_uv_res      (user_id, isactive, effective_from, effective_to),
  KEY idx_uv_username (username, isactive, effective_from, effective_to),  -- resolve login by today
  KEY idx_uv_org      (org_unit_id, isactive, effective_from, effective_to),
  KEY idx_uv_role     (role_id, isactive, effective_from, effective_to),
  CONSTRAINT fk_uv_user FOREIGN KEY (user_id)     REFERENCES `user`(id),
  CONSTRAINT fk_uv_org  FOREIGN KEY (org_unit_id) REFERENCES `org_unit`(id),
  CONSTRAINT fk_uv_role FOREIGN KEY (role_id)     REFERENCES `role`(id),
  CONSTRAINT chk_uv_period CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
- **Login resolution:** today's Windows username → filter `user_version` by `username=@u AND isactive=1 AND effective_from<=@today<=effective_to` → yields `user_id` + the current (org_unit_id, role_id) (D5: permissions/scope resolved by **today**).
- **App-level invariant (MySQL cannot enforce this, similar to D6):** on any given day, **no** two different user identities may both have an active version carrying the same `username`. Validated at the app level + a named lock (§7), and included in the §12 integrity-check sweep.
- Each user has **1 org unit + 1 role per point in time** (spec ①): each version has only 1 `org_unit_id` + 1 `role_id`. Changing org unit/role = a new version.
- **`sid` write-once (R4, 2026-07-03):** filling in `sid` the first time = `UPDATE user SET sid=@sid WHERE id=@id AND sid IS NULL` (the `sid IS NULL` guard → no overwrite). The scenario spec ① worries about (a username **renamed/reused** for a different person — a different SID): the login logic detects a new SID ≠ the old identity's SID → **creates a new user IDENTITY**, does NOT overwrite the old sid. `sid` must NOT become a disguised mutable column.

### 1.4 `function` — function (synced from code, epoch C2)
```sql
CREATE TABLE `function` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `function_version` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  function_id    BIGINT UNSIGNED NOT NULL,   -- FK -> function(id) (IDENTITY)
  function_key   VARCHAR(150) NOT NULL,      -- Module.Entity.Action — stable technical key from code
  business_code  VARCHAR(30)  NOT NULL,      -- e.g. FX002 (display)
  display_name   VARCHAR(255) NOT NULL,
  menu_group     VARCHAR(100) NOT NULL,      -- points to the shared menu-group codes constant (SharedKernel)
  nav_target     VARCHAR(150) NOT NULL,      -- navigation target (opens the right screen)
  effective_from DATE NOT NULL DEFAULT '2000-01-01',  -- C2: fixed epoch
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,      -- 'system-sync' when synced from code
  reason         VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_fv_res (function_id, isactive, effective_from, effective_to),
  KEY idx_fv_key (function_key, isactive, effective_from, effective_to),  -- match during sync
  CONSTRAINT fk_fv_function FOREIGN KEY (function_id) REFERENCES `function`(id),
  CONSTRAINT chk_fv_period  CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
- **Synced from code (C2, spec ③) — re-brainstormed 2026-07-04 (details + rationale: `docs/design-function-catalog-sync.md`).** Append-only, NO UPDATE of business columns on the version (keeps hard invariant #1). **Automatic sync ONLY performs case 1 + case 2; "removal" and "restore" cases only FLAG a candidate for admin confirmation (never done automatically):**
  1. **A new `function_key` (no identity yet — never seen in the DB before):** [AUTOMATIC] create `function` + `function_version` with `effective_from='2000-01-01'`, `effective_to='9999-12-31'`. → the function gets an open period starting **very early** so that `role_permission` (the child) can declare any start period without STRICT temporal-FK wrongly blocking it.
  2. **`function_key` already exists (active today) but its METADATA changed** (`display_name`/`business_code`/`menu_group`/`nav_target` differs from the active version) [settled 2026-07-03, Issue #1]: [AUTOMATIC] handled per the **8-case algebra — case 7 "exact match"**: **soft-delete** the currently open version (`isactive=0`) + **insert a new version with the SAME period as the current active one** (a function synced from code is always `[2000-01-01, 9999-12-31]`; after an admin reopens it, it may be `[D, 9999-12-31]`) carrying the new metadata, `reason='metadata-sync'`. The sync takes the period FROM the current active version (no hard-coded epoch) so it always matches exactly, avoiding silently dragging `effective_from` back to 2000. Absolutely NO `UPDATE` of metadata columns on the old version. Because `role_permission.function_id` points to the **identity** (not a version id) → changing metadata does NOT break permissions; temporal-FK coverage stays sufficient (1 active version covers [2000,9999]).
  3. **`function_key` removed from code (still active today in the DB, no longer in the registry):** [NOT AUTOMATIC] the app **flags it as a "suspected removal" (removal candidate)** — it does NOT close it automatically. Reason: "absent from code" could be a temporarily broken/unloaded module → auto-closing would wrongly cut permissions; a temporary glitch self-heals on the next sync. **Admin confirmation** on the admin screen is what actually closes it: close dependent permissions first, then run the repository close operation (cutting the period back to the date the admin chooses, reverse-FK BLOCKS if a child still needs coverage) — soft-delete the old version + remnant, keep the audit trail, NO physical deletion.
  4. **`function_key` restored (re-add: an identity already exists but is NOT active today — it was closed before):** [NOT AUTOMATIC] **reuse the exact same old identity** — one `function_key` = one `function.id` for life (hard invariant #5, never spawn a duplicate identity). The app flags it as a "suspected restore" (reopen candidate); the **admin reopens** the old identity (adding a new effective period from the date the admin chooses → `9999-12-31`). Old permissions **stay closed**, the admin reassigns them. → "treated as new" shows up as a new effective period on the timeline; the identity + all historical links stay unchanged.
  - **Known limitation:** renaming a `function_key` = the sync sees the old key as "suspected removal" + the new key as "newly created" (it does not recognize a rename) → the admin handles it manually (reassigning permissions to the new key). To keep permissions: keep the old key unchanged in the code.
- `menu_group` = a constant value from the shared menu-group codes (SharedKernel) — a string, not an FK.

### 1.5 `role_permission` — permission (role x function x scope)
**Model 2:** one write identity per grant; revoke closes that identity. Each "Add function to role" mints a new `role_permission` identity; closing/revoking that grant closes that identity's open version (it does not reuse a single lifetime identity for the `(role, function)` pair). **REVERSED 2026-08-12 (requester):** `scope_level` is FIXED for the life of a grant identity. Changing the level is **revoke the old grant + create a new one**, never a second version of the same identity — so a grant identity holds exactly ONE version for life (plus the close remnant that ends it). The previous rule (a new version on the same identity) is what made privilege escalation possible: cancelling the narrower newer version restored the broader adjacent predecessor's `scope_level`, audited only as a cancel. That restore is correct behaviour for a version-plan cancellation and stays untouched in the engine; it simply must never have a predecessor to restore here. `role_id`, `function_id` are on the version per the §1 template and are **temporal-FK parent edges**. `RolePermissionRepository.RevokeAsync` is a thin wrapper over Seam-2 `CloseVersionAsync` (plain + composite-context) — that wrapper claim still holds. What sits above it changed: `RoleDeclarationService.SaveRoleDeclarationAsync` now derives, per revoked grant, whether to Retire (close) or Cancel-plan it, via the shared `VersionCloseRules.BranchFor` (`AST.Core/EffectivePeriod/VersionCloseRules.cs`) — see that type for the branch boundary itself, not restated here.
```sql
CREATE TABLE `role_permission` (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `role_permission_version` (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  role_permission_id BIGINT UNSIGNED NOT NULL,   -- FK -> role_permission(id) (IDENTITY)
  role_id            BIGINT UNSIGNED NOT NULL,    -- FK -> role(id) (IDENTITY)     — temporal-FK parent
  function_id        BIGINT UNSIGNED NOT NULL,    -- FK -> function(id) (IDENTITY) — temporal-FK parent
  scope_level        TINYINT UNSIGNED NOT NULL,   -- 1..4 = ScopeLevel
  -- lifecycle marker (V010, replaced a `cancelled` TINYINT). AST.Core.Data.VersionLifecycleStatus.
  -- COLLATE _as_cs is load-bearing here for the same reason as on org_unit_version (§1.1).
  status             VARCHAR(10) COLLATE utf8mb4_0900_as_cs NOT NULL DEFAULT 'normal',
  operation_kind     VARCHAR(10) NULL,
  effective_from     DATE NOT NULL,
  effective_to       DATE NOT NULL DEFAULT '9999-12-31',
  isactive           TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by        VARCHAR(100) NOT NULL,
  reason             VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_rpv_res  (role_permission_id, isactive, effective_from, effective_to),
  KEY idx_rpv_rf   (role_id, function_id, isactive, effective_from, effective_to),  -- lookup (role,function)
  KEY idx_rpv_role (role_id, isactive, effective_from, effective_to),
  KEY idx_rpv_func (function_id, isactive, effective_from, effective_to),
  CONSTRAINT fk_rpv_rp   FOREIGN KEY (role_permission_id) REFERENCES `role_permission`(id),
  CONSTRAINT fk_rpv_role FOREIGN KEY (role_id)     REFERENCES `role`(id),
  CONSTRAINT fk_rpv_func FOREIGN KEY (function_id) REFERENCES `function`(id),
  CONSTRAINT chk_rpv_period CHECK (effective_from <= effective_to),
  CONSTRAINT chk_rpv_scope  CHECK (scope_level BETWEEN 1 AND 4),
  -- no `replaced` value, same reason as chk_rv_status (§1.2).
  CONSTRAINT chk_rpv_status CHECK (
       status = 'normal'
    OR (status = 'cancelled' AND isactive = 0)
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
> **Field set updated 2026-08-08** — add `cancelled`, `operation_kind`; Model 2 wording locked (one write identity per grant). Applied to `migrations/V005__role_permission.sql`.
- **App-level invariant (Model 2, tightened 2026-08-12):** each grant action creates its own `role_permission` identity, which carries ONE version for life — `(role_id, function_id)` AND `scope_level` are all fixed once written; only the period's END moves, and only to end the grant. A second version on an existing grant identity is a defect, not a supported edit. **ENFORCED at the repository boundary (2026-08-13), on both writers.** The PERIOD half: both grant `Upsert` writers reject bounded ends and any start other than the operation date (no unchanged-start carve-out survives on either entity — the `role` one went 2026-08-13 with the ruling that a role edit takes effect from today rather than rewriting the running version). The IDENTITY half: `RolePermissionRepository.ValidateUpsertAsync` rejects an upsert on an identity that already has ANY version row with `RolePermission.IdentityAlreadyVersioned`. The probe is existence-**any**, not as-of-today and not `isactive = 1`, and the two filters fail on different shapes: an as-of-today read calls a REVOKED identity empty (its remnant ends yesterday), while an `isactive = 1` read still finds that remnant but calls a CANCELLED-only identity empty (its sole version is `isactive = 0, status = 'cancelled'`, with no predecessor to restore). Either filter would let such an identity be re-granted in place. It runs under the identity's named lock on both paths (see the `VersionedRepository` row in `docs/shared-components.md` for why the seam sits at the top of `ApplyUpsertPlanAsync`), so two cooperating writers racing on one fresh identity produce exactly one version. That lock does not serialise direct SQL, which the §12 duplicate-natural-key sweep still backstops. Two identities for the same `(role_id, function_id)` may not have overlapping active periods (D6-style, validated at the app level + §12 duplicate-natural-key sweep on `(role_id, function_id)`).
- **STRICT temporal-FK (D8):** saving `role_permission_version [F,T]` → `role_id` (via `role_version`) AND `function_id` (via `function_version`) must continuously cover the entire `[F,T]`. The `function` epoch `2000-01-01` ensures the function edge covers almost always; the role edge depends on the role's period.
- `scope_level` maps to the scope level: 1=Self, 2=OwnOrgUnit, 3=OwnOrgUnitAndDescendants, 4=Global.

### 1.6 Summary of temporal-FK edges (declared as metadata — §3)
| Child table (version) | FK column (→ parent identity) | Parent table (version) | Status |
|---|---|---|---|
| `user_version` | `org_unit_id` | `org_unit_version` | Settled (D8) |
| `user_version` | `role_id` | `role_version` | Settled (D8) |
| `role_permission_version` | `role_id` | `role_version` | Settled (D8) |
| `role_permission_version` | `function_id` | `function_version` | Settled (D8) |
| `org_unit_version` | `parent_id` | `org_unit_version` | **Settled [Q1] 2026-07-03 — self-ref, multi-level; the root unit (`parent_id IS NULL`) is exempt** |

---

## 2. Infrastructure auxiliary tables (§⑧ merged in — NOT temporal)
Operational tables; no header+version, no effective period.
```sql
-- ⑧.1 — App checks at startup: DB schema version matches what the app needs.
CREATE TABLE `schema_version` (
  version     INT NOT NULL,                 -- migration sequence number (V001 -> 1)
  applied_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  applied_by  VARCHAR(100) NOT NULL,        -- DBA who ran the script
  description VARCHAR(255) NULL,
  PRIMARY KEY (version)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ⑧.2 — App control command (scheduled shutdown for a new release); the app polls via the polling service.
CREATE TABLE `app_control` (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  command        VARCHAR(50)  NOT NULL,     -- e.g. 'shutdown'
  target_version VARCHAR(50)  NULL,         -- the new deployed version to switch to
  deadline_at    DATETIME     NULL,         -- deadline by which the app must close itself
  message        VARCHAR(500) NULL,         -- message displayed to the user
  is_active      TINYINT(1) NOT NULL DEFAULT 1,
  created_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  created_by     VARCHAR(100) NOT NULL,
  PRIMARY KEY (id),
  KEY idx_appctl_active (is_active, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ⑧.4 — Business/security audit, append-only (the ast_app account has NO DELETE => self-protecting against deletion).
CREATE TABLE `audit_log` (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  occurred_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  username    VARCHAR(100) NULL,            -- who triggered the event (if any)
  event_type  VARCHAR(50)  NOT NULL,        -- login | break-glass | signature-fail | permission-change | ...
  target      VARCHAR(150) NULL,            -- every role-declaration event, Save and Close/Cancel alike, targets the IDENTITY: role:{roleId} (2026-08-15). An audit row is NEVER rewritten, so the older shapes (role_version:{versionId} for pre-2026-08-15 Close/Cancel, role_permission:{grantId} for older grant rows) are documented as history only: AST has never been released, so no database this column will meet can hold one, and no reader carries a branch for them (amended 2026-08-15). Reinstate that branch only if a database written by, or imported from, a build older than 697cc26 ever appears
  detail      JSON NULL,                    -- detailed payload (before/after at minimum); every row a role-declaration gesture writes carries that gesture's operationId for grouping — Save and Close/Cancel alike (2026-08-15)
  PRIMARY KEY (id),
  KEY idx_audit_time (occurred_at),
  KEY idx_audit_type (event_type, occurred_at),
  KEY idx_audit_target (target, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
- `schema_version`: the last line of every migration script is `INSERT INTO schema_version(...)`. App: a build constant = the required version; mismatch → BLOCK + report (⑧.1).
- `audit_log`: **technical** logs do NOT go here (they go to local Serilog files, ⑧.4) — this table only audits business/security events.
- (The "function usage log" table for the dashboard: **DEFERRED** per spec ⑦.)

---

## 3. Shared engine contracts — intent and invariants (`AST.Core`)

The C# contracts live in code only (single home — directory pointers below; docs never copy signatures). This section records the intent and the invariants each contract family must uphold. Result style at service/infra boundaries: a blocking failure is an **error with a stable error code** (D8 temporal-FK, D9 missing coverage → operation stops); a day gap is a **warning carried in the success payload** (D7 → operation continues). Warnings are never errors.

| Family | Intent | Invariants | Source |
|---|---|---|---|
| Clock / business date (D13b, invariant #6) | Separate the technical clock from the business "today" | Technical clock is ONLY for app-side logging; `recorded_at` on version tables is set by the DB (`DEFAULT CURRENT_TIMESTAMP`) — one clock across ~30 clients, keeps polling deltas consistent (⑧.3) [R1, 2026-07-03]. Business "today" is captured ONCE per operation and drives permission + scope resolution (D5). | `AST.Core/Time/` |
| Effective-period base types | Closed period `[From, To]`; open period = To `9999-12-31` (D4) | `9999-12-31` is "infinity": no ±1-day arithmetic at that boundary (§4); the boundary rule lives in ONE place, reused by the period-edit planner and the temporal-FK validator. Every version DTO implements one marker (id, identity id, period, active flag) so the shared engine operates on any versioned table. | `AST.Core/EffectivePeriod/` |
| As-of resolution (§3, D9) | Pick the usable version at a date among candidates of ONE identity | Usable = active AND From ≤ asOf ≤ To. No match → blocking not-found error with a stable code — the caller STOPS and reports clearly (D9). Pure function over a candidate list (no DB) so it is unit-testable; direct-from-DB resolution is the base repository's job (§3). | `AST.Core/EffectivePeriod/` |
| Period-edit planning (§4, D7) | Compute the soft-delete/insert plan of the 8-case interval algebra for one identity | Implemented geometrically (overlap + head/tail remnant), no case enum. A remnant insert copies the OLD business data and only changes the period; a remnant op always references its source version (invariant: carries-old-data ⟺ has source version id). Day gaps → warnings; invalid input (From > To) → validation error. Planning never touches the DB. | `AST.Core/EffectivePeriod/` |
| Temporal-FK STRICT + edge registry (§5, D8) | Parent-child edges declared as metadata (no hardcoding scattered around); a child period must be covered CONTINUOUSLY by its parent's coverage | Edges are queryable per child (validate on save) and per parent (reverse-FK when shrinking/closing a parent: any dependent losing coverage → BLOCK, fix children first). Coverage providers are INJECTED so the validator stays pure [RATIFIED 2026-07-03]; DB-backed providers (data layer, via the base repository) filter ONLY active + in-period — NEVER by org scope: temporal-FK is a system-wide invariant independent of the operator's permissions. Registered edges: §1.6. The registry MECHANISM is module-agnostic and holds no schema; the owning module declares its own edge set and hands it in, so a new module with temporal FKs never edits `AST.Core` (rule-module-boundary §1). An edgeless registry is rejected at construction — it would silently validate nothing. | `AST.Core/EffectivePeriod/` (pure mechanism), `AST.Modules.IAM/Data/` (IAM's edge declarations + DB-backed coverage) |
| 3-condition scope filter + versioned repository base (§6, D13a) | Modules INHERIT the base repository — they cannot write (or bypass) their own filter | Every read applies simultaneously: (1) active, (2) as-of date inside the period (business date D), (3) org-unit scope by TODAY — Self → owner column = current user; OwnOrgUnit → org-unit column = root unit; OwnOrgUnitAndDescendants → subtree CTE (§4); Global → no unit condition. Every versioned write runs: 8-case plan + STRICT temporal-FK + named lock on parent AND child + one READ COMMITTED transaction (§4/§5/§7); success carries gap warnings. | `AST.Core/Data/` (filter contract), `AST.Infrastructure/` (base repository) |
| IAM shared contracts (spec ②/⑥/⑦) | Authorization = level 1 (is the function open?) + level 2 (data scope); function metadata feeds permissions + menu + dashboard | Scope levels 1..4 match `role_permission_version.scope_level` (1=Self, 2=OwnOrgUnit, 3=OwnOrgUnitAndDescendants, 4=Global; root unit is null only for Global). Resolution user→(role, org unit) is by TODAY (D5); not granted → blocking forbidden error (fail-closed). The authorization contract is async end-to-end — a sync signature would force sync-over-async → WPF deadlock [C3]. Menu-group codes are SharedKernel constants; modules reference the constants, never each other. Each module registers its function descriptors (key `Module.Entity.Action`, business code, display name, menu group, nav target, required permission, order) once at startup. | `AST.Core/Iam/` |

*(Connection factory, polling service, transient-retry policy ⑧.6 are data infrastructure alongside `AST.Core` but outside this contract catalog — recorded only; details live in code.)*

---

## 4. Recursive CTE — org-unit subtree (OwnOrgUnitAndDescendants level, §6)
Requirement of §6: run over the **identity tree**, resolve `parent_id` by **today**, **pass through parent nodes with `isactive=0`** (closing a parent org unit does NOT hide the children's data).
```sql
-- Parameters: @rootOrgUnitId (root identity), @today (today's business date)
WITH RECURSIVE
today_ou AS (
  -- The "as-of today" version representing each org-unit identity:
  -- prefer isactive=1; a CLOSED org unit only has an in-period isactive=0 row => still chosen
  -- to allow traversal to PASS THROUGH an isactive=0 parent node (§6).
  SELECT ouv.org_unit_id, ouv.parent_id,
         ROW_NUMBER() OVER (PARTITION BY ouv.org_unit_id
                            ORDER BY ouv.isactive DESC, ouv.id DESC) AS rn
  FROM org_unit_version ouv
  WHERE ouv.effective_from <= @today AND @today <= ouv.effective_to
),
subtree AS (
  SELECT @rootOrgUnitId AS id                       -- anchor: the root org unit itself
  UNION ALL
  SELECT t.org_unit_id                              -- child: parent_id (today) points into subtree
  FROM today_ou t
  JOIN subtree s ON t.parent_id = s.id
  WHERE t.rn = 1
)
SELECT id FROM subtree;
```
- `today_ou` selects **1 representative version/identity** in-period today (prefer active, fall back to inactive) → a closed parent org unit still carries the `parent_id` linkage down to relay to children, so children are not lost.
- The result = a set of `org_unit_id` (identities) for the OwnOrgUnitAndDescendants level, plugged into `{orgUnitColumn} IN (...)` (§3). A leaf org unit → its subtree contains only itself → naturally collapses to "exactly that org unit" (spec ② level 3).
- **Needs verification when coded (§11):** MySQL 9.7 recursive CTE + `cte_max_recursion_depth`; window-function behavior inside a recursive CTE; confirm D6 (no active overlap) makes `rn=1` uniquely stable. → write an integration test on a multi-level tree with a closed node in the middle.
- **[R2 2026-07-03] Clarify the semantics of "closing an org unit" before coding:** because STRICT reverse-FK (Q1) **BLOCKS** closing/shrinking a parent's period while children still depend on its coverage, the process of closing a parent org unit **must re-parent children to the grandparent first** → once that happens, children point straight to a still-live parent, and the CTE reaching them no longer needs to pass through a closed node. Need to verify when coding whether the fallback branch `isactive=0 in-period` (in `today_ou`) actually ever triggers: if "closing an org unit" means cutting `effective_to` back into the past, then at `@today` there is NO in-period row left → the fallback branch is dead. Definitive definition needed: closing an org unit = **keeping 1 in-period `isactive=0` row** (not cutting into the past) OR forcing children to re-parent first (in which case the fallback is a dead path and should be dropped). the data layer to settle this + test this scenario.

---

## 5. Decision-to-source mapping (traceability, no unstated assumptions)
| Design item | Source of truth |
|---|---|
| Header+version, standard columns, resolution index, CHECK | `design-effective-period.md` §1, hard invariant #1 |
| FK points to the **identity** (not a version id) | D3, §2, hard invariant #4 |
| `effective_to='9999-12-31'` open period; closed 2-ended DATE | D4, `rule-effective-period` |
| Reads filter simultaneously on isactive=1 + within period | D2/§3, hard invariant #2 |
| 8-case algebra + remnant + gap=warning | D7, §4, `effective-period-requirements.md` §2; the period-edit planner |
| Resolver with insufficient coverage → STOPS | D9, §3; the as-of resolver |
| STRICT temporal-FK: user⊂(org_unit,role); role_permission⊂(role,function) | D8, §5; task "settled" |
| `org_unit.parent_id` STRICT temporal-FK (self-ref, multi-level) | **[Q1] settled 2026-07-03** (requester) + D8 "multi-level check" |
| `function` epoch `2000-01-01`, removed from code = period closed | C2 (`design-iam-foundation.md` §③), `docs/archive/2026-07-03-addendum-proposals.md` §C2 |
| user: `username` (domain prefix stripped, case-insensitive), `sid` metadata | spec ①; case-insensitive via collation `utf8mb4_0900_ai_ci` (D11) |
| `sid` placed on the header | **[Q2] settled 2026-07-03** — reconciling "capture on first login" (①) with hard invariant #1 |
| Each user has 1 org unit + 1 role per point in time | spec ① |
| role_permission = role x function x scope (1/4); scope = a temporal value | spec ②/③, task "settled" |
| Data scope with 4 levels + authorization contract with 2 levels | spec ②; the authorization contract and scope-level result (`AST.Core/Iam/`) |
| Base repository with 3 conditions, modules do not write their own | D13a, §6, spec ④ |
| Named lock + READ COMMITTED transaction, locking both parent and child | §7, hard invariant #4 |
| Technical clock / business-date provider, injected, captured once | D13b, §3, hard invariant #6 |
| Subtree recursive CTE over the identity tree, passing through closed nodes | §6 |
| Function registry / function descriptor / menu-group codes | spec ⑥/⑦ |
| Tables `schema_version`/`app_control`/`audit_log` | §⑧.1/⑧.2/⑧.4 |
| Dapper+MySqlConnector, `utf8mb4_0900_ai_ci`, MySQL 9.7 LTS | D11, task; §⑧ note C3 |
| ErrorOr at the service boundary; warning ≠ Error | knowledge `handling-errors-with-erroror`; D7 vs D8/D9 |
| Integrity-check sweep (overlap/gap/orphan) | §12 (C1) — integration test + admin screen |

---

## Handoff notes for the later phase (NOT done this round — separately approved)
- **Data layer** (Scope: `migrations/`, `AST.Modules.IAM` data layer, `AST.Core` data-impl parts): numbered SQL scripts creating the 5 table pairs + 3 auxiliary tables per sections 1–2; version Entity/DTO (implementing the shared version-row marker); base repository impl §3; subtree CTE §4 (settle the semantics of "closing an org unit" — R2); registering temporal-FK edges §1.6 (including the `org_unit.parent_id` edge); the §12 integrity-check sweep query — **[R3] ADD a natural-key duplicate check** beyond the 3 original checks (overlap/gap/orphan): two active identities on the same day sharing the same `username` (`user`), the same `org_code` (`org_unit`), the same `role_code` (`role`), the same `function_key` (`function`), the same `(role_id,function_id)` (`role_permission`) — because the app-level invariants in §1.3/§1.5 promised to fold these into this sweep. (2026-08-08: the sweep also gained a 5th check, ≤1 admin-flag-true `role` per day, per N-14 #2 — `FindDuplicateAdminFlagRolesAsync`.) **[R5] Note:** `isactive` (the effective-period flag, version tables) ≠ `is_active` (the command flag, `app_control`) — 2 different meanings, do not confuse them when writing queries.
- **Service layer** (Scope: `AST.Modules.IAM` service layer, consuming `AST.Core` contracts): implementing the authorization contract (resolving by today), syncing `function` from the function registry (epoch C2), the period-edit planner / temporal-FK validator logic (8 cases + coverage); unit tests covering all 8 cases (injecting a fake business-date provider) + integration tests for the named lock/CTE (⑧.5).
- Both only touch within their declared Scope; **do NOT** modify the `AST` Shell (built on a fixed directory-based module catalog); communication outward is only via SharedKernel (`rule-module-boundary`).
