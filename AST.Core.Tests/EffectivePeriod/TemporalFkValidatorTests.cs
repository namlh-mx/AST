using AST.Core.EffectivePeriod;
using AST.Core.Tests.TestSupport;
using ErrorOr;
using EP = AST.Core.EffectivePeriod.EffectivePeriod;
using static AST.Core.Tests.TestSupport.Dates;

namespace AST.Core.Tests.EffectivePeriod;

public class TemporalFkValidatorTests
{
    // The validator's own fixture. These mirror IAM's real edge set only so the existing test bodies keep their
    // table names; they are declared HERE deliberately, so AST.Core.Tests does not depend on a module's schema
    // (rule-module-boundary §1). IAM's real set is asserted by AST.Modules.IAM.Tests/IamTemporalFkEdgesTests.
    private static readonly TemporalFkEdge[] Edges =
    [
        new("user_version", "org_unit_id", "org_unit_version", "org_unit_id"),
        new("user_version", "role_id", "role_version", "role_id"),
        new("role_permission_version", "role_id", "role_version", "role_id"),
        new("role_permission_version", "function_id", "function_version", "function_id"),
        new("org_unit_version", "parent_id", "org_unit_version", "org_unit_id"),
    ];

    private static (TemporalFkValidator Validator, InMemoryParentCoverageProvider Parents, InMemoryDependentCoverageProvider Dependents) Build()
    {
        var registry = new TemporalFkRegistry(Edges);
        var parents = new InMemoryParentCoverageProvider();
        var dependents = new InMemoryDependentCoverageProvider();
        var validator = new TemporalFkValidator(registry, parents, dependents);
        return (validator, parents, dependents);
    }

    [Fact]
    public void ValidateChildCoverage_ContinuousCoverageAcrossMultipleParentVersions_Ok()
    {
        var (validator, parents, _) = Build();
        // 2 consecutive parent periods with no gap fully cover the whole child period.
        parents.Add("org_unit_version", 10, new EP(D(2019, 1, 1), D(2020, 6, 30)));
        parents.Add("org_unit_version", 10, new EP(D(2020, 7, 1), EP.OpenEnd));
        parents.Add("role_version", 20, new EP(D(2019, 1, 1), EP.OpenEnd));

        var childPeriod = new EP(D(2020, 1, 1), D(2020, 12, 31));
        var result = validator.ValidateChildCoverage(
            "user_version",
            new Dictionary<string, long> { ["org_unit_id"] = 10, ["role_id"] = 20 },
            childPeriod,
            null);

        Assert.False(result.IsError);
    }

    [Fact]
    public void ValidateChildCoverage_GapInParentCoverage_ReturnsParentGap()
    {
        var (validator, parents, _) = Build();
        // Gap between 2020-04-01 and 2020-05-31 (missing coverage).
        parents.Add("org_unit_version", 10, new EP(D(2019, 1, 1), D(2020, 3, 31)));
        parents.Add("org_unit_version", 10, new EP(D(2020, 6, 1), EP.OpenEnd));
        parents.Add("role_version", 20, new EP(D(2019, 1, 1), EP.OpenEnd));

        var childPeriod = new EP(D(2020, 1, 1), D(2020, 12, 31));
        var result = validator.ValidateChildCoverage(
            "user_version",
            new Dictionary<string, long> { ["org_unit_id"] = 10, ["role_id"] = 20 },
            childPeriod,
            null);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Failure, result.FirstError.Type);
        Assert.Equal("TemporalFk.ParentGap", result.FirstError.Code);
    }

    [Fact]
    public void ValidateChildCoverage_MissingParentColumn_SkipsThatEdge_TreatedAsExempt()
    {
        var (validator, parents, _) = Build();
        parents.Add("role_version", 20, new EP(D(2019, 1, 1), EP.OpenEnd));
        // org_unit_id is NOT provided -> [Q1] exempts the org_unit edge from checking (simulating a root org unit).

        var childPeriod = new EP(D(2020, 1, 1), D(2020, 12, 31));
        var result = validator.ValidateChildCoverage(
            "org_unit_version",
            new Dictionary<string, long>(),
            childPeriod,
            null);

        Assert.False(result.IsError);
    }

    [Fact]
    public void ValidateParentChange_DependentsRemainCovered_Ok()
    {
        var (validator, _, dependents) = Build();
        var edge = new TemporalFkEdge("user_version", "org_unit_id", "org_unit_version", "org_unit_id");
        dependents.Add(edge, 10, new EP(D(2020, 3, 1), D(2020, 9, 30)));

        var remainingCoverage = new List<EP> { new(D(2020, 1, 1), EP.OpenEnd) };
        var result = validator.ValidateParentChange("org_unit_version", 10, remainingCoverage, null);

        Assert.False(result.IsError);
    }

    [Fact]
    public void ValidateParentChange_ShrinkingCoverageLeavesDependentUncovered_ReturnsDependentsUncovered()
    {
        var (validator, _, dependents) = Build();
        var edge = new TemporalFkEdge("user_version", "org_unit_id", "org_unit_version", "org_unit_id");
        dependents.Add(edge, 10, new EP(D(2020, 3, 1), D(2020, 9, 30)));

        // Shrinks the parent period down to 2020-06-30 -> the child [03-01,09-30] is no longer fully covered.
        var remainingCoverage = new List<EP> { new(D(2020, 1, 1), D(2020, 6, 30)) };
        var result = validator.ValidateParentChange("org_unit_version", 10, remainingCoverage, null);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Failure, result.FirstError.Type);
        Assert.Equal("TemporalFk.DependentsUncovered", result.FirstError.Code);
    }

    [Fact]
    public void ValidateParentChange_MultiSpanCoverage_DependentInSecondSpan_Ok()
    {
        var (validator, _, dependents) = Build();
        var edge = new TemporalFkEdge("user_version", "org_unit_id", "org_unit_version", "org_unit_id");
        dependents.Add(edge, 10, new EP(D(2020, 7, 1), D(2020, 8, 31)));

        var remainingCoverage = new List<EP>
        {
            new(D(2019, 1, 1), D(2020, 3, 31)),
            new(D(2020, 6, 1), EP.OpenEnd),
        };
        var result = validator.ValidateParentChange("org_unit_version", 10, remainingCoverage, null);

        Assert.False(result.IsError);
    }

    [Fact]
    public void ValidateParentChange_MultiSpanCoverage_DependentFallsInGapBetweenSpans_ReturnsDependentsUncovered()
    {
        var (validator, _, dependents) = Build();
        var edge = new TemporalFkEdge("user_version", "org_unit_id", "org_unit_version", "org_unit_id");
        dependents.Add(edge, 10, new EP(D(2020, 4, 1), D(2020, 5, 31)));

        var remainingCoverage = new List<EP>
        {
            new(D(2019, 1, 1), D(2020, 3, 31)),
            new(D(2020, 6, 1), EP.OpenEnd),
        };
        var result = validator.ValidateParentChange("org_unit_version", 10, remainingCoverage, null);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Failure, result.FirstError.Type);
        Assert.Equal("TemporalFk.DependentsUncovered", result.FirstError.Code);
    }
}
