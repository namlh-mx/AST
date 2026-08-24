# Design: Function Catalog Sync (`function`) from Code

> **Status:** APPROVED 2026-07-04 (brainstorm + requester interview).
> This document captures all decisions on **function catalog sync** (item C2). It is the source of truth for
> sync semantics; for table DDL + effective-period algebra see `docs/design-iam-schema.md §1.4` and
> `docs/design-effective-period.md`. Foundation approved earlier: fixed epoch `2000-01-01` (`docs/archive/2026-07-03-addendum-proposals.md §C2`).

## 1. Context
The function catalog has **source of truth = code**: each function (screen/task) is declared by the developer
with a stable `function_key` following the `Module.Entity.Action` convention (e.g. `Transfer.Report.View`). The
app **syncs** this list into the `function`/`function_version` tables so the admin has something to **assign
permissions to** (`role_permission` = role x function x scope). `function` is the **parent table** of
`role_permission` (strict temporal-FK, D8): each `function_key` is assigned one **identity** `function.id`; **all
permissions/business logic point to this identity, not to a specific version** -> changing the "wrapper"
(metadata) never breaks permissions; a version is never physically deleted -> history never breaks.

## 2. Two Risks Found While Drafting C2 (2026-07-04)
- **R1 — "absent from code" is not "intentionally deleted".** The earlier design treated "removed from code"
  as "auto-close the period". But a function can be absent at sync time because a **module failed to load /
  temporarily failed to load** (even though everyone shares one build on the network drive, a load failure can
  still happen). Auto-closing immediately -> **wrongly cuts permissions in bulk**, then reopening them next
  time -> churn; and it could be wrongly blocked by reverse-FK, causing a sync error.
- **R2 — re-add was undefined.** A `function_key` gets removed then added back to the code. Treating it as
  "brand new" -> creates a **duplicate identity** (2 `function.id` rows for the same key) -> the old permission
  configuration silently becomes orphaned, **breaking invariant #5**, leaving the app in an ambiguous data
  state.

## 3. Decisions Made
### Decision 1: Auto-sync ADDS + UPDATES ONLY. Removal = admin confirmation.
- **Automatic (every sync):** ADD new functions; UPDATE metadata of existing functions.
- **NOT automatic:** CLOSE (remove). The app only **flags a "removal candidate"** = active in the DB but not
  present in code. **Admin confirmation** on the admin screen is what actually closes it.
- *Rationale & benefits:* avoids wrongly cutting permissions when a module fails to load; **transient issues
  self-heal** (next time the module loads fully -> the function reappears -> the flag disappears on its own,
  the admin doesn't need to do anything); **no impact on regular users** (a flagged-for-removal function stays
  in effect until the admin confirms); **no repeated nagging** (once closed it is no longer a candidate).

### Decision 2: Re-add = "Option X" — reuse the same old identity.
- One `function_key` = one `function.id` **for life**. Restoring = the admin **reopens that exact identity**
  (adding a new effective period, from the date the admin picks -> `9999-12-31`). **Old permissions stay
  closed; the admin re-assigns them.**
- The app **never auto-creates a duplicate, never auto-reopens** — it only flags a **"reopen candidate"** =
  present in code, already has an old identity, but not active today.
- *Rationale:* preserves full history + keeps links intact (per invariant #5); "treated as new" is expressed
  via a **new effective period**, not a new identity. (Same model as Django auto-permissions: a stable natural
  key is reused.)
- *Considered & rejected:* "Option Y" (spawn a new identity + a history-linking pointer) — more complex (adds
  a linking column, loosens the natural-key uniqueness rule), with no meaningful added benefit.

## 4. Boundary: C2 (automatic) vs. Admin Screen (manual, later phase)
| Task | Who | When |
|---|---|---|
| Create new function (epoch `[2000-01-01, 9999-12-31]`) | **C2 automatic** | Every sync |
| Update metadata (case 7 exact match) | **C2 automatic** | Every sync |
| List "removal candidates" + "reopen candidates" | **C2 automatic (report only)** | Every sync |
| Confirm CLOSE of a function (close dependent permissions first, then close the version via the repository close operation (reverse-FK guarded)) | **Admin** | Admin-screen phase |
| REOPEN a removed function (Option X) | **Admin** | Admin-screen phase |
| Trigger sync | **Automatic** — every successful (re)connect | `StartupRunner.Rerun()` reaching `StartupMode.Connected` |

- **Trigger (locked, shipped 2026-07-29):** sync fires automatically from `AST/Startup/StartupRunner.cs`'s
  `Rerun()` whenever the resolved status reaches `StartupMode.Connected` — i.e. on every app launch that
  connects, and every time an operator re-declares/fixes the connection via `ConnectionDeclarationView`
  (which also re-runs `Rerun()`). Fire-and-forget, fail-clear logged (`rule-platform-infra` #1) — a sync
  failure never blocks startup, the catalog simply stays out of date until the next successful (re)connect.
  **Concurrent sessions — corrected 2026-08-17, this paragraph previously claimed more than the code did.**
  The two halves are not alike and must not be described together:
  - **UPDATE (case 7)** is idempotent **sequentially only**, and the distinction is not pedantry. A later
    run re-reads the metadata, finds it already matching, and does nothing. But two sessions that both
    read the metadata as stale will both upsert, and an exact-period upsert is not a no-op write: it
    **soft-deactivates the current version and inserts a replacement** (`PeriodEditor.PlanUpsert` treats an
    exact match as an overlap). They serialise on the identity lock and converge on the same final state,
    so nothing is corrupted — but the version history gains one redundant replacement row per losing
    session. Say **"converges under serialisation"**, not "idempotent", and never "harmlessly redundant".
  - **ADD (a key seen for the first time)** was **NOT** safe, and no amount of idempotence made it so. The
    decision "this key is absent" is a read, `idx_fv_key` is non-unique, and every workstation reaches this
    trigger on its own connect — so a deployment introducing a key had many machines creating it at once,
    producing two identities for one key and, later, `Function.DuplicateKey` from `GetByKeyAsync`. What makes
    it safe now is a mechanism, not a property: `FunctionRepository.CreateAsync` re-decides the key's absence
    **under `FunctionRepository.CatalogCreateLockKey`, inside the transaction that mints the identity**, and a
    caller that loses returns `FunctionCreateOutcome.KeyAlreadyPresent` instead of minting a second one.
    The guarantee's own description lives with the contract, in `AST.Core/Iam/Repositories/IFunctionRepository.cs`.

  The earlier wording ("safe under concurrent sessions BY DESIGN") is recorded here rather than deleted
  because it is why the defect went unseen: it stated a conclusion about the whole trigger from a property
  that held for only one of its two branches.

  The REMOVAL part still stays admin-only (report-only from C2, see the table above) — automatic sync never
  closes/removes a function on its own.
- **The qa condition from Slice B #1** (every shrink/close-coverage operation must invoke the reverse-FK
  validation) **is honored**: C2 automatic sync does NOT shrink/close coverage (it never touches reverse-FK);
  the actual close operation lives in the admin screen, which uses the repository close operation (which is
  reverse-FK guarded).

## 5. What C2 Does (Technical Summary)
The sync engine (see `AST.Modules.IAM/`) reads today's active functions (Global scope) and all known keys,
then per registered descriptor: metadata changed -> upsert (case 7); never seen -> create at epoch;
known-but-inactive identity -> reopen candidate (report only); active in DB but missing from code -> removal
candidate (report only). It returns a report listing created, metadata-updated, removal-candidate and
reopen-candidate keys.
`recorded_by='system-sync'`; `reason`: `code-sync-new` (create) / `metadata-sync` (update).
