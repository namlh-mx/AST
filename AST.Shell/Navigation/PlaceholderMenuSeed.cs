namespace AST.Shell.Navigation;

internal static class PlaceholderMenuSeed
{
    public static IReadOnlyList<(string Title, int Order)> ForGroup(int groupOrder) =>
    [
        ($"Menu {groupOrder}.1", 1),
        ($"Menu {groupOrder}.2", 2),
        ($"Menu {groupOrder}.3", 3),
    ];
}
