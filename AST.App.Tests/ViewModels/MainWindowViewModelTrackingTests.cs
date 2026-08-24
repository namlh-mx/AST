using System.Collections;
using System.Windows.Input;
using AST.Navigation;
using AST.Core.Startup;
using AST.Shell.Navigation;
using AST.Shell.ViewModels;
using FluentAssertions;
using Moq;
using Prism.Navigation;
using Prism.Navigation.Regions;
using Wpf.Ui.Controls;

namespace AST.App.Tests.ViewModels;

// Fix round 2 — event plumbing for ShownView tracking (Moq on IRegion* is permitted: non-persistence seams).
public sealed class MainWindowViewModelTrackingTests
{
    private static readonly SidebarNode HelpLeaf = new("Trợ giúp", "QuestionCircle24", "HelpView", []);
    private static readonly SidebarNode Placeholder = new("Menu 1.1", null, "ComingSoonView", []);
    private static readonly SidebarNode Group = new("Nhóm 1", "Folder24", null, [Placeholder]);

    [Fact]
    public void StartTrackingShownView_called_twice_subscribes_Navigated_only_once()
    {
        var (vm, nav) = CreateTrackingViewModel(regionPresent: true);
        var raised = 0;
        vm.ShownViewChanged += _ => raised++;

        vm.StartTrackingShownView();
        vm.StartTrackingShownView();

        nav.Raise(n => n.Navigated += null!, CreateNavigatedArgs("HelpView", title: null));

        raised.Should().Be(1);
    }

    [Fact]
    public void StartTrackingShownView_when_region_absent_never_raises_ShownViewChanged_and_does_not_throw()
    {
        var (vm, _) = CreateTrackingViewModel(regionPresent: false);
        var raised = 0;
        vm.ShownViewChanged += _ => raised++;

        var act = () => vm.StartTrackingShownView();

        act.Should().NotThrow();
        raised.Should().Be(0);
    }

    [Fact]
    public void Navigated_publishes_resolved_leaf_using_title_parameter()
    {
        var (vm, nav) = CreateTrackingViewModel(regionPresent: true);
        MainWindowViewModel.ShownView? shown = null;
        vm.ShownViewChanged += s => shown = s;
        vm.StartTrackingShownView();

        nav.Raise(
            n => n.Navigated += null!,
            CreateNavigatedArgs("ComingSoonView", title: Placeholder.Title));

        shown.Should().NotBeNull();
        shown!.ViewName.Should().Be("ComingSoonView");
        shown.Leaf.Should().BeSameAs(Placeholder);
    }

    [Fact]
    public void NavigationFailed_publishes_view_name_from_ActiveViews()
    {
        var active = new object(); // GetType().Name == "Object" — resolves to no leaf
        var (vm, nav, _) = CreateTrackingViewModelWithRegion(regionPresent: true, activeViews: [active]);
        MainWindowViewModel.ShownView? shown = null;
        vm.ShownViewChanged += s => shown = s;
        vm.StartTrackingShownView();

        nav.Raise(
            n => n.NavigationFailed += null!,
            new RegionNavigationFailedEventArgs(
                new NavigationContext(nav.Object, new Uri("HelpView", UriKind.Relative))));

        shown.Should().NotBeNull();
        shown!.ViewName.Should().Be(nameof(Object));
        shown.Leaf.Should().BeNull();
        // Failed URI must not be used as the published name when ActiveViews is present.
        shown.ViewName.Should().NotBe("HelpView");
    }

    [Fact]
    public void Navigated_ViewName_strips_query_and_fragment()
    {
        var (vm, nav) = CreateTrackingViewModel(regionPresent: true);
        var names = new List<string>();
        vm.ShownViewChanged += s => names.Add(s.ViewName);
        vm.StartTrackingShownView();

        nav.Raise(n => n.Navigated += null!, CreateNavigatedArgs("HelpView?x=1", title: null));
        nav.Raise(n => n.Navigated += null!, CreateNavigatedArgs("HelpView#section", title: null));

        names.Should().Equal("HelpView", "HelpView");
    }

    // WPF-UI Activate/Deactivate write IsActive + SymbolIcon.Filled — the same DPs ApplyHighlight owns.
    // They stay dormant only while TargetPageType is null (closes the gate in OnClick). Setting it later
    // would produce a double-highlight mystery; fail the build instead.
    [Fact]
    public void NavigationMenuBuilder_items_never_set_TargetPageType()
    {
        Sta.Run(() =>
        {
            var nodes = new SidebarNode[]
            {
                new("G", "Folder24", null,
                [
                    new("L1", null, "LeafOneView", []),
                    new("Nest", null, null,
                    [
                        new("L2", null, "LeafTwoView", []),
                    ]),
                ]),
                new("FooterLike", "Settings24", "ConfigView", []),
            };

            var items = NavigationMenuBuilder.Build(nodes, new NoOpCommand());
            AssertAllTargetPageTypeNull(items);
        });
    }

    private static void AssertAllTargetPageTypeNull(IEnumerable items)
    {
        foreach (var raw in items)
        {
            if (raw is not NavigationViewItem item)
            {
                continue;
            }

            item.TargetPageType.Should().BeNull(
                because: "a non-null TargetPageType lets WPF-UI Activate/Deactivate fight ApplyHighlight");
            AssertAllTargetPageTypeNull(item.MenuItems);
        }
    }

    private static (MainWindowViewModel Vm, Mock<IRegionNavigationService> Nav) CreateTrackingViewModel(
        bool regionPresent)
    {
        var (vm, nav, _) = CreateTrackingViewModelWithRegion(regionPresent, activeViews: []);
        return (vm, nav);
    }

    private static (
        MainWindowViewModel Vm,
        Mock<IRegionNavigationService> Nav,
        Mock<IRegion> Region) CreateTrackingViewModelWithRegion(
        bool regionPresent,
        IReadOnlyList<object> activeViews)
    {
        var menuBuilder = new Mock<ISidebarMenuBuilder>();
        menuBuilder.Setup(b => b.BuildMain()).Returns([Group]);
        menuBuilder.Setup(b => b.BuildFooter()).Returns([HelpLeaf]);

        var nav = new Mock<IRegionNavigationService>();
        var region = new Mock<IRegion>();
        region.Setup(r => r.NavigationService).Returns(nav.Object);

        var views = new Mock<IViewsCollection>();
        views.As<IEnumerable>().Setup(e => e.GetEnumerator()).Returns(() => activeViews.GetEnumerator());
        views.Setup(v => v.GetEnumerator()).Returns(() => activeViews.GetEnumerator());
        region.Setup(r => r.ActiveViews).Returns(views.Object);

        var regions = new Mock<IRegionCollection>();
        regions.Setup(r => r.ContainsRegionWithName(MainWindowViewModel.ContentRegionName))
            .Returns(regionPresent);
        if (regionPresent)
        {
            regions.Setup(r => r[MainWindowViewModel.ContentRegionName]).Returns(region.Object);
        }

        var regionManager = new Mock<IRegionManager>();
        regionManager.Setup(r => r.Regions).Returns(regions.Object);

        var vm = new MainWindowViewModel(
            regionManager.Object,
            new StartupStatusViewModel(new Mock<IStartupState>().Object),
            menuBuilder.Object);

        return (vm, nav, region);
    }

    private static RegionNavigationEventArgs CreateNavigatedArgs(string uriString, string? title)
    {
        var nav = new Mock<IRegionNavigationService>();
        var uri = new Uri(uriString, UriKind.Relative);
        var parameters = new NavigationParameters();
        if (title is not null)
        {
            parameters.Add(ComingSoonViewModel.TitleParameterKey, title);
        }

        return new RegionNavigationEventArgs(new NavigationContext(nav.Object, uri, parameters));
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
