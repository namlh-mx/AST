using AST.Core.EffectivePeriod;
using AST.Core.Tests.TestSupport;
using ErrorOr;
using static AST.Core.Tests.TestSupport.Dates;

namespace AST.Core.Tests.EffectivePeriod;

public class EffectivePeriodResolverTests
{
    private static readonly IEffectivePeriodResolver Resolver = new EffectivePeriodResolver();

    [Fact]
    public void ResolveAt_HasCoverage_ReturnsMatchingVersion()
    {
        var candidates = new List<FakeVersionRow>
        {
            new(1, 100, D(2020, 1, 1), D(2020, 6, 30)),
            new(2, 100, D(2020, 7, 1), D(2020, 12, 31)),
        };

        var result = Resolver.ResolveAt<FakeVersionRow>(candidates, D(2020, 8, 15));

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Id);
    }

    [Fact]
    public void ResolveAt_NoCoverage_ReturnsNotFound()
    {
        var candidates = new List<FakeVersionRow>
        {
            new(1, 100, D(2020, 1, 1), D(2020, 6, 30)),
        };

        var result = Resolver.ResolveAt<FakeVersionRow>(candidates, D(2021, 1, 1));

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
        Assert.Equal("EffectivePeriod.NoCoverage", result.FirstError.Code);
    }

    // D6 (no 2 active versions of one identity with intersecting periods) cannot be enforced by MySQL, so the read
    // path is its last line of defence. Picking one silently would make permission/parameter resolution
    // non-deterministic and undebuggable -- D9 says STOP + report clearly, never fall back to a default.
    [Fact]
    public void ResolveAt_TwoOverlappingActiveVersions_ReturnsConflict()
    {
        var candidates = new List<FakeVersionRow>
        {
            new(1, 100, D(2020, 1, 1), D(2020, 12, 31)),
            new(2, 100, D(2020, 6, 1), D(2021, 6, 30)),
        };

        var result = Resolver.ResolveAt<FakeVersionRow>(candidates, D(2020, 8, 15));

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
        Assert.Equal("EffectivePeriod.OverlappingVersions", result.FirstError.Code);
    }

    // An INACTIVE overlapping row is not a violation -- that is exactly what soft-deleted history looks like.
    [Fact]
    public void ResolveAt_OverlapWithInactiveVersion_ResolvesNormally()
    {
        var candidates = new List<FakeVersionRow>
        {
            new(1, 100, D(2020, 1, 1), D(2020, 12, 31), IsActive: false),
            new(2, 100, D(2020, 6, 1), D(2021, 6, 30)),
        };

        var result = Resolver.ResolveAt<FakeVersionRow>(candidates, D(2020, 8, 15));

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Id);
    }

    [Fact]
    public void ResolveAt_IgnoresInactiveCandidate_EvenIfInPeriod()
    {
        var candidates = new List<FakeVersionRow>
        {
            new(1, 100, D(2020, 1, 1), D(2020, 12, 31), IsActive: false),
        };

        var result = Resolver.ResolveAt<FakeVersionRow>(candidates, D(2020, 6, 1));

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
    }
}
