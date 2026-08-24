# Operation history — one gesture, one row

Canonical for: the `operation` table, the two provenance columns on every version row, the
invariants that keep them truthful, the per-object history read model, and the retreat of
`audit_log` to security-only scope.

**Not canonical for** (do not restate here): period semantics and the 8-case algebra
(`design-effective-period.md`), which entities are versioned at all (`design-temporality-classes.md`),
the IAM table shapes (`design-iam-schema.md`), or the business-transaction / adjustment schema —
which does not exist yet and is deliberately not designed here.

Decision and its reasoning: `decision-log.md`, the rows dated 2026-08-18. This document is the
design, not the argument.

## 0. The problem in one paragraph

State changes today that record **no actor**: a Cancel (`VersionedRepository.CancelVersionCoreAsync`),
a cascaded dependent cancel (`AutoCutExclusivelyOwnedAsync`) and `DeleteVersionAsync` all change a row
by flipping `isactive` / `cancelled`, and the flipped row keeps the *creator's* `recorded_by`. Nothing
in any version table records **which rows moved together**: `RoleDeclarationService` mints an operation
id per gesture and writes it only into `audit_log`'s `detail` JSON. `audit_log` has zero readers, so
today's history screen cannot answer "who cancelled this, and what else did that same Save touch?".

## 1. Schema

### 1.1 `operation` — one row per user gesture, application-wide

| Column | Type | Meaning |
|---|---|---|
| `id` | BIGINT UNSIGNED AUTO_INCREMENT | surrogate. **Not gapless, and never displayed as a business document number** |
| `occurred_at` | DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP | when the database recorded it |
| `operation_date` | DATE NOT NULL | the captured business date of the gesture (`AST.Core/Time/OperationDate.cs`) |
| `username` | VARCHAR(100) NOT NULL | the actor — a person, or a system principal (§4.3) |
| `kind` | VARCHAR(20) NOT NULL | **gesture-level** vocabulary (§1.3) |
| `note` | VARCHAR(255) NULL | the note the operator gave **for the gesture** (§1.4) |

One index for the only query that reads it outside a join: `KEY idx_op_actor_time (username, occurred_at)`.

`operation` is application-wide — one table, not one per entity. It is infrastructure: it answers
"which gesture?" and nothing else.

### 1.2 Two provenance columns on every version table

Added to all five: `org_unit_version`, `role_version`, `function_version`, `user_version`,
`role_permission_version`.

| Column | Type | Meaning |
|---|---|---|
| `created_by_operation_id` | BIGINT UNSIGNED NOT NULL (after the §6 contract phase) | the gesture that INSERTED this row |
| `superseded_by_operation_id` | BIGINT UNSIGNED NULL | the gesture that took this row out of force. NULL while `isactive = 1` |

Both are FKs to `operation(id)`. The identity (header) tables get **no** column: an identity is minted
inside the same transaction as its first version row, and that row carries the provenance.

**No tombstone rows are introduced.** A Close/Cancel/Delete keeps its current physical shape; it
additionally stamps `superseded_by_operation_id`. This is where this design deliberately diverges from
the advisory that proposed appending terminal revisions.

### 1.3 Two vocabularies, never unified

- `operation.kind` — what the **user did**: `Save`, `Close`, `Cancel`, `Delete`, `Sync`.
- `{table}.operation_kind` (existing, `VersionOperationKind`) — what happened to **that row**:
  `Add`, `Edit`, `Close`, `Cancel`.

One Save can write an `Edit` role row, a `Cancel` grant row and two `Add` grant rows. A scalar header
field cannot carry that, and must not try to. Collapsing the two vocabularies re-creates exactly the
lossy header this design exists to avoid.

`operation.kind` is load-bearing and cannot be derived from the rows: a Cancel with no adjacent
predecessor inserts **no new row at all**, so the only rows linked to that gesture carry
`operation_kind = 'Add'` — the kind written when someone else created them. Without the header kind, a
cancellation would read as an addition.

### 1.4 Why the header keeps a `note`

Same reason. On a no-new-row Cancel the operator's note has no row to live on; today it survives only
in `audit_log`, which §5 removes. So:

- `operation.note` = the note supplied for the **gesture** (the one field the screen shows).
- `{table}.reason` = the note attached to **that row** (e.g. a per-grant `add.Note`).

The header never carries a per-row value, and a row's `reason` is never derived from the header. When
a gesture has one note and one row the two agree; that is granularity, not duplication of authority.

## 2. Invariants

Each is stated so a test can falsify it. `OP*` is this document's ID prefix.

**OP1 — At most one `operation` row per write transaction; exactly one if and only if that
transaction writes at least one version row.** It is minted *inside* the transaction, immediately
before the first version-row write (§4.2), never at transaction entry, never ahead of the transaction,
never reused across transactions.
*"If and only if" is load-bearing, not pedantry:* a successful transaction can write **no** version
row — `FunctionRepository.CreateAsync`'s `KeyAlreadyPresent` branch returns `Result.Success` from
inside the composite after the re-read under the lock finds the key, so the transaction commits having
written nothing. Minting at transaction entry would commit an orphan header on exactly that path.
*Falsified by:* a rollback test asserting zero `operation` rows after a failed write; a test asserting
one row (not two) after a Role Save that writes six rows; a concurrent-create test asserting the
**losing** caller leaves zero `operation` rows.

**OP2 — `operation` is insert-only.** No UPDATE, no DELETE, ever. Provenance that can be rewritten is
not provenance.
*Falsified by:* an `AST.Meta.Tests` guard asserting no `UPDATE`/`DELETE` statement in production source
names `operation`.

**OP3 — `operation` never holds a business value.** No amount, no code, no period, no business
document number, no traceability link. It is not a business document and does not replace one.
*Falsified by:* review, and by this rule's restatement in `shared-components.md` when the table is
registered there.

**OP4 — Every version row insert stamps `created_by_operation_id`; every state flip stamps
`superseded_by_operation_id` exactly once.** A row is superseded at most once: no production path sets
`isactive` back to `1` (verified — the engine inserts a remnant instead of reactivating).

**"Exactly once" is enforced, not merely true today.** Every state-flip `UPDATE` currently predicates
on `id` alone, so a second statement would silently overwrite the first gesture's provenance. Each of
the seven flip sites (§4.1) therefore gains `AND superseded_by_operation_id IS NULL` to its `WHERE`,
and validates the affected-row count: zero rows affected means the row was already superseded, which
is a **clear failure**, never a silent no-op.
*Falsified by:* a per-site test over all ten engine write sites (§4.1); a double-supersession test in
which the second attempt affects zero rows and leaves the first operation id intact; an integrity query
asserting no row has `isactive = 0` with `superseded_by_operation_id IS NULL`.

**OP5 — The history read is a UNION of two roles, not a GROUP BY on one column** (§3).
*Falsified by:* a real-MySQL test in which a Cancel with no predecessor produces exactly one history
line, a Delete exactly one, and a Close exactly one — none missing, none doubled.

**OP6 — A system/batch actor mints one `operation` per atomic write, not per run** (§4.3).
*Falsified by:* a catalog-sync test where item 2 of 3 fails and the run leaves exactly one `operation`
row — the one belonging to the committed item — and no orphan header.

**OP7 — `audit_log` carries no business writer.** Its scope is login, break-glass and signature-fail.
*Falsified by:* an `AST.Meta.Tests` absence guard over `IAuditLogWriter` call sites in the business
modules.

## 3. The read model

### 3.1 The unit of history is the AGGREGATE, not the identity

"One history line = one user gesture" only holds if the line can show everything that gesture touched
**on this object**. A Role Save writes `role_version` *and* the `role_permission_version` rows it
revoked and added — different identities, in a different table. A query over `role_version` alone
returns a line that cannot answer "what else did this Save touch?", which is the whole requirement.

So each screen's history reads over its **aggregate**: the object's own version table plus the tables
that object owns.

| Object | Aggregate = own table + owned tables | History read |
|---|---|---|
| Role | `role_version` + `role_permission_version` (by `role_id`) | exists (`GetHistoryAsync` ×2) |
| Org unit | `org_unit_version` | exists (`GetHistoryInScopeAsync`) |
| Function | `function_version` | **none — out of scope, see below** |
| User | `user_version` | **none — out of scope, see below** |

**Function and User have no history read, and this design does not give them one.** Two facts, both
verified: neither `function_version` nor `user_version` carries an `operation_kind` column (only
`org_unit_version`, `role_version` and `role_permission_version` do, and only those three repositories
set `RecordsOperationKind`), and no repository exposes a history read for either. So §3.2 is defined
for the two objects that have a history screen today. Provenance columns (§1.2) still land on **all
five** tables — attribution is uniform; only the *read* is scoped.

Two preconditions, to be met by whoever builds those screens, not here: the table must record
`operation_kind` (§1.3's row vocabulary has nowhere else to live), and the object needs a scope ruling
(§3.3). Building either screen without both produces a grid that cannot say what happened, or one that
shows rows its viewer may not see.

The owned half is not a second history — it is the same operation seen through the rows it also
touched, and it carries the grant's own `operation_kind` and `reason`.

**Ownership means same-gesture write/cascade authority — NOT every foreign key and not every temporal
parent.** `role_permission_version` is in the Role aggregate because a Role Save writes it and a role
Close cascades into it. It is **not** in the Function aggregate, even though it carries `function_id`:
no function gesture writes a grant. By the same rule `user → role`, `user → org_unit`,
`grant → function` and `org_unit → parent` are temporal dependencies, not ownership, and none of them
adds a table to an aggregate. Read the FK direction and you get the wrong answer; read *which gesture
writes the row* and you get this table.

### 3.2 The query

For one aggregate, a history line is one `operation`. The rows under it are the union of the two roles
a row can play — per table in the aggregate:

```sql
SELECT o.id AS operation_id, o.occurred_at, o.operation_date, o.username, o.kind, o.note,
       'role_version' AS source_table,          -- REQUIRED: see the uniqueness note below
       v.id AS version_id, v.operation_kind, v.reason,
       v.effective_from, v.effective_to, v.isactive, v.cancelled,
       'created' AS row_role
  FROM operation o JOIN role_version v ON v.created_by_operation_id = o.id
 WHERE v.role_id = @identityId
UNION ALL
SELECT o.id, /* … */ 'role_version', /* … */ 'superseded'
  FROM operation o JOIN role_version v ON v.superseded_by_operation_id = o.id
 WHERE v.role_id = @identityId
/* …and the same two halves again over role_permission_version, source_table
   'role_permission_version', joined by its own role_id */
 ORDER BY occurred_at DESC, operation_id DESC, source_table, version_id
```

**The `ORDER BY` uses output aliases, never `o.`-qualified names.** After a `UNION ALL` MySQL orders
the set operation's *result*, where table qualifiers are not in scope; `ORDER BY o.occurred_at` fails
at runtime. This is exactly the kind of line that reads fine and dies on first execution, so the
exemplar is gated by a real-MySQL test rather than by review.

**`source_table` is not decoration.** Every version table has its own `AUTO_INCREMENT`, so
`role_version.id = 1` and `role_permission_version.id = 1` can both belong to one Save. Without the
discriminator the projected rows collide: they cannot be typed by the screen, and any de-duplication
would silently drop one of them.

**The ordering is three keys, not two.** `occurred_at` alone does not separate two operations recorded
in the same second, and `version_id` does not keep one operation's rows from different tables
contiguous — a materializer that builds header lines as it streams would emit split or duplicated
headers. Order by `occurred_at DESC, operation_id DESC`, then within the operation by
`source_table, version_id`.

Four branches for Role (own table × 2 roles, owned table × 2 roles), two for an object that owns
nothing. A row from the owned table carries its own `operation_kind` — that is what distinguishes a
revoke from a grant inside one Save.

Three consequences the screen must handle, and which a naive `GROUP BY created_by_operation_id` gets
wrong:

1. A gesture can appear **only** through the `superseded` half (a Cancel with no predecessor; a
   Delete). Grouping on `created_by_operation_id` alone omits it entirely — the canceller stays invisible.
2. One row appears **twice** under different operations (created by A, superseded later by B) —
   correct, and it is one row-line under each.
3. Within one operation a row can play **both** roles. The union yields one line per
   `(operation_id, source_table, version_id, row_role)` — distinct by construction, so **no
   de-duplication step is required** — and none may be added that collapses the two roles of one row,
   because that pair is what makes an Edit legible. The key is a **quadruple**: dropping `source_table`
   from it is the collision described above.

`row_role` alone does not say *what* the gesture did — `superseded` covers Close, Cancel, Delete and
edit-supersession alike. `operation.kind` supplies that (§1.3).

**The read is TWO stages, and the union is only the first.** The union above is the provenance
skeleton: who, when, which rows, in which role. It deliberately carries **no business payload** — and
it cannot, because a `UNION` demands one column shape across branches while `role_version` shows code,
name and admin flag whereas `role_permission_version` shows function and scope. Squashing both into
shared generic columns would lose their types and their meaning.

So: **stage 1** returns the skeleton, ordered. **Stage 2** fetches typed detail per table, keyed by the
`(source_table, version_id)` pairs stage 1 returned — each module projecting the same business columns
its grid shows today. Without stage 2 the screen renders opaque ids and the current Role history grid
regresses, which is a defect the migration order's "switch reads" step would otherwise introduce
silently.
*Falsified by:* a Role Save that renames the role **and** grants a permission, whose single history
line expands to show both the name change and the grant, each with its own business columns.

Normal (non-history) screens never join `operation`.

### 3.3 The scope predicate is preserved as a CAPABILITY — and reads stay Global by decision

The query above filters by **identity only**. `OrgUnitRepository.GetHistoryInScopeAsync` also takes a
`DataScope` and applies an undated history-scope predicate server-side. Rewriting the read without
that predicate would delete the capability.

**Correcting an over-claim made on 2026-08-18** (this document's first version, and the decision-log
row that accepted it): the danger is *not* that a scoped actor would start seeing another subtree's
history. **Org-unit reads are deliberately Global** — a recorded product decision (`decision-log.md`
2026-08-04: the org catalogue is reference data for ~30 in-house users of one organisation, and the
root probe is a system-wide question), and `LoadHistoryCoreAsync` passes
`new DataScope(ScopeLevel.Global, …)` on purpose, with that decision cited at the call site. No live
actor is protected by the predicate on the history path today. That decision even anticipated this
exact failure: *"without this row someone would later clean up that comment and silently change
security behaviour."* This design must not be that someone.

**Where losing the predicate would really bite:** `IsWithinScopeAsync` — the **write-path** gate —
shares the same SQL clause, deliberately factored so the two cannot diverge. A history rewrite that
inlines or drops the clause therefore endangers a live authorization check, which is the substance
worth protecting.

So: **each module's history read keeps its own scope predicate**, and §3.2 describes the shape of the
provenance join inside it, never a replacement for it. A generic cross-module history query is not
part of this design and must not be introduced by it.

"Keeps its own predicate" is vacuous where no predicate exists, so the rule is a **matrix**, not a
sentence:

| Object | Isolation rule |
|---|---|
| Org unit | the repository keeps its **undated** subtree predicate (`GetHistoryInScopeAsync`, shared with `IsWithinScopeAsync`); the **screen keeps passing Global**, per the 2026-08-04 decision. Changing what the screen passes is a product decision and is out of scope here |
| Role + grants | **Global-only.** `role` is a system-wide entity and its screen authorizes at `ScopeLevel.Global`; the repository read takes no `DataScope` and must not grow one. The gate is that a non-Global actor never reaches the screen — pinned by a test, not by the read's silence |
| Function, User | **undecided, and deliberately so** — no history read exists (§3.1). Whoever builds one settles its rule first |

**The User rule is a requester decision, not a technical one, and it is NOT made here.** `user_version`
carries a mutable `org_unit_id`, so a user who moves from unit A to unit B has history rows belonging
to both. Whether an A-scoped viewer then sees nothing, only the A-period rows, or the whole identity
including B-period actors and notes is a confidentiality choice about people, and inventing it in a
mechanism spec would be exactly the wrong place. It becomes a blocking question the moment a user
history screen is requested.

*Falsified by:* two tests, each at the layer its claim lives in. At the **repository** — a non-Global
`DataScope` for an identity outside the subtree returns **empty** while historical descendants stay
visible, proving the capability survives the rewrite. At the **screen** — `LoadHistoryCoreAsync` still
passes `ScopeLevel.Global`, pinned so a future "cleanup" of that call site turns a test red instead of
silently changing policy. A repository test alone proves neither.

## 4. Write path

### 4.1 The ten engine sites, and what each must stamp

All in `AST.Infrastructure/VersionedRepository.cs`.

| Site (method) | What it does today | Must stamp |
|---|---|---|
| `InsertNewAsync` | INSERT a new version | `created_by` |
| `InsertRemnantAsync` | INSERT … SELECT a copy with a new period | `created_by` (of the CURRENT gesture) |
| `InsertRemnantOnTableAsync` | same, for a dependent table | `created_by` — **see the trap below** |
| `CloseVersionCoreAsync` | `isactive = 0` on the closed version | `superseded_by` |
| `DeleteVersionAsync` | `isactive = 0` | `superseded_by` |
| `CancelVersionCoreAsync` (target) | `isactive = 0, cancelled = 1` | `superseded_by` |
| `CancelVersionCoreAsync` (predecessor) | `isactive = 0` before re-inserting it | `superseded_by` |
| `ApplyUpsertPlanAsync` (SoftDeactivate) | `isactive = 0` | `superseded_by` |
| `AutoCutExclusivelyOwnedAsync` (cancel) | `isactive = 0, cancelled = 1` on a dependent | `superseded_by` |
| `AutoCutExclusivelyOwnedAsync` (cut) | `isactive = 0` on a dependent | `superseded_by` |

**Trap — `InsertRemnantOnTableAsync` builds its copy list from `INFORMATION_SCHEMA`**, excluding a
hard-coded set (`id`, `effective_from`, `effective_to`, `isactive`, `recorded_at`, `recorded_by`,
`reason`, `cancelled`) and removing `operation_kind`. Both new columns MUST be added to that exclusion
set, and `created_by_operation_id` set explicitly. Left alone, the generic copy carries the *source
row's* provenance into a row the current gesture created — a silent lie, green tests, and the worst
possible failure mode for a provenance mechanism.

### 4.2 How the id reaches the engine

The id is threaded, never ambient, never re-derived. It travels in the **operation context** already
required by the 2026-08-11 decision (): a value carrying the captured
`OperationDate`, the actor, and the operation id, replacing today's separately-passed
`(operationDate, recordedBy)` pair on repository writers.

- The parameter is **required, with no default** — so every production and test call site must supply
  it, and the *compiler* enforces the complete inventory §7 lists. No grep-based sweep is trusted for
  this.
- Pre-transaction guards (branch derivation, lock-key selection) keep using the captured date alone;
  they run before the operation row exists.
- **Two types, not one nullable id.** An **operation request** (captured date, actor, gesture kind,
  note) is what a caller builds before any transaction exists. An **operation context** is what a
  version-row write requires, and only it can produce an id. A row cannot be written from a request.
- **The mint is LAZY: it happens on first use, inside the transaction, immediately before the first
  version-row write.** The operation context is constructed from `(request, connection, transaction)`
  and memoizes the id it mints on the first ask.
- **The context is TRANSACTION-AFFINE, and enforces it.** It holds the transaction it was built from,
  and a write that presents a *different* transaction fails clearly. A memoized id is valid only inside
  the transaction that minted it: without this, a context captured from transaction A and reused in a
  second `ExecuteAsync` would attribute B's rows to A's operation while B itself minted nothing —
  provenance that is wrong rather than missing, which is worse.
  *Falsified by:* reusing a captured context in a second `ExecuteAsync` and asserting a clear failure. Not at transaction entry — a transaction can succeed
  having written no version row (OP1), and an entry-time mint would commit an orphan header on exactly
  that path. Not by the caller either: the context is the **sole mint authority**, so "who mints?" has
  one answer on both paths and no caller can mint a second row.
- **`ICompositeWriteContext` is unchanged and is not replaced.** It keeps owning the connection, the
  transaction and the enlistment check; the operation context owns provenance and nothing else. The two
  are separate parameters that travel together:

| Path | What the writer receives |
|---|---|
| Composite (`CompositeWrite.ExecuteAsync`) | `ICompositeWriteContext` (existing) **+** `OperationContext` built from the request and that same connection/transaction |
| Plain (`VersionedRepository.ExecuteWriteAsync`) | the engine's own connection/transaction **+** an `OperationContext` the engine builds from the request the caller passed in |

  The caller supplies a **request** on both paths; neither path lets a caller supply an id.
- The per-gesture `Guid.NewGuid().ToString("N")` that `RoleDeclarationService` mints today for
  `audit_log`'s `detail` JSON is **retired** by this: the operation row's id is the gesture identifier,
  and there is no second one.

### 4.3 System and batch actors

`FunctionCatalogSyncService` is not a user gesture: its actor is the constant `system-sync`, and it
performs a **separate repository transaction per function** inside a loop. So:

- No new column — `operation.username = 'system-sync'` uses the principal name the code already uses.
- `operation.kind = 'Sync'`.
- **One `operation` per item**, i.e. per atomic write, not one per run. A per-run header cannot be
  created inside a transaction that does not exist (OP1), and would be orphaned the moment the first
  item fails. Per-item also reads correctly on a per-object history screen, which is the only screen
  that exists.

## 5. `audit_log` after this change

Seven business write sites are removed: `RoleDeclarationService` ×5 (role save, grant revoke, grant
add, role close, cascaded child) and `OrgUnitDeclarationService` ×2 (add, close/cancel). What remains
is login, break-glass and signature-fail.

The atomicity property those sites carried does not disappear — it moves. Today a failing audit write
rolls back the whole composite (`FailingAuditLogWriter` tests). After this change the same tests must
assert that a failing **operation** write rolls the composite back, and that no version row can commit
without its `operation` row.

## 6. Migration order

Expand → migrate → contract; every intermediate revision keeps the suite green.

1. **Expand** — create `operation`; add both columns **nullable** to all five version tables. No code
   change. Green.
2. **Dual-write** — thread the operation context; every writer mints and stamps. Reads still come from
   the version rows as today. Green at each writer.
3. **Contract** — `created_by_operation_id` NOT NULL, once every writer and every fixture (§7) supplies
   one.
4. **Switch reads** — history screens move to §3.
5. **Remove** — the seven business `audit_log` writers and their now-duplicated tests.

**There is no backfill step, and that is a ruling, not an omission.** The requester, acting as DBA
(2026-08-18, restating a decision from earlier sessions): the application has not been released, no
database holds real data, and schema work may reset data freely while the app is being built. So the
contract step needs no legacy provenance and invents no legacy chronology — a backfill would have had
to give every legacy `operation` an `occurred_at`, and letting that column default would have collapsed
years of history onto the migration moment. Any database still holding pre-migration version rows is
**reset**, not backfilled. This phasing survives only because that ruling holds; if a populated
database ever exists, this section is wrong and must be redesigned before the migration runs.

The phases above remain phases for a different reason: each keeps the **test suite** green on its own,
which is independent of what data exists.

**Every phase that ships a migration carries three artifacts, not one.** A forward migration that adds
`operation` changes the table inventory and the schema version, and the application refuses to start
against a version it does not expect. So each such phase updates, in the same commit:

1. `migrations/V0xx__*.sql` — including its own `INSERT INTO schema_version` row;
2. `App.ExpectedSchemaVersion` (`AST/App.xaml.cs`) — the startup gate;
3. `SchemaBootstrapSmokeTests` — `CurrentSchemaVersion` **and** `ExpectedTables`.

Omitting (2) blocks startup; omitting (3) fails the bootstrap smoke test on a fresh schema.

## 7. Complete writer inventory

Every production path that must supply an operation context (verified at `b57e7e4`):

| Caller | Path |
|---|---|
| `RoleDeclarationService` | `SaveRoleDeclarationAsync` (role upsert, grant revoke, grant cancel, grant upsert); `CloseRoleDeclarationAsync` (close/cancel + cascade) |
| `OrgUnitDeclarationService` | `AddOrgUnitDeclarationAsync`; `CloseOrgUnitDeclarationAsync` (close/cancel); `EditOrgUnitDeclarationAsync` (Edit — moved behind the service 2026-08-21, backlog 0.7) |
| `FunctionCatalogSyncService` | `UpsertAsync` (metadata sync) and `CreateAsync` (new key), both directly on the repository |
| `UserRepository.UpsertAsync` | no production caller today; still takes the parameter, so one cannot appear silently |

Out of scope, stated so it is not mistaken for an omission: `UserRepository.TrySetSidOnceAsync` writes
the `user` **identity** table (first-login SID binding), not a version row. It records no period and no
business state, so it is not part of business history.

### 7.1 Raw-SQL fixtures — the compiler does NOT own this half

The required parameter makes the compiler enforce every writer that goes *through* a repository. Test
fixtures that seed rows with **raw SQL** bypass that entirely, and they are the reason the contract
step can fail with the whole production side correct. Enumerated at this commit:

| File | What it raw-inserts |
|---|---|
| `AST.Modules.IAM.Tests/Integration/IamRepositoryTestBase.cs` | `role_version`, `role_permission_version` (and the org-unit/function seeds beside them) |
| `AST.Modules.IAM.Tests/Integration/IntegrityCheckServiceTests.cs` | version rows crafted to violate an invariant |
| `AST.Modules.IAM.Tests/Integration/RolePermissionRepositoryTests.cs` | grant version rows |
| `AST.Modules.IAM.Tests/TestSupport/SelfOwningOrgUnitRepository.cs` | org-unit version rows |

Each seeds a provenance id explicitly (a fixture-owned `operation` row), and an `AST.Meta.Tests` guard
asserts that no raw `INSERT INTO {table}_version` anywhere in the solution omits
`created_by_operation_id` — so a fifth such fixture cannot appear silently.

Test fixtures are part of this inventory: NOT NULL lands only after every fixture supplies a non-null
operation.

## 8. Open, and deliberately not decided here

- **Business transactions.** Their tables do not exist. The requester ruled 2026-08-18 that a
  **completed** transaction is never edited in place: a state change on it is recorded as a new row,
  never as an edit of the old one. That ruling settles the `design-temporality-classes.md` §2 tension
  in principle; how the "adjusted" label and the traceability links are physically represented is
  transaction-slice design.
- **Traceability links** (adjustment / re-do chains) group transactions across many gestures and many
  days. That is a different axis from `operation_id`, it belongs on the transaction table, and neither
  link goes on `operation` (OP3).
- **Tamper evidence** (hash chain / signed checkpoints) — asked, not answered.
- **Gapless business document numbers** — a business-layer mechanism. `operation.id` is explicitly
  outside its scope (§1.1).
