using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;

namespace AST.Modules.IAM.Tests.Integration;

// N2: AstOrgUnitPicker eligibility — candidate parents whose ACTIVE versions continuously cover the whole
// child effective-period. Exercises the real composition (QueryInScopeAsync + IParentCoverageProvider +
// CoverageGap) against real MySQL, not just the pure algebra (already covered by CoverageGapTests).
public sealed class OrgUnitEligibleParentsTests : IamRepositoryTestBase
{
    [Fact]
    public async Task CandidateCoveringWholeOpenEndedChildPeriod_IsEligible()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "PAR", "Đơn vị cha", "Cha", null, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));

        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd);
        var eligible = await OrgUnits.GetEligibleParentsAsync(new DataScope(ScopeLevel.Global, null, "tester"), childPeriod);

        Assert.Contains(eligible, e => e.Id == parent);
    }

    [Fact]
    public async Task CandidateEndingBeforeAnOpenEndedChildPeriod_IsNotEligible()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "END", "Đơn vị hết hạn", "Hết hạn", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd);
        var eligible = await OrgUnits.GetEligibleParentsAsync(new DataScope(ScopeLevel.Global, null, "tester"), childPeriod);

        Assert.DoesNotContain(eligible, e => e.Id == parent);
    }

    [Fact]
    public async Task CandidateNotYetEffectiveAtChildPeriodStart_IsNotEligible()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "LATE", "Đơn vị muộn", "Muộn", null,
            new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd));

        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd);
        var eligible = await OrgUnits.GetEligibleParentsAsync(new DataScope(ScopeLevel.Global, null, "tester"), childPeriod);

        Assert.DoesNotContain(eligible, e => e.Id == parent);
    }

    [Fact]
    public async Task EligibleCandidate_DisplayIsCodeDashShortName()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "DISP", "Đơn vị hiển thị đầy đủ", "Hiển thị", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));

        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd);
        var eligible = await OrgUnits.GetEligibleParentsAsync(new DataScope(ScopeLevel.Global, null, "tester"), childPeriod);

        var item = Assert.Single(eligible, e => e.Id == parent);
        Assert.Equal("DISP — Hiển thị", item.Display);
    }

    // review finding: the candidate-universe argument rests on QueryInScopeAsync's own scope narrowing —
    // confirm a candidate that would otherwise be eligible is excluded once it falls outside the DataScope.
    [Fact]
    public async Task CandidateCoveringThePeriod_ButOutsideDataScope_IsNotEligible()
    {
        SkipUnlessDbAvailable();

        var inScope = await CreateOrgUnitAsync(
            "INSC", "Trong phạm vi", "Trong PV", null, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));
        var outOfScope = await CreateOrgUnitAsync(
            "OUTSC", "Ngoài phạm vi", "Ngoài PV", null, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd));

        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd);
        var scope = new DataScope(ScopeLevel.OwnOrgUnit, inScope, "tester");
        var eligible = await OrgUnits.GetEligibleParentsAsync(scope, childPeriod);

        Assert.Contains(eligible, e => e.Id == inScope);
        Assert.DoesNotContain(eligible, e => e.Id == outOfScope);
    }

    // review finding: exercise the actual reason CoverageGap is reused here — TWO adjacent active versions
    // with NO gap between them must together satisfy N2, not just a single version.
    [Fact]
    public async Task CandidateWithTwoAdjacentActiveVersionsAndNoGap_TogetherCoverTheChildPeriod_IsEligible()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "MULTI", "Đơn vị nhiều phiên bản", "Nhiều PB", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30)));

        // Extend forward with a second, contiguous (no-gap) active version on the SAME identity (G4: a future
        // phase on a living unit is an Edit, not Close+Add) — 2020-06-30 -> 2020-07-01 has no day in between.
        var second = await OrgUnits.UpsertAsync(
            parent, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "MULTI", "Đơn vị nhiều phiên bản", "Nhiều PB", null, VersionOperationKind.Edit, "tester", "extend forward");
        Assert.False(second.IsError, string.Join("; ", second.IsError ? second.Errors.Select(e => e.Description) : []));

        // Child period spans BOTH parent versions -- no single version covers it alone.
        var childPeriod = new EffectivePeriod(new DateOnly(2020, 3, 1), EffectivePeriod.OpenEnd);
        var eligible = await OrgUnits.GetEligibleParentsAsync(new DataScope(ScopeLevel.Global, null, "tester"), childPeriod);

        Assert.Contains(eligible, e => e.Id == parent);
    }
}
