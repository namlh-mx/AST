# Effective-Period Design (Effective-Period Core) — CANONICAL Document

> **TECHNICAL source of truth** for every entity/parameter with an effective period in project AST.
> **BUSINESS** source of truth: `docs/effective-period-requirements.md` (do not edit here — always consult the original).
> High-level principle: skill `rule-soft-delete`. Module boundary: skill `rule-module-boundary`.
> **Anti-drift:** every other doc/skill/memory must only POINT to this file, never copy its content.
> Status: APPROVED 2026-07-02 (brainstorming session, not yet coded). Applies project-wide.

---

# PART I — SHORT RULES (mandatory reading, every agent)

## Decision log — CLOSED (D1–D13 — do not reopen)
- **D1 — Model:** uni-temporal (one axis = effective period) + **already-processed transactions are immutable**. NOT bitemporal.
- **D2 — Edit:** = create a **new version** + invalidate the old one (append-only, keeps audit trail). Already-processed transactions **keep their old value**; a correction = a **new labeled transaction/entry**, the old one stays intact.
- **D3 — Identity:** separate the **"identity card"** (durable id, never changes) from the **"version"** (`[F,T]`, `isactive`). **Live** links point to the **identity** (DB enforces a real foreign key); **frozen** links point to a specific **version id**. Versions are NEVER physically deleted → links never break.
- **D4 — Time unit:** **DAY** (`DATE`, format `yyyy-mm-dd`), **both ends of `[F,T]` inclusive**; open period = `effective_to = 9999-12-31` (UI shows **"Not yet determined"**).
- **D5 — Resolution:** **PERMISSIONS & SCOPE resolve by TODAY**; **business-parameter VALUES resolve by the TRANSACTION DATE D** (the business flow passes `D` in).
- **D6 — Core invariant:** for a given identity, on any given day ↔ **at most 1 active version** (`isactive=1` versions **never overlap in period**). MySQL has no `WITHOUT OVERLAPS` → enforce at the app layer + **named lock**.
- **D7 — Editing a period:** follows the **8-case algebra** (Part II §4), producing remnants, soft-deleting the old version, inserting the new one. **A date gap = a WARNING** (does NOT block *by default* — an entity may set `GapIsBlocking`, and org unit does). **WHICH gaps warn is Part II §4a**, and it is not "any hole anywhere": only the two boundaries this edit touches, measured against the coverage the plan leaves behind.
- **D8 — Temporal-FK, STRICT level:** the parent must cover the child's **entire period continuously**; a missing/gapped coverage, or narrowing/closing the parent such that the child loses coverage = **BLOCKED**; **the parent must be declared before the child**; checked edge-by-edge across multiple levels. (IAM: `user` ⊂ (`org unit`, `role`); `role_permission` ⊂ (`role`, `function`).)
- **D9 — Missing coverage at business-run time:** **STOP + report clearly** ("Parameter 'X' has no effective value on date dd/mm/yyyy"). ABSOLUTELY no falling back to a default/other period.
- **D10 — Declaration rights:** declaration screens (org unit/role/user/permission/parameter) are **admin-only**; regular users only perform operational tasks.
- **D11 — Data technology:** **Dapper + MySqlConnector** (no EF Core); charset `utf8mb4_0900_ai_ci`; migrations = **numbered SQL scripts**. Data access is hidden behind a shared-kernel interface (`AST.Core/Data/`) ⇒ the ORM choice is not hard-locked.
- **D12 — Anti-drift:** one canonical document (this file) + decision log + self-loading skill; everywhere else only POINTS to it.
- **D13 — DI (Prism.DryIoc):** (a) force all data access through the base repository (the filter cannot be bypassed); (b) inject a clock / business-date-provider abstraction (`AST.Core/Time/`) for a consistent, testable notion of "today".

## Hard invariants (violating one = a bug; review blocks it)
1. Never hard-delete a version; "editing" = a new version + `isactive=0` on the old one; never `UPDATE` over a business column (the ONLY exception: `UPDATE`-ing **effective_from/effective_to** when cutting/closing a period per the algebra in §4).
2. Reading "data usable on date D" must filter **SIMULTANEOUSLY**: `isactive=1` **AND** `effective_from <= D AND D <= effective_to`. Missing either condition = a bug. (`isactive=0` and "outside the period" are **two different concepts**.)
3. No two `isactive=1` versions of the same identity may overlap in period.
4. Links between entities point to the **identity** (not the version id); only where historical freezing is needed does a link point to a version id.
5. Strict temporal-FK: never save a child if the parent does not continuously cover the child's whole period; never narrow the parent if it would make a child lose coverage.
6. The date (`D`, "today") comes from the injected business-date provider (`AST.Core/Time/`) — NEVER from scattered direct system-clock reads.

---

# PART II — DETAILS (for implementers)

## 1. Data model for an "entity with an effective period" (header + version)
Every entity with an effective period = **2 tables**:
- **Identity table** `<name>`: contains only the **durable identifier** (`id` PK, never changes for the entity's lifetime; contains NO column that changes over time).
- **Version table** `<name>_version`: `id` (version PK), `<name>_id` (FK → identity), the **business columns**, the effective period + audit fields.

**Standard columns of the version table:**
| Column | Type | Note |
|---|---|---|
| `id` | BIGINT UNSIGNED PK AI | version id (used for **freezing**) |
| `<name>_id` | BIGINT UNSIGNED FK | points to the identity (the **live** link points here) |
| `effective_from` | DATE NOT NULL | F, inclusive |
| `effective_to` | DATE NOT NULL | T, inclusive; open period = `9999-12-31` |
| `isactive` | TINYINT(1) NOT NULL DEFAULT 1 | 1 = currently recognized, 0 = superseded/cancelled (audit) |
| `recorded_at` | DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP | the moment it was RECORDED (append-only, never backdated) |
| `recorded_by` | VARCHAR(100) NOT NULL | acting username |
| `reason` | VARCHAR(255) NULL | label/reason (distinguishes an ordinary edit from a correction) |

Required constraints/indexes: `CHECK(effective_from <= effective_to)`; `KEY (<name>_id, isactive, effective_from, effective_to)` (serves both overlap checking and date-based resolution).

**Sample DDL (illustrative pattern — generic `code`/`name`; the real per-entity column sets live in `docs/design-iam-schema.md`):**
```sql
CREATE TABLE org_unit (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE org_unit_version (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  org_unit_id    BIGINT UNSIGNED NOT NULL,
  code           VARCHAR(50)  NOT NULL,         -- the entity's natural code (illustrative)
  name           VARCHAR(255) NOT NULL,         -- the entity's display name (illustrative)
  parent_id      BIGINT UNSIGNED NULL,          -- FK → org_unit(id) (parent identity), resolved by date
  effective_from DATE NOT NULL,
  effective_to   DATE NOT NULL DEFAULT '9999-12-31',
  isactive       TINYINT(1) NOT NULL DEFAULT 1,
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  recorded_by    VARCHAR(100) NOT NULL,
  reason         VARCHAR(255) NULL,
  PRIMARY KEY (id),
  KEY idx_org_unit_version_res  (org_unit_id, isactive, effective_from, effective_to),
  KEY idx_org_unit_version_code (code, isactive, effective_from, effective_to),
  CONSTRAINT fk_ouv_ou     FOREIGN KEY (org_unit_id) REFERENCES org_unit(id),
  CONSTRAINT fk_ouv_parent FOREIGN KEY (parent_id)   REFERENCES org_unit(id),
  CONSTRAINT chk_ouv_period CHECK (effective_from <= effective_to)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
> This block shows only the effective-period *pattern* (identity + version + `[from,to]` + `isactive`) — the columns are generic placeholders. The 5 concrete IAM tables (`org_unit`, `role`, `user`, `function`, `role_permission`) — including org_unit's real `org_code`/`org_name_full_vn`/`org_name_short_vn` + supplemental set — are detailed in **`docs/design-iam-schema.md`** §1.1 (full DDL + engine-contract signatures + temporal-FK edges).

## 2. Identity & references (D3 — solving "references never break")
- **Live link** (currently being declared/looked up): the FK column points to **`<parent>_id` (identity)**; when the value is needed, **resolve by date** → version. The DB enforces a real FK to the identity table ⇒ the target always exists.
- **Freezing** (once a business transaction has been processed): the transaction record stores the **`<...>_version_id`** (the specific version id used). Since versions are never physically deleted → the link stays valid forever.
- Changing the natural code (the entity's business code, e.g. org_unit's `org_code`) = creating a new version of the same identity; it **does not** affect links (links follow the identity, not the code).

## 3. Resolution by date (D5, D9)
- **Parameter value on date D:**
```sql
SELECT * FROM <name>_version
WHERE <name>_id = @id AND isactive = 1
  AND effective_from <= @D AND @D <= effective_to;   -- open period: effective_to = 9999-12-31
```
No row → **STOP the business flow + report clearly** (D9), absolutely no falling back to a default/other period.
- **Permissions & scope:** resolve user → (role, org unit) by **today** (the business-date-provider abstraction's "today", `AST.Core/Time/`), NOT by D.
- A single business operation captures `D` (and "today") **once**, used consistently for every parameter within that operation.

## 4. Period-interval algebra for Add/Edit (D7) — 8 cases
New period `[F,T]` compared to each existing active period `[a,b]` (business source: `docs/effective-period-requirements.md §2`):

| # | Situation | Condition | Handling (append-only) |
|---|---|---|---|
| 1 | Disjoint | `T<a` or `F>b` | Insert new. A date gap → **gap warning**. |
| 2 | Adjacent | `F=b+1` / `T=a−1` | Insert new (seamless). |
| 3 | Overlaps head | `F≤a≤T<b` | Soft-delete old; tail remnant `[T+1,b]`; insert new. |
| 4 | Overlaps tail | `a<F≤b≤T` | Soft-delete old; head remnant `[a,F−1]`; insert new. |
| 5 | Sub-period (new ⊂ old) | `a<F` and `T<b` | Soft-delete old; head + tail remnants; insert new (splits into 2). |
| 6 | New engulfs old | `F≤a` and `b≤T` | Soft-delete old (no remnant); insert new. |
| 7 | Exact match | `F=a` and `T=b` | = a correction: soft-delete + insert same period. |
| 8 | Overlaps multiple periods | new spans ≥2 periods | Repeat #3–7 for each period + insert 1 new period. |

- Every case: the original replaced record keeps `isactive=0` (audit); a remnant is a **new version** (keeps the old version's business data, only the period changes).
- **Closing an open period:** an open period `[x, 9999-12-31]` + a following period `[F,…]` with `F>x` → falls into an overlap case → the app auto-sets the old version's `effective_to` = `F−1` (via a remnant/cut), keeping the original version at `isactive=0`.
- **The `9999-12-31` boundary** (addition): treat it as "infinity"; the `b+1`/`T+1` operations special-case this boundary (no overflow arithmetic). There is no `b+1` when `b=9999-12-31`.
- All of this logic is gathered into **one shared engine** — the period-editing engine (`AST.Core/EffectivePeriod/`) — so no entity re-implements it on its own.

### 4a. WHICH gaps warn — the two boundaries this edit touches, on the coverage the plan LEAVES BEHIND

D7 says a date gap is a warning. It does not say which gaps, and the codebase holds **two different
gap questions** that must not be merged:

| Question | Who asks it | Scope | Blocks? |
|---|---|---|---|
| *"Does this edit leave a hole at either boundary it touches?"* | `PeriodEditor.PlanUpsert` (Add/Edit, this §4) | the **two** boundaries immediately around the new period | per-entity: `GapIsBlocking` |
| *"Does any hole remain anywhere in this identity's coverage?"* | `VersionedRepository.ComputeGapWarnings` (Close / Cancel / Delete) | the **whole** remaining coverage | never — warns only |

Two rules follow, and both are load-bearing:

1. **The neighbour on each side is drawn from the coverage the plan LEAVES BEHIND** — the untouched
   versions **plus every period the plan inserts** (head/tail remnants and the new period itself),
   never from the untouched versions alone. A cut whose remnant abuts its neighbour opens no hole, so
   it must produce no warning. Reading `untouched` alone reported a gap the plan's own remnant was
   about to fill — and for an entity with `GapIsBlocking` that was a refusal to write a legal edit.
2. **Only the nearest period on each side is examined; the scan never walks the timeline.** A hole is
   reported **only when it lies between `newPeriod` and that nearest neighbour**. Anything further out
   is out of scope, deliberately: widening this to the whole coverage would let one old hole — a Cancel
   or a Delete can leave one *without* blocking — refuse every later edit of that identity forever.

   ⚠️ **Two restatements of this rule are WRONG, in opposite directions, and both were written and
   corrected on 2026-08-26.** It is neither *"a hole the edit does not touch is not reported"* (too
   wide) nor *"suppression needs a remnant this plan generates"* (too narrow). What stands between the
   hole and `newPeriod` may be a generated remnant **or** an ordinary untouched version — the rule asks
   only which period is *nearest*, never *what kind* it is. Three worked cases, all traced against
   `PeriodEditor.PlanUpsert`:

   | Fixture | Edit | Outcome |
   |---|---|---|
   | `C[2020-01-01, 2025-12-31]`, `B[2026-02-01, …]`, hole `[2026-01-01, 2026-01-31]` | `C` → `[2020-01-01, 2023-12-31]` | **Not reported** — the tail remnant `[2024-01-01, 2025-12-31]` is nearest, the hole is beyond it |
   | `A[2019]`, hole `[2020]`, `B[2021]`, `C[2022-01-01, 2025-12-31]` | `C` → `[2022-01-01, 2024-12-31]` | **Not reported** — nearest before is untouched `B`, which abuts `newPeriod`. **No remnant is involved at all** |
   | `A[2019]`, hole `[2020]`, `C[2021-01-01, 2025-12-31]` | `C` → `[2021-01-01, 2024-12-31]` | **Reported, and for org unit a refusal** — `C.From == newPeriod.From` ⇒ no head remnant, nothing else in front, so `A` is nearest and the hole lies between them |

⚠️ **`GapIsBlocking` is per entity and D7's "does not block" is the default, not a universal.** Org
unit sets it **true** (`AST.Modules.IAM/.../OrgUnitRepository.cs`), so for org unit a gap warning IS a
refusal; role, user, function and role-permission leave it false and only warn.

⚠️ Do **not** reuse `ComputeGapWarnings` for the Add/Edit question. It is correct for its own
question — whole-coverage, after a coverage REDUCTION, never blocking — and merging the two is a
regression, not a tidy-up. Rationale + the measurement: spec
`2026-08-22-orgunit-edit-close-code-reuse-shaping.md` §18.1, §19.5.

## 5. Temporal referential integrity (D8 — STRICT level)
- **Coverage check:** saving a child `[F,T]` → every parent (per the relevant relationship) must **continuously cover the entire `[F,T]`** with valid active versions (may be covered by **multiple parent periods** as long as there is no gap). Missing/gapped → **BLOCKED**: "Parent parameter '…' has no declared effective period for the range [d1–d2]."
- **Reverse-FK:** narrowing/closing/deleting a parent's period such that ≥1 child loses coverage → **BLOCKED** ("N child parameters depend on the range […]"); the children must be handled first.
- **Multi-level:** checked edge-by-edge between parent and child, transitively across levels. **A parent must be declared before its children.**
- Gathered into a temporal-FK validator (`AST.Core/EffectivePeriod/`); relationships are declared via metadata (child table → parent table(s) + column), not hardcoded ad hoc.

## 6. Reading data by scope (base repository — 3 conditions)
A standard scope-filter builder (`AST.Core/Data/`) produces a SQL fragment that enforces, **simultaneously**: `isactive=1` + `effective_from<=D AND D<=effective_to` (D supplied by the business flow) + **org-unit scope** per the data-scope value (`AST.Core/Iam/`), resolved by **today**. The "Org unit + descendant org units" level uses a **recursive CTE** over the identity tree (resolving `parent_id` by today), traversing through a parent node even if `isactive=0` (closing a parent org unit does not hide its children's data). Modules must NOT write this filter themselves.

## 7. Concurrent-write protection (addition #4)
Wrap the sequence (overlap check → cut/split per §4 → insert) in **1 transaction** + a MySQL **named lock** (`GET_LOCK('astep:<table>:<identity>', timeout)`), since `SELECT...FOR UPDATE` cannot lock a brand-new row that doesn't exist yet. When checking temporal-FK, **lock both the child identity and the related parent identity(ies)**, in a **fixed order** (e.g. sorted by table name then id) to avoid deadlock. Isolation level `READ COMMITTED`.

**A header and its first version commit or roll back together** (amendment 2026-08-16, decision-log). An identity must NOT be minted on a separate connection ahead of the transaction that writes its first version — that ordering is what leaves a zero-version header behind when the write fails, and best-effort compensation cannot close it. To make minting-inside-the-transaction legal, one narrow carve-out to the paragraph above: **an identity created inside the transaction takes no named lock of its own**, because no other session can name it — nothing committed references it — until commit, so the lock would protect nothing. The carve-out is bounded by two conditions that stay absolute:
- every **pre-existing** identity the write touches — child *or* parent, including one reached by re-attaching to an existing header — is still locked **up front, in the fixed order above**; and
- **no `GET_LOCK` is ever taken after the transaction has opened** (that, not the missing key, is what would reintroduce deadlock).

A rolled-back insert still consumes its `AUTO_INCREMENT` value, so **gaps in identity ids are expected and are not a defect**. As of 2026-08-17 **every production mint path follows this rule** — `role`, `role_permission`, `function` and `org_unit` (backlog 0.4b closed; `user` has no production mint path yet). The remaining callers of the own-connection mint are integration-test fixtures. What keeps it that way is mechanical, not this paragraph: `AST.Meta.Tests/RoleWritePathAbsenceTests` and `OrgUnitWritePathAbsenceTests` fail if a migrated write path reaches back for the pre-transaction mint or a compensating delete.

## 8. The effective-period engine — contract in the shared kernel (registered via DI)
Detailed signatures are closed at the detailing step; the required catalog:
- The period-editing engine (`AST.Core/EffectivePeriod/`) — the 8-case algebra + remnants + gap warnings (§4).
- The period resolver (`AST.Core/EffectivePeriod/`) — (identity, D) → version; missing coverage → "none" (§3).
- The temporal-FK validator (`AST.Core/EffectivePeriod/`) — coverage check, reverse-FK, multi-level (§5).
- The standard scope-filter builder (`AST.Core/Data/`) — 3 conditions + scope (§6).
- The clock / business-date-provider abstraction (`AST.Core/Time/`) — the single consistent date source, captured once per operation. "Captured once" is carried by the `OperationDate` type (same folder) and enforced by `AST.Meta.Tests/WritePathBusinessDateTests`; the provider itself is a live clock, so a second read on the same operation is a real defect, not a style question.
- A standard base "versioned repository" for modules to inherit (the filter cannot be bypassed).
- (Reused by IAM) an authorization service + a data-scope value (`AST.Core/Iam/`) (4 levels, resolved by today), a function registry + a function descriptor (`AST.Core/Iam/`), menu-group code constants (`AST.Core/Iam/`).

## 9. Seven additions from standard-model research (already merged)
1. **Minimal audit** (`recorded_at/by`, `reason`) on every version; the recording direction is append-only, never backdated.
2. **The `9999-12-31` boundary** treated as "infinity" (§4).
3. **A unified business-date source** via the clock / business-date-provider abstraction (§3).
4. **Lock both parent and child** when checking temporal-FK (§7).
5. **Deletion semantics:** distinguish deleting **1 period** vs **retiring the whole entity** (closing every active period / `isactive=0`, never a physical delete); **the original version must always exist**; reverse-FK blocks if dependent children remain.
6. **Two "not in use" concepts:** `isactive=0` ≠ "outside the period"; reading must satisfy **both simultaneously** (hard invariant #2).
7. **An "impact report" hook:** the shared kernel leaves room to later answer "who has referenced/frozen this version" for a business module (so editing the past can list what's affected).

## 10. Reference basis (standard models consulted)
The model = Fowler's *Temporal Object/Property/Snapshot/Audit Log*; a temporal-FK **points to the object, not the version**, gapless coverage = *temporal referential integrity*; no-overlap = `WITHOUT OVERLAPS`, cut/split = `PORTION OF` (SQL:2011). Fowler recommends **avoiding bitemporal**, favoring *append + track history* ⇒ this confirms the D1/D2 choice.
- https://martinfowler.com/eaaDev/timeNarrative.html
- https://www.sciencedirect.com/topics/computer-science/temporal-foreign-key

## 11. Points to verify when coding
Recursive CTE on MySQL 9.7 + the `cte_max_recursion_depth` setting; the named-lock call bound to the correct session through the MySqlConnector pool + a fixed parent-child lock order; the `9999-12-31` boundary; the .NET 10 API for reading the logged-in Windows identity; idempotent Prism module registration + its module initialization-mode setting; tests injecting the business-date-provider abstraction to build all 8 algebra cases + verify temporal-FK coverage.

## 12. Safety net — data-integrity checks (C1, approved 2026-07-03)
D6 (no period overlap) and D8 (strict temporal-FK) can only be enforced at the **app layer** (MySQL cannot enforce these constraints) → if the app has a bug, data can become corrupted **silently** until a business error exposes it. An integrity-check query set detects this early:
- Two `isactive=1` versions of the same identity **overlapping in period** (violates D6 / §4).
- A parent with **gapped coverage** over a child's period (violates D8 / §5).
- An **orphaned** child record (parent identity does not exist).

Packaged as an admin **"Data Integrity Check"** screen (run ad hoc/on schedule) **and** run inside integration tests. **Does NOT replace** app-layer enforcement — it is only an early-detection safety net. (Source: `docs/archive/2026-07-03-addendum-proposals.md §C1`.)
