using AST;
using AST.Core.Startup;
using AST.Shell.Navigation;
using AST.Shell.ViewModels;
using FluentAssertions;
using Moq;
using Prism.Navigation.Regions;

namespace AST.App.Tests.ViewModels;

// RED tests written BEFORE the implementation (ViewModel work is
// gated by pre-written tests). They pin the ONE subtle part of the sidebar-highlight rebuild: mapping "which
// view is shown" back to "which sidebar leaf should be lit".
//
// Why the mapping is not a plain name lookup: every placeholder leaf in every group navigates to the SAME view
// (ComingSoonView), so a view name alone does not identify a leaf. The navigation parameters carry the leaf
// title (ComingSoonViewModel.TitleParameterKey), which disambiguates. When it cannot disambiguate, the
// contract is to light NOTHING -- a blank sidebar is honest, a wrong leaf is a lie.
public sealed class MainWindowViewModelResolveLeafTests
{
    // A controlled 2-group menu with two placeholders that share ComingSoonView, plus a footer -- the exact
    // shape SidebarMenuBuilder produces, minus the parts that need a real IFunctionRegistry.
    private static readonly SidebarNode RealLeaf = new("R1 - Khai báo đơn vị", null, "RealOneView", []);
    private static readonly SidebarNode PlaceholderG1 = new("Menu 1.1", null, "ComingSoonView", []);
    private static readonly SidebarNode PlaceholderG2 = new("Menu 2.1", null, "ComingSoonView", []);
    private static readonly SidebarNode PlaceholderG2B = new("Menu 2.2", null, "ComingSoonView", []);

    // Two leaves that collide on BOTH target and title. Production cannot produce this while every MenuGroup.Order
    // is distinct (placeholder titles are "Menu {groupOrder}.{n}"), but MenuGroupCatalog.Groups is a hand-kept list
    // and nothing enforces that. The resolver must not depend on the catalog being well-formed.
    private static readonly SidebarNode DuplicateA = new("Menu 9.1", null, "DupView", []);
    private static readonly SidebarNode DuplicateB = new("Menu 9.1", null, "DupView", []);
    private static readonly SidebarNode HelpLeaf = new("Trợ giúp", "QuestionCircle24", "HelpView", []);
    // The footer hub: screens reached only from inside Trạm cấu hình are not sidebar leaves of their own, so the
    // hub declares that it owns them and stays lit while one is shown (fix round 1).
    private static readonly SidebarNode ConfigLeaf = new("Cấu hình", "Settings24", "ConfigurationStationView", [])
    {
        OwnedViewNames = ["AdminAuthView", "OrgUnitDeclarationView", "RealOneView"],
    };

    private static readonly SidebarNode Group1 = new("Nhóm 1", "Folder24", null, [RealLeaf, PlaceholderG1]);
    // Group2 holds two placeholders so THREE leaves share ComingSoonView overall — production emits three per
    // group (PlaceholderMenuSeed), and a two-leaf fixture never exercises the resolver's 3rd-and-beyond branch.
    private static readonly SidebarNode Group2 =
        new("Nhóm 2", "Folder24", null, [PlaceholderG2, PlaceholderG2B, DuplicateA, DuplicateB]);

    private static MainWindowViewModel CreateViewModel()
    {
        var menuBuilder = new Mock<ISidebarMenuBuilder>();
        menuBuilder.Setup(b => b.BuildMain()).Returns([Group1, Group2]);
        menuBuilder.Setup(b => b.BuildFooter()).Returns([HelpLeaf, ConfigLeaf]);

        var regions = new Mock<IRegionCollection>();
        regions.Setup(r => r.ContainsRegionWithName(It.IsAny<string>())).Returns(false);
        var regionManager = new Mock<IRegionManager>();
        regionManager.Setup(r => r.Regions).Returns(regions.Object);

        return new MainWindowViewModel(
            regionManager.Object,
            new StartupStatusViewModel(new Mock<IStartupState>().Object),
            menuBuilder.Object);
    }

    [Fact]
    public void ResolveLeaf_returns_the_leaf_when_the_view_name_is_unique()
    {
        CreateViewModel().ResolveLeaf("RealOneView", title: null).Should().BeSameAs(RealLeaf);
    }

    [Fact]
    public void ResolveLeaf_finds_footer_leaves_too()
    {
        CreateViewModel().ResolveLeaf("HelpView", title: null).Should().BeSameAs(HelpLeaf);
    }

    // The View matches the resolved node against each NavigationViewItem's CommandParameter by REFERENCE, so
    // resolution must hand back the very instance that lives in the menu tree -- not an equal copy. SidebarNode
    // is a record (value equality), so an equality-based assertion would pass even if this broke.
    [Fact]
    public void ResolveLeaf_returns_the_instance_from_the_menu_tree_not_an_equal_copy()
    {
        var vm = CreateViewModel();

        var resolved = vm.ResolveLeaf("RealOneView", title: null);

        resolved.Should().BeSameAs(vm.MainMenu[0].Children[0]);
    }

    // Home/Dashboard is deliberately not a sidebar item: the AST title text is its affordance. Resolution must
    // report "no leaf" so the View lights the title instead of guessing at a leaf.
    [Fact]
    public void ResolveLeaf_returns_null_for_a_view_that_has_no_sidebar_leaf()
    {
        CreateViewModel().ResolveLeaf("DashboardView", title: null).Should().BeNull();
    }

    [Fact]
    public void ResolveLeaf_returns_null_for_an_unknown_view()
    {
        CreateViewModel().ResolveLeaf("NoSuchView", title: null).Should().BeNull();
    }

    // The disambiguation that the whole design turns on: two leaves in different groups share ComingSoonView,
    // and only the title separates them.
    [Fact]
    public void ResolveLeaf_uses_the_title_to_pick_between_leaves_that_share_a_view()
    {
        var vm = CreateViewModel();

        vm.ResolveLeaf("ComingSoonView", "Menu 2.1").Should().BeSameAs(PlaceholderG2);
        vm.ResolveLeaf("ComingSoonView", "Menu 1.1").Should().BeSameAs(PlaceholderG1);
    }

    // A navigation to a shared view name from somewhere that passes no title (a card, a future programmatic
    // navigate) is genuinely ambiguous. Light nothing rather than the first match.
    [Fact]
    public void ResolveLeaf_returns_null_when_the_view_is_shared_and_no_title_disambiguates_it()
    {
        CreateViewModel().ResolveLeaf("ComingSoonView", title: null).Should().BeNull();
    }

    [Fact]
    public void ResolveLeaf_returns_null_when_the_title_matches_no_leaf()
    {
        CreateViewModel().ResolveLeaf("ComingSoonView", "Menu 9.9").Should().BeNull();
    }

    // Title is a tie-breaker, not a filter: a view name that identifies exactly one leaf resolves regardless of
    // what title (if any) came along with the navigation.
    [Fact]
    public void ResolveLeaf_ignores_the_title_when_the_view_name_is_already_unique()
    {
        CreateViewModel().ResolveLeaf("RealOneView", "a title nobody sent").Should().BeSameAs(RealLeaf);
    }

    // Groups carry no target and must never be reported as the shown leaf -- the group is marked by its parent
    // co-highlight, not by being resolved.
    [Fact]
    public void ResolveLeaf_never_returns_a_group_node()
    {
        var vm = CreateViewModel();

        foreach (var name in new[] { "RealOneView", "HelpView", "ConfigurationStationView" })
        {
            vm.ResolveLeaf(name, title: null).Should().NotBeSameAs(Group1).And.NotBeSameAs(Group2);
        }
    }

    // Three leaves share ComingSoonView here, as they do in production. A fixture with only two never reaches the
    // resolver's "third and beyond" accumulation branch — the branch every real placeholder navigation uses.
    [Fact]
    public void ResolveLeaf_disambiguates_when_three_or_more_leaves_share_a_view()
    {
        var vm = CreateViewModel();

        vm.ResolveLeaf("ComingSoonView", "Menu 1.1").Should().BeSameAs(PlaceholderG1);
        vm.ResolveLeaf("ComingSoonView", "Menu 2.1").Should().BeSameAs(PlaceholderG2);
        vm.ResolveLeaf("ComingSoonView", "Menu 2.2").Should().BeSameAs(PlaceholderG2B);
    }

    // The one degradation the design must never make: an ambiguity that resolves to SOMETHING rather than to
    // nothing. If two leaves share a target AND a title, neither is defensibly "the" answer, so light neither.
    [Fact]
    public void ResolveLeaf_returns_null_when_the_title_itself_is_ambiguous()
    {
        CreateViewModel().ResolveLeaf("DupView", "Menu 9.1").Should().BeNull();
    }

    // A screen reachable only from a hub (Trạm cấu hình's three Execute cards) has no sidebar leaf of its own.
    // The hub stays lit, because that IS where the operator is in the menu tree.
    [Fact]
    public void ResolveLeaf_returns_the_hub_leaf_for_a_view_the_hub_owns()
    {
        CreateViewModel().ResolveLeaf("OrgUnitDeclarationView", title: null).Should().BeSameAs(ConfigLeaf);
    }

    // Ownership is a FALLBACK, never an override: a screen that is a sidebar leaf in its own right lights itself,
    // even if some hub also claims it. Without this the hub could silently steal a real leaf's highlight.
    [Fact]
    public void ResolveLeaf_prefers_a_real_leaf_over_a_hub_that_also_owns_the_view()
    {
        CreateViewModel().ResolveLeaf("RealOneView", title: null).Should().BeSameAs(RealLeaf);
    }

    // Ownership must not become a catch-all: an unknown view is still unknown.
    [Fact]
    public void ResolveLeaf_returns_null_for_a_view_no_leaf_owns()
    {
        CreateViewModel().ResolveLeaf("SomeOtherView", title: null).Should().BeNull();
    }

    // rule-platform-infra §1: edge/bootstrap code fails clearly, never crashes. If the content region is not
    // registered yet, tracking must degrade to "no highlight updates" + a logged error -- the shell must still
    // open. The mock above reports no such region.
    [Fact]
    public void StartTrackingShownView_does_not_throw_when_the_content_region_is_absent()
    {
        var vm = CreateViewModel();

        var act = () => vm.StartTrackingShownView();

        act.Should().NotThrow();
    }
}
