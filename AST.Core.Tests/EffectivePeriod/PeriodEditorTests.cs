using AST.Core.EffectivePeriod;
using AST.Core.Tests.TestSupport;
using ErrorOr;
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
}
