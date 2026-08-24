using AST.Core.EffectivePeriod;
using AST.Modules.IAM.Data;

namespace AST.Modules.IAM.Tests.Data;

// The 5 IAM temporal-FK edges (§1.6 docs/design-iam-schema.md). Moved here from AST.Core.Tests when the edges
// themselves moved into the module: the SharedKernel neither declares nor asserts a module's schema
// (rule-module-boundary §1). Guards against an edge being dropped -- losing one silently disables that FK's
// temporal validation.
public class IamTemporalFkEdgesTests
{
    [Fact]
    public void Registry_DeclaresTheFiveIamEdges()
    {
        ITemporalFkRegistry registry = IamTemporalFkEdges.CreateRegistry();

        // Asserted explicitly: the per-child counts below would not notice a 6th edge on a NEW child table.
        Assert.Equal(5, IamTemporalFkEdges.All.Count);

        Assert.Equal(2, registry.EdgesForChild("user_version").Count);
        Assert.Contains(registry.EdgesForChild("user_version"), e => e.ChildParentColumn == "org_unit_id" && e.ParentVersionTable == "org_unit_version");
        Assert.Contains(registry.EdgesForChild("user_version"), e => e.ChildParentColumn == "role_id" && e.ParentVersionTable == "role_version");

        Assert.Equal(2, registry.EdgesForChild("role_permission_version").Count);
        Assert.Contains(registry.EdgesForChild("role_permission_version"), e => e.ChildParentColumn == "role_id" && e.ParentVersionTable == "role_version");
        Assert.Contains(registry.EdgesForChild("role_permission_version"), e => e.ChildParentColumn == "function_id" && e.ParentVersionTable == "function_version");

        var orgSelfRef = Assert.Single(registry.EdgesForChild("org_unit_version"));
        Assert.Equal("parent_id", orgSelfRef.ChildParentColumn);
        Assert.Equal("org_unit_version", orgSelfRef.ParentVersionTable);
        Assert.Equal("org_unit_id", orgSelfRef.ParentIdentityColumn);
    }

    [Fact]
    public void EdgesForParent_ReturnsReverseLookup()
    {
        ITemporalFkRegistry registry = IamTemporalFkEdges.CreateRegistry();

        var edges = registry.EdgesForParent("role_version");

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.ChildVersionTable == "user_version");
        Assert.Contains(edges, e => e.ChildVersionTable == "role_permission_version");
    }
}
