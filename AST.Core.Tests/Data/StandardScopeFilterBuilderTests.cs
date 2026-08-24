using AST.Core.Data;
using AST.Core.Iam;
using static AST.Core.Tests.TestSupport.Dates;

namespace AST.Core.Tests.Data;

public class StandardScopeFilterBuilderTests
{
    private static readonly IStandardScopeFilterBuilder Builder = new StandardScopeFilterBuilder();

    [Fact]
    public void Build_Self_FiltersByOwnerColumnAndUsername()
    {
        var scope = new DataScope(ScopeLevel.Self, null, "nguyen.van.a");
        var filter = Builder.Build(scope, D(2020, 6, 1), D(2020, 6, 15), "t", "t.org_unit_id", "t.recorded_by");

        Assert.Contains("t.isactive = 1", filter.WhereSql);
        Assert.Contains("t.effective_from <= @asOf AND @asOf <= t.effective_to", filter.WhereSql);
        Assert.Contains("t.recorded_by = @currentUsername", filter.WhereSql);
        Assert.Equal(D(2020, 6, 1), filter.Parameters["@asOf"]);
        Assert.Equal("nguyen.van.a", filter.Parameters["@currentUsername"]);
        Assert.False(filter.Parameters.ContainsKey("@rootOrgUnitId"));
    }

    [Fact]
    public void Build_OwnOrgUnit_FiltersByExactOrgUnitId()
    {
        var scope = new DataScope(ScopeLevel.OwnOrgUnit, 42, "nguyen.van.a");
        var filter = Builder.Build(scope, D(2020, 6, 1), D(2020, 6, 15), "t", "t.org_unit_id", "t.recorded_by");

        Assert.Contains("t.org_unit_id = @rootOrgUnitId", filter.WhereSql);
        Assert.Equal(42L, filter.Parameters["@rootOrgUnitId"]);
        Assert.False(filter.Parameters.ContainsKey("@today"));
    }

    [Fact]
    public void Build_OwnOrgUnitAndDescendants_EmitsSubtreeCteWithRootAndToday()
    {
        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, 42, "nguyen.van.a");
        var filter = Builder.Build(scope, D(2020, 6, 1), D(2020, 6, 15), "t", "t.org_unit_id", "t.recorded_by");

        Assert.Contains("t.org_unit_id IN (", filter.WhereSql);
        Assert.Contains("WITH RECURSIVE", filter.WhereSql);
        Assert.Contains("today_ou", filter.WhereSql);
        Assert.Contains("subtree", filter.WhereSql);
        Assert.Equal(42L, filter.Parameters["@rootOrgUnitId"]);
        Assert.Equal(D(2020, 6, 15), filter.Parameters["@today"]);
    }

    [Fact]
    public void Build_Global_AddsNoOrgUnitCondition()
    {
        var scope = new DataScope(ScopeLevel.Global, null, "nguyen.van.a");
        var filter = Builder.Build(scope, D(2020, 6, 1), D(2020, 6, 15), "t", "t.org_unit_id", "t.recorded_by");

        Assert.DoesNotContain("org_unit_id", filter.WhereSql);
        Assert.Contains("t.isactive = 1", filter.WhereSql);
        Assert.False(filter.Parameters.ContainsKey("@rootOrgUnitId"));
        Assert.False(filter.Parameters.ContainsKey("@currentUsername"));
    }
}
