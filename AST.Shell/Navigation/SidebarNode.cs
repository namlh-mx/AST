namespace AST.Shell.Navigation;

// A projection of the menu tree for the Shell to render (the exe maps each node to a WPF-UI NavigationViewItem).
// DERIVED — the source of truth is IFunctionRegistry (real functions) + MenuGroupCatalog + the placeholder seed,
// NOT this record. BCL-only so it can be built + tested headless.
//   Group node : TargetViewName == null, Children populated.
//   Leaf node  : TargetViewName set (Prism view name), Children empty.
public sealed record SidebarNode(
    string Title,
    string? IconKey,
    string? TargetViewName,
    IReadOnlyList<SidebarNode> Children)
{
    // Views this leaf owns without them being leaves themselves: screens reachable only through this one
    // (a hub's child screens). The sidebar keeps this leaf lit while one of them is shown. Empty for all
    // ordinary leaves.
    public IReadOnlyList<string> OwnedViewNames { get; init; } = [];
}
