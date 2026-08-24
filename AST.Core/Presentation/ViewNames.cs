namespace AST.Core.Presentation;

// Neutral home for a view name literal referenced from more than one registry that must not
// couple to each other (composition-root FunctionDescriptor registration vs. AST.Shell's
// sidebar/hub navigation constants) -- 2026-08-05 rejected pointing the composition
// root at AST.Shell.Navigation.ConfigurationStationTargets for exactly this reason. Add an entry
// here only when a view name is genuinely shared across such registries; a name used by a single
// consumer does not need one.
public static class ViewNames
{
    public const string Dashboard = "DashboardView";
    public const string OrgUnitDeclaration = "OrgUnitDeclarationView";
    public const string RoleDeclaration = "RoleDeclarationView";
}
