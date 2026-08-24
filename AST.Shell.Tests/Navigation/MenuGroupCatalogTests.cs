using AST.Shell.Navigation;
using FluentAssertions;

namespace AST.Shell.Tests.Navigation;

// Fix round 2 R5.2 — placeholder titles are "Menu {groupOrder}.{n}". Duplicate Orders would make two leaves
// share a title and TargetViewName (ComingSoonView), and ResolveLeaf must then light nothing. Enforce the
// data-side half of that fact so a careless catalog edit fails here instead of painting the wrong leaf.
public sealed class MenuGroupCatalogTests
{
    [Fact]
    public void Groups_Orders_are_distinct()
    {
        var orders = new MenuGroupCatalog().Groups.Select(g => g.Order).ToArray();

        orders.Should().OnlyHaveUniqueItems();
    }
}
