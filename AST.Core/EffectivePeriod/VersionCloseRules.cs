using ErrorOr;

namespace AST.Core.EffectivePeriod;

// Which server-derived operation the target version's own [From, today] relationship selects.
public enum VersionCloseBranch
{
    Retire,
    CancelPlan,
}

// Entity-agnostic close/cancel date-rule guards for ANY versioned entity.
//
// The branch (Retire vs CancelPlan) is deliberately DERIVED here from `targetPeriod.From >= today`
// (D1, 2026-08-10 — see Validate), never accepted as a caller-supplied `bool` — a caller must not be
// able to assert its own branch
// (feedback-required-param-over-optional-for-correctness): that assertion is exactly the class of
// bug this component exists to make impossible.
//
// The 6-rule evaluation order below IS the contract, not an implementation detail — reordering it
// changes which error a caller sees for an input satisfying more than one rule (an already-lapsed
// version must report VersionAlreadyEnded, never the generic range guard; an exact-match close date
// must report CloseDateEqualsVersionEnd, never the also-true `>=` range guard). This is the single
// home of these rules — no caller may re-implement or reorder them (docs/shared-components.md §⑥).
[SharedComponent]
public static class VersionCloseRules
{
    public static class Codes
    {
        public const string CloseDateNotApplicableToCancelPlan = "VersionClose.CloseDateNotApplicableToCancelPlan";
        public const string VersionAlreadyEnded = "VersionClose.VersionAlreadyEnded";
        public const string CloseDateRequired = "VersionClose.CloseDateRequired";
        public const string CloseDateInPast = "VersionClose.CloseDateInPast";
        public const string CloseDateEqualsVersionEnd = "VersionClose.CloseDateEqualsVersionEnd";
        public const string CloseDateOutsideVersionPeriod = "VersionClose.CloseDateOutsideVersionPeriod";

        // The exhaustive, enumerable set of close/cancel codes declared above. This does NOT by itself
        // enforce that any screen's presentation-text map covers every code — it only MAKES that
        // completeness check writable (a map could still add a code to `All` and separately leave its
        // own map with an unhandled/fallthrough case; that gap is closed by a per-screen test, not by
        // this list existing). Deliberately a MANUALLY maintained list (not reflection-generated) so
        // VersionCloseRulesTests, which independently reflects over this class's public const string
        // fields, fails the moment a 7th constant is declared above without also being added here.
        public static readonly IReadOnlyList<string> All =
        [
            CloseDateNotApplicableToCancelPlan,
            VersionAlreadyEnded,
            CloseDateRequired,
            CloseDateInPast,
            CloseDateEqualsVersionEnd,
            CloseDateOutsideVersionPeriod,
        ];
    }

    // effective_to is the INCLUSIVE last-effective-day (D4, rule-effective-period) — cessation begins
    // the NEXT calendar day. This member is the named home of that INCLUSIVE-END CONVENTION for any
    // operator-facing "ngừng từ ngày X" text (a second close screen must call this instead of
    // re-deriving the convention inline — a locked data convention re-derived in a second home is
    // exactly the drift class this component exists to prevent, see the type-level comment above). It
    // is deliberately NOT a second arithmetic implementation: the actual "add one day, guard the
    // 9999-12-31 boundary" arithmetic lives in exactly one place, `EffectivePeriod.NextDay` (its own
    // comment: "the 9999-12-31 boundary = 'infinity': no overflow arithmetic at the boundary", §4) —
    // this method delegates to it rather than calling `.AddDays(1)` itself, so that boundary rule keeps
    // exactly one enforcement point.
    //
    // Return stays nullable, matching `NextDay`: an open-ended version (effective_to = OpenEnd) has no
    // cessation day, and that is a legal, expected state to be looking at on a close form — not a
    // caller bug — so null is the answer to propagate, not an exception.
    public static DateOnly? CeaseFrom(DateOnly lastEffectiveDay) => EffectivePeriod.NextDay(lastEffectiveDay);

    // Single home of the Retire-vs-CancelPlan boundary: depends ONLY on the target version's own
    // start versus business today (D1, 2026-08-10) — the requested close date never changes it.
    // Exposed as `public` so a UI can present the correct close form (which fields to show, which
    // confirmation copy) from the SAME home the server decides on, instead of re-deriving the
    // boundary from a status label (`VersionStatus`) or a private copy of the `>=` comparison — that
    // indirect re-derivation is exactly the defect class this component exists to prevent (see the
    // type-level comment above; the pre-D1 org-unit screen branched on `Status == VersionStatus.Pending`,
    // which diverges from this boundary for a same-day-effective version).
    //
    // This does NOT weaken the "a caller must not assert its own branch" invariant above: `BranchFor`
    // is a pure derivation from the same two inputs `Validate` already takes (today, targetPeriod) —
    // it does not accept a caller-supplied branch — and `Validate` itself still derives its branch by
    // calling this same method rather than trusting anything a caller passes in.
    public static VersionCloseBranch BranchFor(DateOnly today, EffectivePeriod targetPeriod) =>
        targetPeriod.From >= today ? VersionCloseBranch.CancelPlan : VersionCloseBranch.Retire;

    public static ErrorOr<VersionCloseBranch> Validate(
        DateOnly today,
        EffectivePeriod targetPeriod,
        DateOnly? requestedCloseDate)
    {
        // Server derives the branch — the caller does not get to pick "close" vs "cancel" itself
        // (prevents a caller self-asserting which server operation runs).
        //
        // D1 (requester decision, 2026-08-10): `>=`, not `>`. A version whose
        // coverage starts TODAY has not completed a single effective day — no date-sequence
        // contradiction is possible because no close date has been entered against it yet — so it
        // "never really counted" and is cancellable, exactly like a strictly-future plan. Consequence
        // accepted by the requester: a same-day-effective version can no longer be closed to a
        // one-day period [today, today] — that shape is reachable only through Edit.
        var isCancelPlanBranch = BranchFor(today, targetPeriod) == VersionCloseBranch.CancelPlan;

        if (isCancelPlanBranch)
        {
            if (requestedCloseDate is not null)
            {
                return Error.Validation(
                    Codes.CloseDateNotApplicableToCancelPlan,
                    "A cancel-plan for a version whose coverage starts today or later must not specify an effective-through date.");
            }

            return VersionCloseBranch.CancelPlan;
        }

        // An ACTIVE version that has already fully lapsed (its own effective_to is before today) can
        // NEVER satisfy both "close date >= today - 1" (CloseDateInPast below) and "close date within
        // [From, To]" (the range guard below) — the two are jointly unsatisfiable once the version's
        // own end is in the past. Reporting the date-range guard in that case would give the wrong
        // reason; check the real state first.
        if (targetPeriod.To < today)
        {
            return Error.Validation(
                Codes.VersionAlreadyEnded,
                $"This version already ended on {targetPeriod.To:yyyy-MM-dd} and can no longer be closed (retire) — its coverage is already fully in the past.");
        }

        if (requestedCloseDate is null)
        {
            return Error.Validation(
                Codes.CloseDateRequired,
                "A close (retire) requires an effective-through date.");
        }

        // The close date floor is enforced HERE, not only on screen — retiring with a date before this
        // floor would retroactively shrink coverage and retroactively cascade into any dependent using
        // this version's effective period, rewriting already-completed days.
        //
        // D2 (requester decision, 2026-08-10): the floor relaxes by exactly ONE
        // day, from `today` to `today - 1`. The last-effective-day may be `today - 1`, i.e. the entity
        // ceases effect FROM today — anything earlier still rewrites a completed day and stays
        // rejected. `effective_to` remains the INCLUSIVE last-effective-day; this does NOT re-base the
        // convention. Data already produced today under this entity keeps its values — nothing is
        // recomputed.
        if (requestedCloseDate.Value < today.AddDays(-1))
        {
            return Error.Validation(
                Codes.CloseDateInPast,
                "The close (retire) date must be yesterday (today - 1) or later.");
        }

        // effective_to is INCLUSIVE — the LAST day the version is still effective. A close date EQUAL
        // to it is a no-op the underlying engine would reject; the system must never report "closed"
        // when nothing was written, so this is its OWN validation error rather than falling through to
        // the range guard below — reachable on the most common shape: an open-ended version left at
        // the open-end sentinel with the close date also left at the open-end sentinel.
        if (requestedCloseDate.Value == targetPeriod.To)
        {
            return Error.Validation(
                Codes.CloseDateEqualsVersionEnd,
                "The close (retire) date equals the version's own effective-through date, which would close nothing — choose an earlier date.");
        }

        // The close date must fall within the TARGET version's own effective period. `>=` (not `>`):
        // together with the equality guard above, this fully pre-empts everything the underlying
        // engine itself rejects on a shrink/close — nothing that reaches it from here can still trip
        // an engine-level guard.
        //
        // The `< targetPeriod.From` half stays dead code — but for a DIFFERENT, tighter reason than
        // before D1/D2, and it is now a coupled invariant, not an incidental one:
        //   - the Retire branch is only reached when `targetPeriod.From < today` (D1 moved the
        //     CancelPlan cutover to `From >= today`, so Retire now requires `From <= today - 1`), and
        //   - rule #4 (CloseDateInPast) requires `requestedCloseDate >= today - 1` to pass (D2 moved
        //     the floor to `today - 1`).
        //   Combined: requestedCloseDate >= today - 1 >= From always holds on this branch, so
        //   "date < From" can never be the reported reason.
        // This unreachability holds ONLY because the cancel cutover (D1) and the close-date floor (D2)
        // sit on the SAME day (`today - 1` in both directions, one exclusive one inclusive). If either
        // boundary is re-tightened independently of the other in a future change, this half becomes
        // LIVE again — re-verify this comment (and add a real discriminating test, not one that merely
        // asserts the guard fires) before shipping such a change. Kept here for defensive symmetry with
        // the engine's own half-open cut window, not because it is independently reachable today.
        if (requestedCloseDate.Value < targetPeriod.From || requestedCloseDate.Value >= targetPeriod.To)
        {
            return Error.Validation(
                Codes.CloseDateOutsideVersionPeriod,
                $"The close (retire) date must fall within the target version's own effective period [{targetPeriod.From:yyyy-MM-dd}, {targetPeriod.To:yyyy-MM-dd}].");
        }

        return VersionCloseBranch.Retire;
    }
}
