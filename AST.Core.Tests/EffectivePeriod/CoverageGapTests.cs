using AST.Core.EffectivePeriod;
using EP = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Core.Tests.EffectivePeriod;

// Extracted from TemporalFkValidator's own gap-detection algebra (D8/N2): does `coverage` continuously cover
// `target` with no gap? Reused by both temporal-FK validation (Save-time) and the org-unit-picker eligibility
// read (N2, browse-time) so the two never diverge on the definition of "continuous coverage".
public class CoverageGapTests
{
    private static readonly DateOnly D2020 = new(2020, 1, 1);
    private static readonly DateOnly D2020Mid = new(2020, 6, 1);
    private static readonly DateOnly D2020End = new(2020, 12, 31);

    [Fact]
    public void SinglePeriodFullyCoveringTarget_NoGap()
    {
        var coverage = new[] { new EP(D2020, EP.OpenEnd) };
        var target = new EP(D2020Mid, D2020End);

        Assert.False(CoverageGap.TryFind(coverage, target, out _));
    }

    [Fact]
    public void TwoAdjacentPeriodsWithNoGap_FullyCoverTarget()
    {
        var coverage = new[]
        {
            new EP(D2020, new DateOnly(2020, 6, 30)),
            new EP(D2020Mid.AddDays(29), EP.OpenEnd), // 2020-06-30 -> 2020-07-01 is contiguous
        };
        var target = new EP(D2020, D2020End);

        Assert.False(CoverageGap.TryFind(coverage, target, out _));
    }

    [Fact]
    public void GapInTheMiddle_ReturnsTheUncoveredRange()
    {
        var coverage = new[]
        {
            new EP(D2020, new DateOnly(2020, 3, 31)),
            new EP(new DateOnly(2020, 6, 1), EP.OpenEnd),
        };
        var target = new EP(D2020, D2020End);

        Assert.True(CoverageGap.TryFind(coverage, target, out var gap));
        Assert.Equal(new DateOnly(2020, 4, 1), gap.From);
        Assert.Equal(new DateOnly(2020, 5, 31), gap.To);
    }

    [Fact]
    public void CoverageEndsBeforeTargetEnd_ReturnsTrailingGap()
    {
        var coverage = new[] { new EP(D2020, D2020End) };
        var target = new EP(D2020, EP.OpenEnd);

        Assert.True(CoverageGap.TryFind(coverage, target, out var gap));
        Assert.Equal(new DateOnly(2021, 1, 1), gap.From);
        Assert.Equal(EP.OpenEnd, gap.To);
    }

    [Fact]
    public void EmptyCoverage_ReturnsTheWholeTargetAsTheGap()
    {
        var target = new EP(D2020, D2020End);

        Assert.True(CoverageGap.TryFind([], target, out var gap));
        Assert.Equal(target, gap);
    }

    [Fact]
    public void CoverageStartsAfterTargetStart_ReturnsLeadingGap()
    {
        var coverage = new[] { new EP(D2020Mid, EP.OpenEnd) };
        var target = new EP(D2020, EP.OpenEnd);

        Assert.True(CoverageGap.TryFind(coverage, target, out var gap));
        Assert.Equal(D2020, gap.From);
        Assert.Equal(new DateOnly(2020, 5, 31), gap.To);
    }
}
