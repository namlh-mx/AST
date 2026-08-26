using AST.Core.EffectivePeriod;
using AST.Core.Tests.TestSupport;
using ErrorOr;
using FluentAssertions;
using EP = AST.Core.EffectivePeriod.EffectivePeriod;
using static AST.Core.Tests.TestSupport.Dates;

namespace AST.Core.Tests.EffectivePeriod;

public class PeriodEditorTests
{
    private static readonly IPeriodEditor Editor = new PeriodEditor();

    [Fact]
    public void Case1_Disjoint_LeavesExistingUntouched_AndWarnsGap()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 8, 1), D(2020, 12, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Equal(newPeriod, plan.Operations[0].Period);
        Assert.False(plan.Operations[0].CarriesOldBusinessData);
        Assert.Null(plan.Operations[0].SourceVersionId);
        Assert.Single(plan.Warnings);
        Assert.Equal(new GapWarning(D(2020, 7, 1), D(2020, 7, 31)), plan.Warnings[0]);
    }

    [Fact]
    public void Case2_Adjacent_LeavesExistingUntouched_NoWarning()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 7, 1), D(2020, 12, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Null(plan.Operations[0].SourceVersionId);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Case3_OverlapHead_SoftDeactivatesAndKeepsTailRemnant()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 6, 1), D(2020, 12, 31));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 8, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(3, plan.Operations.Count);

        var deactivate = Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.SoftDeactivate);
        Assert.Equal(1, deactivate.ExistingVersionId);

        var remnant = Assert.Single(plan.Operations,
            o => o.Kind == VersionOpKind.Insert && o.CarriesOldBusinessData);
        Assert.Equal(new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 9, 1), D(2020, 12, 31)), remnant.Period);
        Assert.Equal(existing.Id, remnant.SourceVersionId);

        var insertNew = Assert.Single(plan.Operations,
            o => o.Kind == VersionOpKind.Insert && !o.CarriesOldBusinessData);
        Assert.Equal(newPeriod, insertNew.Period);
        Assert.Null(insertNew.SourceVersionId);

        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Case4_OverlapTail_SoftDeactivatesAndKeepsHeadRemnant()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 8, 31));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 6, 1), D(2020, 12, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(3, plan.Operations.Count);

        var deactivate = Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.SoftDeactivate);
        Assert.Equal(1, deactivate.ExistingVersionId);

        var remnant = Assert.Single(plan.Operations,
            o => o.Kind == VersionOpKind.Insert && o.CarriesOldBusinessData);
        Assert.Equal(new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 5, 31)), remnant.Period);
        Assert.Equal(existing.Id, remnant.SourceVersionId);

        var insertNew = Assert.Single(plan.Operations,
            o => o.Kind == VersionOpKind.Insert && !o.CarriesOldBusinessData);
        Assert.Equal(newPeriod, insertNew.Period);
        Assert.Null(insertNew.SourceVersionId);
    }

    [Fact]
    public void Case5_InnerSubset_ProducesHeadAndTailRemnants()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 12, 31));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 4, 1), D(2020, 8, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(4, plan.Operations.Count);

        Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.SoftDeactivate);

        var remnants = plan.Operations.Where(o => o.Kind == VersionOpKind.Insert && o.CarriesOldBusinessData).ToList();
        Assert.Equal(2, remnants.Count);
        Assert.Contains(remnants, r => r.Period == new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 3, 31)));
        Assert.Contains(remnants, r => r.Period == new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 9, 1), D(2020, 12, 31)));
        Assert.All(remnants, r => Assert.Equal(existing.Id, r.SourceVersionId));

        var insertNew = Assert.Single(plan.Operations,
            o => o.Kind == VersionOpKind.Insert && !o.CarriesOldBusinessData);
        Assert.Equal(newPeriod, insertNew.Period);
        Assert.Null(insertNew.SourceVersionId);
    }

    [Fact]
    public void Case6_Superset_SoftDeactivatesWithoutRemnant()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 4, 1), D(2020, 8, 31));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 12, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(2, plan.Operations.Count);
        Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.SoftDeactivate);
        var insertNew = Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.Insert);
        Assert.Equal(newPeriod, insertNew.Period);
        Assert.False(insertNew.CarriesOldBusinessData);
        Assert.Null(insertNew.SourceVersionId);
    }

    [Fact]
    public void Case7_ExactMatch_IsCorrectionSoftDeactivateAndReinsertSamePeriod()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 12, 31));
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 12, 31));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(2, plan.Operations.Count);
        Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.SoftDeactivate);
        var insertNew = Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.Insert);
        Assert.Equal(newPeriod, insertNew.Period);
        Assert.Null(insertNew.SourceVersionId);
    }

    [Fact]
    public void Case8_MultiSpan_CoversSeveralExistingVersionsWithSingleNewInsert()
    {
        var existing1 = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 3, 31));   // tail overlapped
        var existing2 = new FakeVersionRow(2, 100, D(2020, 4, 1), D(2020, 6, 30));   // fully covered
        var existing3 = new FakeVersionRow(3, 100, D(2020, 8, 1), D(2020, 10, 31));  // head overlapped
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 2, 1), D(2020, 9, 30));

        var result = Editor.PlanUpsert([existing1, existing2, existing3], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;

        Assert.Equal(3, plan.Operations.Count(o => o.Kind == VersionOpKind.SoftDeactivate));

        var remnants = plan.Operations.Where(o => o.Kind == VersionOpKind.Insert && o.CarriesOldBusinessData).ToList();
        Assert.Equal(2, remnants.Count);

        var headRemnant = Assert.Single(remnants,
            r => r.Period == new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 1, 1), D(2020, 1, 31)));
        Assert.Equal(existing1.Id, headRemnant.SourceVersionId);

        var tailRemnant = Assert.Single(remnants,
            r => r.Period == new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 10, 1), D(2020, 10, 31)));
        Assert.Equal(existing3.Id, tailRemnant.SourceVersionId);

        var newInserts = plan.Operations.Where(o => o.Kind == VersionOpKind.Insert && !o.CarriesOldBusinessData).ToList();
        Assert.Single(newInserts);
        Assert.Equal(newPeriod, newInserts[0].Period);
        Assert.Null(newInserts[0].SourceVersionId);

        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void OpenEndBoundary_DoesNotOverflowAndSkipsTailRemnant()
    {
        var existing = new FakeVersionRow(1, 100, D(2023, 1, 1), EP.OpenEnd);
        var newPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2024, 1, 1), EP.OpenEnd);

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Equal(3, plan.Operations.Count);

        var remnant = Assert.Single(plan.Operations, o => o.Kind == VersionOpKind.Insert && o.CarriesOldBusinessData);
        Assert.Equal(new AST.Core.EffectivePeriod.EffectivePeriod(D(2023, 1, 1), D(2023, 12, 31)), remnant.Period);
        Assert.True(remnant.Period.To != EP.OpenEnd);
        Assert.Equal(existing.Id, remnant.SourceVersionId);
    }

    [Fact]
    public void GapAfter_NewPeriodBeforeExisting_WarnsSingleGapAndLeavesExistingUntouched()
    {
        var existing = new FakeVersionRow(1, 100, D(2020, 8, 1), D(2020, 12, 31));
        var newPeriod = new EP(D(2020, 1, 1), D(2020, 6, 30));

        var result = Editor.PlanUpsert([existing], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Equal(newPeriod, plan.Operations[0].Period);
        Assert.Single(plan.Warnings);
        Assert.Equal(new GapWarning(D(2020, 7, 1), D(2020, 7, 31)), plan.Warnings[0]);
    }

    [Fact]
    public void GapBothSides_NewPeriodBetweenTwoDisjointExisting_WarnsTwoGaps()
    {
        // D4 leap-year: 2020-02-29 exists -> existing1 fully covers February so the gap is computed from 03-01 (matches the case description).
        var existing1 = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 2, 29));
        var existing2 = new FakeVersionRow(2, 100, D(2020, 11, 1), D(2020, 12, 31));
        var newPeriod = new EP(D(2020, 5, 1), D(2020, 6, 30));

        var result = Editor.PlanUpsert([existing1, existing2], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Contains(new GapWarning(D(2020, 3, 1), D(2020, 4, 30)), plan.Warnings);
        Assert.Contains(new GapWarning(D(2020, 7, 1), D(2020, 10, 31)), plan.Warnings);
    }

    [Fact]
    public void NoGap_NewPeriodExactlyFillsBothSides_ProducesNoWarning()
    {
        var existing1 = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 4, 30));
        var existing2 = new FakeVersionRow(2, 100, D(2020, 8, 1), D(2020, 12, 31));
        var newPeriod = new EP(D(2020, 5, 1), D(2020, 7, 31));

        var result = Editor.PlanUpsert([existing1, existing2], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Empty(plan.Warnings);
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Equal(newPeriod, plan.Operations[0].Period);
    }

    [Fact]
    public void AsymmetricGap_AdjacentBeforeButGapAfter_WarnsOnlyTrailingGap()
    {
        var existing1 = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 4, 30));
        var existing2 = new FakeVersionRow(2, 100, D(2020, 10, 1), D(2020, 12, 31));
        var newPeriod = new EP(D(2020, 5, 1), D(2020, 6, 30));

        var result = Editor.PlanUpsert([existing1, existing2], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Single(plan.Warnings);
        Assert.Equal(new GapWarning(D(2020, 7, 1), D(2020, 9, 30)), plan.Warnings[0]);
    }

    [Fact]
    public void OpenEndNewPeriod_NoSubsequentVersions_ProducesNoAfterGapWarning()
    {
        var newPeriod = new EP(D(2020, 1, 1), EP.OpenEnd);

        var result = Editor.PlanUpsert([], newPeriod);

        Assert.False(result.IsError);
        var plan = result.Value;
        Assert.Empty(plan.Warnings);
        Assert.Single(plan.Operations);
        Assert.Equal(VersionOpKind.Insert, plan.Operations[0].Kind);
        Assert.Null(plan.Operations[0].SourceVersionId);
    }

    [Fact]
    public void InvalidRange_FromAfterTo_ReturnsValidationError()
    {
        var invalidPeriod = new AST.Core.EffectivePeriod.EffectivePeriod(D(2020, 12, 31), D(2020, 1, 1));

        var result = Editor.PlanUpsert([], invalidPeriod);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Validation, result.FirstError.Type);
    }

    // Spec 2026-08-22-orgunit-edit-close-code-reuse-shaping.md section 18.1. C abuts B. Shrinking C
    // produces a tail remnant [2024-01-01, 2025-12-31] that fills the whole span between the new period
    // and B, so the resulting coverage has NO hole and there must be no warning. Reading `untouched`
    // alone reports one, and org-unit turns that into a refusal to write a legal edit (GapIsBlocking).
    [Fact]
    public void OverlapCut_TailRemnantFillsTheSpanToTheNextVersion_ReportsNoGap()
    {
        var c = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2025, 12, 31));
        var b = new FakeVersionRow(2, 100, D(2026, 1, 1), EP.OpenEnd);
        var newPeriod = new EP(D(2020, 1, 1), D(2023, 12, 31));

        var result = Editor.PlanUpsert([c, b], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().BeEmpty();
    }

    // Spec 2026-08-22-orgunit-edit-close-code-reuse-shaping.md section 18.1, MIRROR of the tail case.
    // A is untouched and ends the day before C starts. Shrinking C from the front produces a head
    // remnant [2020-07-01, 2022-12-31] that fills the whole span between A and the new period, so the
    // resulting coverage has NO hole. Reading `untouched` alone sees only A and reports the span the
    // remnant is about to fill. The `before` branch has the same defect as `after`; brief 155 measured
    // one side, this measures the other.
    [Fact]
    public void OverlapCut_HeadRemnantAbutsTheNewPeriod_ReportsNoGap()
    {
        var a = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));
        var c = new FakeVersionRow(2, 100, D(2020, 7, 1), D(2025, 12, 31));
        var newPeriod = new EP(D(2023, 1, 1), D(2024, 12, 31));

        var result = Editor.PlanUpsert([a, c], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().BeEmpty();
    }

    // A DELIBERATE widening, pinned so it is visible in the suite rather than inferred from an absence.
    // The hole [2026-01-01, 2026-01-31] sits beyond the edited version C -- immediately BEFORE B, which
    // starts 2026-02-01 -- and existed before this edit; the edit neither
    // creates nor closes it. Today it is reported (with the wrong bounds) on every later edit, which
    // makes an org unit carrying such a hole permanently un-editable -- a Cancel or Delete can leave one
    // WITHOUT blocking, so the state is reachable.
    [Fact]
    public void PreExistingGapBeyondTheEditedVersion_IsNotReported()
    {
        var c = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2025, 12, 31));
        var b = new FakeVersionRow(2, 100, D(2026, 2, 1), EP.OpenEnd);
        var newPeriod = new EP(D(2020, 1, 1), D(2023, 12, 31));

        var result = Editor.PlanUpsert([c, b], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().BeEmpty();
    }

    // 148/F-92, spec section 19.5. The left gap is REAL and this edit touches its boundary; the right
    // side is an overlap-cut whose remnant fills what it cut. Exactly one warning, and it is the left.
    // This is the control that separates the right fix from "suppress the warning whenever any overlap
    // exists" -- a strictly worse defect, because that one hides real holes.
    // Covers ONE orientation only -- real gap BEFORE, overlap-cut AFTER. Its mirror is
    // RealGapOnTheAfterSide_WithAHeadRemnantOnTheBefore (Assurance Advisor 153/F-01).
    [Fact]
    public void RealGapOnOneSide_WithAnOverlapCutOnTheOther_WarnsExactlyTheRealGap()
    {
        var left = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));   // untouched
        var cut = new FakeVersionRow(2, 100, D(2021, 1, 1), D(2025, 12, 31));   // overlapped
        var next = new FakeVersionRow(3, 100, D(2026, 1, 1), EP.OpenEnd);       // abuts `cut`
        var newPeriod = new EP(D(2021, 1, 1), D(2023, 12, 31));

        var result = Editor.PlanUpsert([left, cut, next], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().ContainSingle()
            .Which.Should().Be(new GapWarning(D(2020, 7, 1), D(2020, 12, 31)));
    }

    // Case 8 (multi-span), the shape brief 156's report flagged as unmeasured. One newPeriod swallows
    // THREE existing versions at once: r1 sticks out to the left, r2 is entirely inside and therefore
    // leaves NO remnant, r3 sticks out to the right. r1's head remnant [2020-07-01, 2020-12-31] fills
    // the whole span between untouched A and the new period, so the resulting coverage has no hole.
    // Reading `untouched` alone sees only A and reports exactly the span the remnant is about to fill.
    // The swallowed middle row is what makes this case 8 rather than a second copy of the head case.
    [Fact]
    public void MultiSpanCut_HeadRemnantOfTheOutermostVersionFillsTheSpan_ReportsNoGap()
    {
        var a = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));    // untouched
        var r1 = new FakeVersionRow(2, 100, D(2020, 7, 1), D(2021, 12, 31));  // overlaps, sticks out left
        var r2 = new FakeVersionRow(3, 100, D(2022, 1, 1), D(2022, 12, 31));  // swallowed whole
        var r3 = new FakeVersionRow(4, 100, D(2023, 1, 1), D(2025, 12, 31));  // overlaps, sticks out right
        var newPeriod = new EP(D(2021, 1, 1), D(2024, 12, 31));

        var result = Editor.PlanUpsert([a, r1, r2, r3], newPeriod);

        result.IsError.Should().BeFalse();

        // Pins that this really is a multi-span cut: if a later edit to the fixture stopped swallowing
        // three rows, this test would quietly become a duplicate of the head-remnant case.
        result.Value.Operations.Count(o => o.Kind == VersionOpKind.SoftDeactivate).Should().Be(3);

        result.Value.Warnings.Should().BeEmpty();
    }

    // The BEFORE-side mirror of PreExistingGapBeyondTheEditedVersion_IsNotReported, and the other half
    // of the same deliberate widening. The hole [2020-01-01, 2020-12-31] sits between A and the edited
    // version C and existed before this edit; the edit neither creates nor closes it. Shrinking C from
    // the front leaves a head remnant [2021-01-01, 2022-12-31] that abuts the new period, so after the
    // fix the nearest coverage before newPeriod is that remnant and the older hole is not reported.
    // Pinned rather than inferred from an absence: the fix widens `before` and `after` symmetrically,
    // and only one of the two sides was pinned.
    [Fact]
    public void PreExistingGapBeforeTheEditedVersion_IsNotReported()
    {
        var a = new FakeVersionRow(1, 100, D(2019, 1, 1), D(2019, 12, 31));
        var c = new FakeVersionRow(2, 100, D(2021, 1, 1), D(2025, 12, 31));
        var newPeriod = new EP(D(2023, 1, 1), D(2024, 12, 31));

        var result = Editor.PlanUpsert([a, c], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().BeEmpty();
    }

    // Assurance Advisor AST-CONSULT-153 F-01. The MIRROR of RealGapOnOneSide_WithAnOverlapCutOnTheOther, and the
    // control that closes the last suppression hole. Here the overlap-cut is on the BEFORE side (C is
    // shrunk from the front, leaving a head remnant that abuts the new period) and the REAL gap is on
    // the AFTER side: nothing fills [2026-01-01, 2026-06-30] between the new period and `next`.
    // Without this, a mutation that suppressed warnings whenever a head remnant exists would pass all
    // six earlier controls while letting an org-unit save cross a genuine gap.
    [Fact]
    public void RealGapOnTheAfterSide_WithAHeadRemnantOnTheBefore_WarnsExactlyTheRealGap()
    {
        var a = new FakeVersionRow(1, 100, D(2020, 1, 1), D(2020, 6, 30));      // untouched
        var cut = new FakeVersionRow(2, 100, D(2020, 7, 1), D(2024, 12, 31));   // overlapped, sticks out left
        var next = new FakeVersionRow(3, 100, D(2026, 7, 1), EP.OpenEnd);       // untouched, a REAL gap away
        var newPeriod = new EP(D(2023, 1, 1), D(2025, 12, 31));

        var result = Editor.PlanUpsert([a, cut, next], newPeriod);

        result.IsError.Should().BeFalse();
        result.Value.Warnings.Should().ContainSingle()
            .Which.Should().Be(new GapWarning(D(2026, 1, 1), D(2026, 6, 30)));
    }
}
