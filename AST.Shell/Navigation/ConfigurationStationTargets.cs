namespace AST.Shell.Navigation;

// The screens reachable ONLY from Trạm cấu hình's Execute cards. One home, two consumers:
// ConfigurationStationViewModel navigates to them, and the sidebar's Cấu hình leaf owns them so it
// stays lit while one is shown. Adding a card here means adding it in both roles at once.
public static class ConfigurationStationTargets
{
    public const string AdminAuth = "AdminAuthView";
    public const string ConnectionDeclaration = "ConnectionDeclarationView";
    public const string OrgUnitDeclaration = "OrgUnitDeclarationView";
    public const string RoleDeclaration = "RoleDeclarationView";

    public static IReadOnlyList<string> All { get; } =
        [AdminAuth, ConnectionDeclaration, OrgUnitDeclaration, RoleDeclaration];
}
