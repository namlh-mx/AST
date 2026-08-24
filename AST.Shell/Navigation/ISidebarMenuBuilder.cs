namespace AST.Shell.Navigation;

// Builds the sidebar menu tree (2 levels: L1 group -> L2 leaf) that the Shell renders.
// Source of truth = IFunctionRegistry (real functions, grouped by MenuGroupCode) + the placeholder seed (transitional
// UI-only leaves) + MenuGroupCatalog (group metadata). Placeholders are NEVER written into IFunctionRegistry.
public interface ISidebarMenuBuilder
{
    // Main navigation: 5 L1 business groups, each with its leaves (real functions + placeholders), ordered.
    IReadOnlyList<SidebarNode> BuildMain();

    // Footer: "Trợ giúp" (Help leaf) above "Cấu hình" (group: Admin auth + DB connection leaves).
    IReadOnlyList<SidebarNode> BuildFooter();
}
