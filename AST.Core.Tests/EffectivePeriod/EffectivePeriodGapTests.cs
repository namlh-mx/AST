using AST.Core.EffectivePeriod;
using EP = AST.Core.EffectivePeriod.EffectivePeriod;
using static AST.Core.Tests.TestSupport.Dates;

namespace AST.Core.Tests.EffectivePeriod;

// Direct contract of the shared gap primitive (D7): the uncovered span between two adjacent periods, or none
// when they touch/overlap or a boundary is open. Its behaviour is also exercised indirectly by PeriodEditorTests
// and CoverageReductionTests; this pins it at its own home for any future caller.
public class EffectivePeriodGapTests
{
    [Fact]
    public void One_day_gap_is_reported()
        => Assert.Equal(new GapWarning(D(2026, 1, 11), D(2026, 1, 11)), EP.GapBetween(D(2026, 1, 10), D(2026, 1, 12)));

    [Fact]
    public void Multi_day_gap_is_reported()
        => Assert.Equal(new GapWarning(D(2026, 1, 11), D(2026, 1, 19)), EP.GapBetween(D(2026, 1, 10), D(2026, 1, 20)));

    [Fact]
    public void Adjacent_periods_have_no_gap()
        => Assert.Null(EP.GapBetween(D(2026, 1, 10), D(2026, 1, 11)));

    [Fact]
    public void Overlapping_periods_have_no_gap()
        => Assert.Null(EP.GapBetween(D(2026, 1, 10), D(2026, 1, 5)));

    [Fact]
    public void Open_end_on_the_left_yields_no_gap()
        => Assert.Null(EP.GapBetween(EP.OpenEnd, D(2026, 1, 20)));

    [Fact]
    public void Min_value_on_the_right_yields_no_gap()
        => Assert.Null(EP.GapBetween(D(2026, 1, 10), DateOnly.MinValue));
}
