# EFFECTIVE PERIOD

> Original business requirements (translated from Vietnamese, 2026-07-07). Business source of truth; the design lives in design-effective-period.md.

> Source of truth for **Declared** parameters (see §0) — those that have an **effective period**

## 0. Which parameters this document governs (added 2026-08-11)

Sections 1–7 below were written on the assumption that every parameter has an effective period.
That assumption is now bounded: **this document governs Declared parameters only.** Which
parameters those are is decided by `design-temporality-classes.md` — that is the home for the
answer, and this section only restates the questions the requester answers.

**Question 0 — is it a parameter at all?** A **command** (a request to do something at a time), an
**event record** (a log or a processed transaction, written once and never edited) and
**infrastructure bookkeeping** are not parameters and get none of this.

**Question 1 — the only classifying question.** *Does anyone need to announce a change in advance so
it takes effect on a chosen future date, or to stop it from a chosen date while the old record stays
visible for lookup?*
- **No** → the parameter keeps only its present value plus a change log. No effective period.
- **Yes** → the full model in sections 1–7 applies.
- **Unclear** → stop and ask the requester. There is no safe default in either direction.

**The backdating flag.** *Could someone backdate an entry to change what already happened — rights,
blocking, approval?* If yes, the parameter may be declared only from today forward. This is the
**default**; a parameter is exempt only when a real business document is routinely signed before it
is entered (today: the org unit and its representative).

**Freeze, don't re-resolve.** A processed transaction records the parameter **version** it used.
Reading a past date is for looking things up and for audit, never to recompute a business result —
correcting a past period does not silently change an old transaction. Section 1's "resolution by
transaction date" therefore describes how a parameter is picked **at the moment of processing**; it
is not a promise that the system can re-derive an old result later.

## 1. Foundational principle
- Every date is formatted to the common standard: yyyy-mm-dd.
- **The effective period is a declared attribute**: a parameter is usable only when its effective period contains or matches the business transaction date on which the parameter is used. The effective period is defined from date (F) to date (T).
- **Open period**: an effective period whose end date has not been declared; in that case the end date = 9999-12-31; **the UI shows "Not yet determined"**.
- **Resolution by TRANSACTION DATE**: a business operation has a transaction date D → the parameter is taken from the **period containing D**.
- **Core invariant**: for a given parameter, on any given day there is exactly one active version or none at all (a business error: the parameter has not been declared for that effective period).
- **Append-only + audit**: an edit must NOT destroy old data — **soft-delete** the old version + **insert** a new one. Direct editing of a parameter is allowed only when that parameter has not yet been used/referenced.

## 2. Period-interval algebra — Add/Edit a period
New period `[F,T]` compared to each existing active period `[a,b]` (b may = 9999-12-31):

| # | Situation | Condition | Handling (append-only) |
|---|---|---|---|
| 1 | Disjoint | T<a or F>b | Insert new. A date gap → **gap warning**. |
| 2 | Adjacent | F=b+1 / T=a−1 | Insert new (seamless). |
| 3 | Overlaps head | F≤a≤T<b | Soft-delete old; tail remnant [T+1,b]; insert new. |
| 4 | Overlaps tail | a<F≤b≤T | Soft-delete old; head remnant [a,F−1]; insert new. |
| 5 | Sub-period (new ⊂ old) | a<F & T<b | Soft-delete old; head + tail remnants; insert new (splits into 2). |
| 6 | New engulfs old | F≤a & b≤T | Soft-delete old (no remnant); insert new. |
| 7 | Exact match | F=a & T=b | = correction: soft-delete + insert same period. |
| 8 | Overlaps multiple periods | new spans ≥2 periods | Repeat each period (#3–7) + insert 1 new period. |

> Every case: the original record keeps `is_active=0` (audit).
> **Closing an open period:** an open period `[x, 9999-12-31]` + a following period `[F,…]` (F>x) → automatically falls into an overlap case (3/5/6) → the app auto-determines the old period's end date = F−1 (via a remnant), keeping the original open period at is_active=0.

## 3. Resolution at business-run time
- **Missing coverage** (no period contains D) → **STOP the business flow + report clearly**: "Parameter 'X' has no effective value on date DD/MM/YYYY." ABSOLUTELY no falling back to a default value/other period.

## 4. Temporal referential integrity (temporal-FK) — STRICT level (multiple levels of related parent-child parameters tied to the effective period)
- **Parent before child** (declaration gating, multi-level).
- **Full-period coverage check:** saving a child `[F,T]` → the parent (the logical key) must cover it with an effective period that is **continuous across the whole [F,T]** using valid periods (the correct type/relationship). Missing/gapped → **BLOCKED** ("Parent parameter '…' has no declared effective period for the range [d1–d2]."). A child may be covered by **multiple parent periods** as long as they are continuous.
- **R3 — Reverse-FK:** narrowing/deleting a parent's period such that the child loses coverage → **BLOCKED** ("N child parameters depend on the range […]"), the children must be handled first.
- **R4 — Multi-level:** checked edge-by-edge between parent and child; transitive across levels.

## 5. Deleting a specific period
Soft-delete the exact version (`is_active=0`, audit) + **gap warning** + blocked per temporal-FK + **protecting the mandatory existence of the original version**.

## 6. Referencing the effective period
- The effective period is referenced by other app functions to obtain the parameter's value in use.
- The effective period may also be referenced by modules/features developed later.
- The effective period may be referenced across multiple layers (for example, when processing a business operation, the app considers the effective periods of the user, the role, the org unit, and other business parameters; an org unit is declared effective in period A, a user is declared effective in period B, and period B relates to period A, such as being contained within A or touching A).

## 7. Research and open questions
- Issues that may arise related to the effective period:
    + Multi-level reference issues to the effective period, the risks and handling when declarations overlap.
    + Editing or deleting a parameter with an effective period (whether the parameter has been used or not).
    + Extending other functions/modules that use an already-declared parameter/effective period (principle: must not affect the rest of the project).
    + Handling a business operation that references an effective period that has not been declared.
    + The performance of the SQL and the application.
    + Concurrent declaration/editing of data.
    + Boundary issues related to the value 9999-12-31.
    + Editing a parameter backward in time (editing the past, a parameter already used, a business operation already processed that referenced the old period's parameter value, ...).
    + References must not break when a parameter or effective period is given a new version.
    + Other issues not yet considered.
- The design should draw on how other applications and systems model an effective period, on authoritative technical documentation, and on existing built-in database features, rather than inventing a model from scratch.
- The model, technique and technology chosen must meet every requirement above, and the reasoning for the choice is recorded with the design.
