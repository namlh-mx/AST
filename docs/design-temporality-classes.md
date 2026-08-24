# Temporality classes — which parameters get an effective period

**Read this BEFORE `design-effective-period.md`.** That document defines *how* the temporal model
works once a parameter has an effective period. This one decides *whether it has one at all*, and
is the single home for the answer per entity.

Approved 2026-08-11 (`decision-log.md`). Supersedes nothing; it fills a gap — until now no document
said which parameters are temporal, so the safest-looking default was to give every parameter the
heaviest model and then work around it.

---

## 1. Commitment 0 — freeze, don't re-resolve

A processed transaction records the parameter **version** it used. Reading a past date is for
**display and audit only** — never to recompute a business result.

Consequence, and the reason this commitment is worth stating: **retroactive period editing is a
data-correction feature, not a business-recomputation feature.** Design pressure that would only be
justified by "an old computation must silently change when we correct a past period" is not
justified here.

This is a **commitment binding future work**, not a description of finished code: no transaction
flow exists yet. The transaction / *adjustment* slice must carry an acceptance row proving it
captures the business date once and stores the version ids it used. Nothing in today's code
contradicts it — authorization captures today once and resolves function, user and grant from it;
the arbitrary-`asOf` repository surface serves tests, history display and write validation, not a
live computation.

## 2. Step 0 — eligibility gate

The classification applies to **parameters / master data**: facts about the organisation that
business processing reads in order to decide something. Three shapes are outside it and are never
classified:

| Shape | What it is | Members today |
|---|---|---|
| **Command** | a request to *do* something at a time — not a fact that is true over a period | `app_control` (including `deadline_at`) |
| **Ledger** | immutable event records; append-only, never edited, no `isactive` | `audit_log`; later, **completed** transactions |
| **Infrastructure** | bookkeeping the application does not reason about | `schema_version` |

Run this gate first. Without it a future-dated **command** is indistinguishable from a future-dated
**declaration**, and a ledger looks like a parameter that merely happens to never change.

**The Ledger classification attaches at COMPLETION** (requester ruling 2026-08-18, recorded in
`decision-log.md`). A completed transaction is never edited or deleted in place — a state change on it,
including a privileged flip of a reversal back to a normal transaction, is recorded as a new row, never
as an edit of the old one. How a **not-yet-completed** transaction is edited or deleted is
transaction-slice design and is deliberately not settled here; the row above says nothing about it.

This document only rules those three shapes **out of the effective-period model**; it does not
define their write operations or their UI. Each keeps the shape its own home already gives it
(`design-iam-schema.md` §2 for the infra tables, `rule-platform-infra` for the platform surfaces).
Do not read §3's Writes/UI rows as applying to them.

## 3. The two classes

| | **Current** | **Declared** |
|---|---|---|
| Meaning | only the present value matters | people declare it with a validity period |
| Schema | one table, `isactive`, **no period columns** | identity table + `_version` table (`design-effective-period.md` §1) |
| Writes | insert / soft-delete | Upsert + Close + Cancel + `operation_kind` |
| Read | latest active row | resolve-at-D, plus the `as-of` / `overlap` / `existence-any` / `ordered-pick` shapes named in `rule-effective-period` |
| As a temporal-FK parent | **cannot be one** | STRICT coverage check (`design-effective-period.md` §5) |
| UI | ordinary edit form | declaration screen: period, history, Close, Cancel |
| Rules that apply | no hard delete, `isactive`, audit (`rule-soft-delete` §1) | the above **plus** the whole of `rule-effective-period` and D1–D13 |

**The table above describes a DB table.** A parameter that deliberately lives OUTSIDE the database —
connection and config files — is Current *in policy* (present value only, no periods, never a
temporal-FK parent), but its storage, signature and audit follow `rule-platform-infra`, not
`rule-soft-delete`'s column standard: there is no `isactive` column to set. Membership test: if the
parameter is not a row in the application database, take the policy from this class and the
mechanics from `rule-platform-infra`.

There is no third class. A "system-maintained, always-open" class was proposed and rejected on
2026-08-11: its only candidate (`function`) has admin-confirmed close/reopen actions that carry a
date, and its supposed guarantee — *a perpetually-open parent can never block a child* — is a
property of the data, not of the validator, which is class-blind and raises
`TemporalFk.ParentGap` for any gap.

## 4. The test

| | Question | Answer → |
|---|---|---|
| **Step 0** | Is it a parameter at all, or a command / an event record / infrastructure? | not a parameter → outside; use the matching shape in §2 |
| **Q1** | Does anyone need to **announce a change in advance** so it takes effect on a chosen future date, **or** to **stop** it from a chosen date while the old record stays visible for lookup? | **No → Current. Yes → Declared.** |
| **Flag** | Could someone **backdate an entry to change what already happened** — rights, blocking, approval? | **Yes → `NoBackdate`** (today→forward only) |
| **Flag+** | Beyond that: does the business require the declaration to be made **at the moment the change actually happens**, so that announcing it in advance would itself be wrong? | **Yes → `Immediate`** (today ONLY — no future start, no scheduled stop; implies `NoBackdate`) |

**If Q1 is unclear, STOP and ask the requester.** There is deliberately no cheap default. Promoting
a `Current` row that later turns out to be a temporal-FK parent is not free, so guessing is not
safer than asking.

**`NoBackdate` is the default for a Declared entity.** An entity is exempt only when a real business
document is routinely signed before it is entered. `NoBackdate` is an orthogonal flag, not a class:
two entities of the same class may differ on it.

## 5. Register — the single home for "what class is X"

Adding or reclassifying a row here is a decision: it needs a `decision-log.md` line in the same
commit.

| Concept | Class | Backdate | Note |
|---|---|---|---|
| org unit | Declared | **allowed** | founding / merger decision is signed before entry |
| org-unit representative | Declared | **allowed** | appointment decision is signed before entry. Screen not built; re-run Q1 when it is designed |
| role | Declared | `Immediate` | 2026-08-12: declared at the moment it happens, effective the same day; no future start, no scheduled stop |
| permission (role grant) | Declared | `Immediate` | 2026-08-12: same as role. Changing `scope_level` is revoke-old + grant-new, never a second version — see the Model 2 note below |
| user | Declared | `NoBackdate` | advance declaration IS allowed (requester 2026-08-12) — deliberately NOT `Immediate`. Permanent leaving closes the user; temporary absence locks the account |
| function (catalog) | Declared | `NoBackdate` | system-written for add / metadata-update; **admin-confirmed** for remove / re-add (`design-function-catalog-sync.md`) |
| user lock / admin lock | Declared | `NoBackdate` | the lock chain's date policy, settled 2026-08-07; this flag is its name |
| business / system parameters | run the test per parameter | per parameter | no blanket default |
| connection / config settings | Current | n/a | file-based + audit; not a DB parameter |
| `app_control` | Command — outside | n/a | `deadline_at` is a command time, not an effective period |
| `audit_log` | Ledger — outside | n/a | |
| `schema_version` | Infrastructure — outside | n/a | outside this rule entirely |

### Notes that keep two rows from being misread

- **`function`'s `effective_from = 2000-01-01` is a default starting value, not a guarantee.** It
  exists so a grant that starts early is not blocked for lack of parent coverage. It does **not**
  make a function unable to block a child: closing a function that still has a grant is blocked,
  and an integration test asserts exactly that. Do not reason "function is epoch-pinned, therefore
  coverage is never a problem".
- **`Immediate` is not "more `NoBackdate`" — it closes the OTHER end.** `NoBackdate` forbids the
  past and leaves the future open; `Immediate` collapses the window to today alone. The two entities
  carrying it, `role` and `permission`, are the ones where a version sitting in the future is what
  makes "revoke" ambiguous: a plan that has never been effective cannot be stopped the way a running
  grant is. Removing the future removes the ambiguity by construction rather than by algorithm. Do
  not generalise it to a neighbouring row because it "sounds stricter and therefore safer" — `user`
  and `user lock` deliberately keep advance declaration, and `org unit` deliberately keeps backdating.
- **`user` is Declared but the code does not yet offer Close.** `IUserRepository` exposes upsert and
  SID writes only, and `user_version` has neither `cancelled` nor `operation_kind`. A permanently
  departed user therefore keeps resolving in authorization. This is latent only because no
  user-declaration screen exists; it becomes reachable the moment one does, so Close must ship in
  the same slice as that screen, not after it.

## 6. What this classification does not change

The inclusive-end convention, the open-end value `9999-12-31`, the uni-temporal choice (D1), D1–D13,
the 8-case algebra, and globally-STRICT temporal-FK all stand unchanged. Per-edge temporal-FK modes
were considered and deferred: there is no second real case, and the pressure that produced the
epoch workaround came from the missing front door, not from the FK rule.
