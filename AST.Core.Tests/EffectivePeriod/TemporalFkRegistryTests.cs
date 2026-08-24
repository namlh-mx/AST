using AST.Core.EffectivePeriod;

namespace AST.Core.Tests.EffectivePeriod;

// Tests the registry itself -- lookup behaviour only. The edges are synthetic on purpose: AST.Core knows no
// module's schema, so asserting IAM's real edge set belongs to the IAM module's own tests
// (AST.Modules.IAM.Tests/IamTemporalFkEdgesTests).
public class TemporalFkRegistryTests
{
    private static readonly TemporalFkEdge ChildToParent = new("child_version", "parent_id", "parent_version", "parent_id");
    private static readonly TemporalFkEdge ChildToOther = new("child_version", "other_id", "other_version", "other_id");
    private static readonly TemporalFkEdge SecondChildToParent = new("second_child_version", "parent_id", "parent_version", "parent_id");

    [Fact]
    public void EdgesForChild_ReturnsEveryEdgeDeclaredForThatChild()
    {
        ITemporalFkRegistry registry = new TemporalFkRegistry([ChildToParent, ChildToOther, SecondChildToParent]);

        var edges = registry.EdgesForChild("child_version");

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.ChildParentColumn == "parent_id" && e.ParentVersionTable == "parent_version");
        Assert.Contains(edges, e => e.ChildParentColumn == "other_id" && e.ParentVersionTable == "other_version");
    }

    [Fact]
    public void EdgesForParent_ReturnsReverseLookup()
    {
        ITemporalFkRegistry registry = new TemporalFkRegistry([ChildToParent, ChildToOther, SecondChildToParent]);

        var edges = registry.EdgesForParent("parent_version");

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.ChildVersionTable == "child_version");
        Assert.Contains(edges, e => e.ChildVersionTable == "second_child_version");
    }

    [Fact]
    public void EdgesForChild_UnknownTable_ReturnsEmpty() =>
        Assert.Empty(new TemporalFkRegistry([ChildToParent]).EdgesForChild("not_declared_version"));

    [Fact]
    public void Register_AddsAnEdgeAfterConstruction()
    {
        var registry = new TemporalFkRegistry([ChildToParent]);

        registry.Register(ChildToOther);

        Assert.Equal(2, registry.EdgesForChild("child_version").Count);
    }

    // An empty registry makes TemporalFkValidator return Success for everything, silently disabling the STRICT
    // temporal-FK invariant. It must be impossible to reach quietly -- including via a container auto-wiring
    // IEnumerable<TemporalFkEdge> to an empty collection.
    [Fact]
    public void Ctor_NoEdges_ThrowsRatherThanValidatingNothing()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TemporalFkRegistry([]));

        Assert.Equal("edges", ex.ParamName);
    }
}
