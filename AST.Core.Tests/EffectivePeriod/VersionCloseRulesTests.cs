using System.Reflection;
using AST.Core.EffectivePeriod;
using ErrorOr;
using FluentAssertions;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Tests.EffectivePeriod;

// Pins VersionCloseRules.Validate (AST.Core/EffectivePeriod/VersionCloseRules.cs) — pure unit tests,
// no DB (this component has no infrastructure dependency). Every error case asserts BOTH
// FirstError.Code AND FirstError.Type (rule-testing invariant 6), never just IsError.
public class VersionCloseRulesTests
{
    private static readonly DateOnly Today = new(2026, 8, 10);

    // FR1 — pin the actual wire string VALUES, not just the symbol.
    // Every other test in this file compares against VersionCloseRules.Codes.X, so it only proves
    // internal self-consistency; it would stay 100% green even if a code's string VALUE changed,
    // silently breaking any consumer (e.g. the Role close screen) that maps these codes by literal.
    // Precedent for treating a code string as a persisted/wire contract: docs/shared-components.md
    // (ConfigErrors — "rename the const, not the string").
    [Fact]
    public void Codes_PinTheActualWireStringValues()
    {
        VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan.Should().Be("VersionClose.CloseDateNotApplicableToCancelPlan");
        VersionCloseRules.Codes.VersionAlreadyEnded.Should().Be("VersionClose.VersionAlreadyEnded");
        VersionCloseRules.Codes.CloseDateRequired.Should().Be("VersionClose.CloseDateRequired");
        VersionCloseRules.Codes.CloseDateInPast.Should().Be("VersionClose.CloseDateInPast");
        VersionCloseRules.Codes.CloseDateEqualsVersionEnd.Should().Be("VersionClose.CloseDateEqualsVersionEnd");
        VersionCloseRules.Codes.CloseDateOutsideVersionPeriod.Should().Be("VersionClose.CloseDateOutsideVersionPeriod");
    }

    // A1 — future plan + a close date supplied is rejected: a caller cannot assert a close date
    // against a version whose retirement branch does not accept one.
    [Fact]
    public void FuturePlan_WithCloseDate_IsRejected()
    {
        var period = new Period(Today.AddDays(10), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(20));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A2 — future plan + null date succeeds as CancelPlan.
    [Fact]
    public void FuturePlan_WithNullDate_SucceedsAsCancelPlan()
    {
        var period = new Period(Today.AddDays(10), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, null);

        result.IsError.Should().BeFalse();
        result.Value.Should().Be(VersionCloseBranch.CancelPlan);
    }

    // A3 — target already ended before today.
    [Fact]
    public void Retire_VersionAlreadyEnded_IsRejected()
    {
        var period = new Period(Today.AddDays(-30), Today.AddDays(-1));

        var result = VersionCloseRules.Validate(Today, period, Today);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.VersionAlreadyEnded);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A4 — retire branch with a null close date.
    [Fact]
    public void Retire_NullCloseDate_IsRejected()
    {
        var period = new Period(Today.AddDays(-30), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, null);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateRequired);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A5 — D2 (2026-08-10): the close-date floor relaxes by exactly one day. today - 1 is now ACCEPTED
    // (the entity may cease effect FROM today) — pins the new boundary is not rejected.
    [Fact]
    public void Retire_CloseDateOneDayInPast_Succeeds()
    {
        var period = new Period(Today.AddDays(-30), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(-1));

        result.IsError.Should().BeFalse();
        result.Value.Should().Be(VersionCloseBranch.Retire);
    }

    // A5b — anything earlier than the relaxed floor (today - 2) still rewrites an already-completed
    // day and stays rejected.
    [Fact]
    public void Retire_CloseDateTwoDaysInPast_IsRejected()
    {
        var period = new Period(Today.AddDays(-30), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(-2));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateInPast);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A6 — retire branch, close date equals To, using the most-reachable production shape:
    // To = 9999-12-31 (open-ended, "Không xác định").
    [Fact]
    public void Retire_CloseDateEqualsOpenEndedVersionEnd_IsRejected()
    {
        var period = new Period(Today.AddDays(-30), Period.OpenEnd);

        var result = VersionCloseRules.Validate(Today, period, Period.OpenEnd);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateEqualsVersionEnd);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A7 — MOVED under D1 (2026-08-10): a version whose own From == today is now the CancelPlan
    // branch (`From >= today`), not Retire — so supplying a close date at all is rejected as
    // "not applicable to a cancel-plan", regardless of what that date is.
    [Fact]
    public void SameDayFromVersion_WithCloseDateBeforeToday_IsRejectedAsCancelPlanWithDate()
    {
        var period = new Period(Today, Today.AddDays(60));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(-1));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A7b — D1 core boundary pin: a version whose own From == today (not just From > today, A2's
    // case) succeeds as CancelPlan when no close date is supplied. This is the same-day cancel
    // eligibility the requester decision widens.
    [Fact]
    public void SameDayFromVersion_WithNullDate_SucceedsAsCancelPlan()
    {
        var period = new Period(Today, Today.AddDays(60));

        var result = VersionCloseRules.Validate(Today, period, null);

        result.IsError.Should().BeFalse();
        result.Value.Should().Be(VersionCloseBranch.CancelPlan);
    }

    // A8 — retire branch, close date after To.
    [Fact]
    public void Retire_CloseDateAfterTo_IsRejected()
    {
        var period = new Period(Today.AddDays(-30), Today.AddDays(30));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(31));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateOutsideVersionPeriod);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A9 — retire branch, valid date strictly inside the period succeeds as Retire.
    [Fact]
    public void Retire_ValidDateStrictlyInside_SucceedsAsRetire()
    {
        var period = new Period(Today.AddDays(-30), Today.AddDays(30));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(10));

        result.IsError.Should().BeFalse();
        result.Value.Should().Be(VersionCloseBranch.Retire);
    }

    // A10 — ORDERING: To < today AND the date is also outside the period -> must report
    // VersionAlreadyEnded, NOT CloseDateOutsideVersionPeriod (rule #2 fires before rule #6).
    [Fact]
    public void Retire_AlreadyEndedAndOutsidePeriod_ReportsVersionAlreadyEnded()
    {
        var period = new Period(Today.AddDays(-60), Today.AddDays(-10));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(-100));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.VersionAlreadyEnded);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A11 — ORDERING: date == To, where rule #6 (>=) would also fire -> must report
    // CloseDateEqualsVersionEnd, NOT CloseDateOutsideVersionPeriod (rule #5 fires before rule #6).
    [Fact]
    public void Retire_CloseDateEqualsVersionEnd_ReportsEqualsNotOutsidePeriod()
    {
        var period = new Period(Today.AddDays(-30), Today.AddDays(30));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(30));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateEqualsVersionEnd);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A12 — MOVED under D1 (2026-08-10): a same-day-From version (From == today) with a close date
    // equal to today is still the CancelPlan branch (never Retire), so it is rejected the same way as
    // any other close date supplied against a cancel-plan.
    [Fact]
    public void SameDayFromVersion_WithCloseDateEqualsToday_IsRejectedAsCancelPlanWithDate()
    {
        var period = new Period(Today, Today.AddDays(30));

        var result = VersionCloseRules.Validate(Today, period, Today);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // A12b — the Retire-branch equivalent of "date == From is a legal cut point" now requires
    // From == today - 1 (the earliest From that still reaches the Retire branch under D1). Pins the
    // D2 floor and the D1 branch cutover meeting at the SAME day without contradiction.
    [Fact]
    public void Retire_FromEqualsFloor_CloseDateEqualsFrom_Succeeds()
    {
        var period = new Period(Today.AddDays(-1), Today.AddDays(30));

        var result = VersionCloseRules.Validate(Today, period, Today.AddDays(-1));

        result.IsError.Should().BeFalse();
        result.Value.Should().Be(VersionCloseBranch.Retire);
    }

    // FR4 — boundary for guard #2 (VersionAlreadyEnded): To == today.
    // No other case in this file exercises To == today, so mutating the guard's `<` to `<=` would
    // keep the rest of the suite green while silently changing behaviour (a version ending exactly
    // today would then report VersionAlreadyEnded instead of the correct CloseDateEqualsVersionEnd).
    // Verified as a discriminating fixture: mutating `targetPeriod.To < today` to `<=` in
    // VersionCloseRules.cs turned this test RED; the guard was then restored and re-verified green.
    [Fact]
    public void Retire_ToEqualsToday_ReportsCloseDateEqualsVersionEnd_NotVersionAlreadyEnded()
    {
        var period = new Period(Today.AddDays(-30), Today);

        var result = VersionCloseRules.Validate(Today, period, Today);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.CloseDateEqualsVersionEnd);
        result.FirstError.Type.Should().Be(ErrorType.Validation);
    }

    // B1-B3 — pin VersionCloseRules.BranchFor directly on both sides of the D1 cutover (`From >= today`
    // selects CancelPlan). BranchFor is the single home a UI consumes to derive the branch without
    // re-deriving the comparison itself (see the type-level comment on BranchFor).
    [Fact]
    public void BranchFor_FromOneDayBeforeToday_IsRetire()
    {
        var period = new Period(Today.AddDays(-1), Period.OpenEnd);

        VersionCloseRules.BranchFor(Today, period).Should().Be(VersionCloseBranch.Retire);
    }

    [Fact]
    public void BranchFor_FromEqualsToday_IsCancelPlan()
    {
        var period = new Period(Today, Period.OpenEnd);

        VersionCloseRules.BranchFor(Today, period).Should().Be(VersionCloseBranch.CancelPlan);
    }

    [Fact]
    public void BranchFor_FromOneDayAfterToday_IsCancelPlan()
    {
        var period = new Period(Today.AddDays(1), Period.OpenEnd);

        VersionCloseRules.BranchFor(Today, period).Should().Be(VersionCloseBranch.CancelPlan);
    }

    // C1 — CeaseFrom is the single home of "inclusive last-effective-day -> next calendar day". Plain
    // mid-month case.
    [Fact]
    public void CeaseFrom_ReturnsTheNextCalendarDay()
    {
        VersionCloseRules.CeaseFrom(new DateOnly(2026, 8, 10)).Should().Be(new DateOnly(2026, 8, 11));
    }

    // C2 — month-end boundary rolls into the next month.
    [Fact]
    public void CeaseFrom_MonthEndBoundary_RollsIntoNextMonth()
    {
        VersionCloseRules.CeaseFrom(new DateOnly(2026, 8, 31)).Should().Be(new DateOnly(2026, 9, 1));
    }

    // C3 — year-end boundary rolls into the next year.
    [Fact]
    public void CeaseFrom_YearEndBoundary_RollsIntoNextYear()
    {
        VersionCloseRules.CeaseFrom(new DateOnly(2026, 12, 31)).Should().Be(new DateOnly(2027, 1, 1));
    }

    // C4 — an open-ended version (effective_to = OpenEnd) has no cessation day: a legal, expected state
    // to be looking at on a close form, not a caller bug — so CeaseFrom returns null (matching
    // EffectivePeriod.NextDay) rather than throwing.
    [Fact]
    public void CeaseFrom_OpenEnd_ReturnsNull()
    {
        VersionCloseRules.CeaseFrom(Period.OpenEnd).Should().BeNull();
    }

    // C5 (rule-testing invariant 6 — a guard test must be a real DISCRIMINATING check, not a second
    // hard-coded copy of the same 6 strings): reflects independently over Codes' own public string
    // fields and asserts Codes.All is exactly that set — fails if a 7th code is declared without also
    // being added to Codes.All (the reflected set would then have 7 entries against All's 6), and fails
    // if All drifts to contain something Codes no longer declares. The filter deliberately catches BOTH
    // `const string` and `static readonly string` shapes (not just `IsLiteral`) so a future code
    // declared either way is still picked up by this independent reflection — only `Codes.All` itself
    // (FieldType `IReadOnlyList<string>`, not `string`) is excluded.
    [Fact]
    public void Codes_All_ContainsExactlyTheDeclaredConstants()
    {
        var declaredCodes = typeof(VersionCloseRules.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(string) && (f.IsLiteral || f.IsInitOnly))
            .Select(f => (string)(f.IsLiteral ? f.GetRawConstantValue()! : f.GetValue(null)!))
            .ToArray();

        VersionCloseRules.Codes.All.Should().BeEquivalentTo(declaredCodes);
    }
}
