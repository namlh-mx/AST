using AST.Core.Iam;

namespace AST.Shell.Navigation;

public sealed class SidebarMenuBuilder : ISidebarMenuBuilder
{
    private readonly IFunctionRegistry _functionRegistry;
    private readonly IMenuGroupCatalog _groupCatalog;

    public SidebarMenuBuilder(IFunctionRegistry functionRegistry, IMenuGroupCatalog groupCatalog)
    {
        _functionRegistry = functionRegistry;
        _groupCatalog = groupCatalog;
    }

    public IReadOnlyList<SidebarNode> BuildMain()
    {
        var groups = new List<SidebarNode>(_groupCatalog.Groups.Count);

        foreach (var group in _groupCatalog.Groups)
        {
            var realLeaves = _functionRegistry.All
                .Where(f => f.MenuGroupCode == group.Code)
                .Select(f => (Node: new SidebarNode(
                    $"{f.BusinessCode} - {f.DisplayName}",
                    f.Icon,
                    f.NavTarget,
                    []), Order: f.Order, IsReal: true));

            var placeholderLeaves = PlaceholderMenuSeed.ForGroup(group.Order)
                .Select(p => (Node: new SidebarNode(
                    p.Title,
                    null,
                    "ComingSoonView",
                    []), Order: p.Order, IsReal: false));

            var leaves = realLeaves
                .Concat(placeholderLeaves)
                .OrderBy(x => x.Order)
                .ThenByDescending(x => x.IsReal)
                .Select(x => x.Node)
                .ToArray();

            groups.Add(new SidebarNode(group.DisplayName, group.IconKey, null, leaves));
        }

        return groups;
    }

    public IReadOnlyList<SidebarNode> BuildFooter() =>
    [
        new("Trợ giúp", "QuestionCircle24", "HelpView", []),
        new("Cấu hình", "Settings24", "ConfigurationStationView", [])
        {
            OwnedViewNames = ConfigurationStationTargets.All,
        },
    ];
}
