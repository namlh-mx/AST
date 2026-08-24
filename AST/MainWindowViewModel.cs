using System;
using System.Collections.Generic;
using AST.Shell.Navigation;
using AST.Shell.ViewModels;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation;
using Prism.Navigation.Regions;
using Serilog;

namespace AST;

// Shell-level ViewModel: drives Prism region navigation from the NavigationView menu and exposes the startup
// banner VM. DI registration is done at the composition root. Namespace is the root "AST" so Prism's default
// ViewModelLocator convention maps AST.MainWindow -> AST.MainWindowViewModel (AutoWireViewModel="True" on the
// shell window). (The admin-gate for the DB screen now lives in ConfigurationStationViewModel, not here.)
//
// The menu trees are exposed as BCL SidebarNode projections (source of truth = ISidebarMenuBuilder); the View
// (code-behind) maps them to WPF-UI NavigationViewItems so this ViewModel stays free of System.Windows types.
//
// Sidebar highlight is owned here as a BCL projection (ShownView) derived from the content region's navigation
// results — the View only paints. Prism remains the single navigation authority; this ViewModel never decides
// whether navigation happens, only reports which leaf corresponds to what the region actually shows.
public sealed class MainWindowViewModel : BindableBase
{
    // The Prism region hosted in MainWindow.xaml (ContentControl). Kept in sync with the XAML region name.
    public const string ContentRegionName = "ContentRegion";

    private readonly IRegionManager _regionManager;
    private readonly ISidebarMenuBuilder _menuBuilder;
    private bool _trackingShownView;
    private IReadOnlyList<SidebarNode>? _mainMenu;
    private IReadOnlyList<SidebarNode>? _footerMenu;

    public MainWindowViewModel(
        IRegionManager regionManager, StartupStatusViewModel startup, ISidebarMenuBuilder menuBuilder)
    {
        _regionManager = regionManager;
        Startup = startup;
        _menuBuilder = menuBuilder;
        // Deliberately NOT built here: the ctor runs during Prism's CreateShell(), which happens BEFORE
        // InitializeModules() loads business-module DLLs. Building eagerly would freeze whatever function set
        // IFunctionRegistry held at that early moment, permanently hiding anything a module registers later.
        // Build lazily instead, at first read (MainWindow.xaml.cs's OnLoaded, which runs after modules have
        // loaded and the app's own OnInitialized has registered its own functions) -- same "chosen once per
        // app run" semantics as before, just at the correct moment. Never rebuilt after the first read.
    }

    // What the content region actually shows, as the sidebar needs to read it. Leaf == null means no sidebar
    // leaf corresponds to it (Dashboard/Home, or a target the sidebar cannot identify -- see ResolveLeaf).
    public sealed record ShownView(string ViewName, SidebarNode? Leaf);

    public event Action<ShownView>? ShownViewChanged;

    // The startup-status banner VM, bound by MainWindow's banner (Message + Mode->brush).
    public StartupStatusViewModel Startup { get; }

    // The sidebar menu trees (2 levels), rendered by the View into NavigationViewItems. Built once, on first
    // read, and cached forever after -- see the ctor comment for why this cannot build eagerly.
    public IReadOnlyList<SidebarNode> MainMenu => _mainMenu ??= _menuBuilder.BuildMain();
    public IReadOnlyList<SidebarNode> FooterMenu => _footerMenu ??= _menuBuilder.BuildFooter();

    private DelegateCommand<SidebarNode>? _navigateCommand;
    public DelegateCommand<SidebarNode> NavigateCommand =>
        _navigateCommand ??= new DelegateCommand<SidebarNode>(Navigate);

    // Pure mapping over MainMenu + FooterMenu.
    // Order: (1) unique TargetViewName, (2) shared name + title tie-break, (3) OwnedViewNames fallback.
    // Ownership is last so a real leaf always wins over a hub that also claims the view, and so an
    // ambiguous ComingSoonView still lights nothing rather than guessing at a hub.
    public SidebarNode? ResolveLeaf(string viewName, string? title)
    {
        List<SidebarNode>? matches = null;
        SidebarNode? single = null;

        foreach (var leaf in EnumerateLeaves())
        {
            if (leaf.TargetViewName != viewName)
            {
                continue;
            }

            if (single is null && matches is null)
            {
                single = leaf;
            }
            else if (matches is null)
            {
                matches = [single!, leaf];
                single = null;
            }
            else
            {
                matches.Add(leaf);
            }
        }

        if (matches is null)
        {
            return single ?? ResolveOwnedLeaf(viewName);
        }

        if (title is null)
        {
            return null;
        }

        // Title must identify exactly one candidate. Two matches → light nothing (never the wrong leaf).
        SidebarNode? titled = null;
        foreach (var match in matches)
        {
            if (match.Title != title)
            {
                continue;
            }

            if (titled is not null)
            {
                return null;
            }

            titled = match;
        }

        return titled;
    }

    // Fallback only: first leaf that lists viewName in OwnedViewNames (hub chrome for child screens).
    private SidebarNode? ResolveOwnedLeaf(string viewName)
    {
        foreach (var leaf in EnumerateLeaves())
        {
            foreach (var owned in leaf.OwnedViewNames)
            {
                if (owned == viewName)
                {
                    return leaf;
                }
            }
        }

        return null;
    }

    // Subscribe once to the content region's Navigated + NavigationFailed. Absent region → Log.Error and leave
    // the highlight dead rather than crash the shell (rule-platform-infra §1).
    public void StartTrackingShownView()
    {
        if (_trackingShownView)
        {
            return;
        }

        if (!_regionManager.Regions.ContainsRegionWithName(ContentRegionName))
        {
            Log.Error(
                "Content region '{RegionName}' is not registered; sidebar highlight will not track navigation",
                ContentRegionName);
            return;
        }

        _trackingShownView = true;
        var nav = _regionManager.Regions[ContentRegionName].NavigationService;
        nav.Navigated += OnContentNavigated;
        nav.NavigationFailed += OnContentNavigationFailed;
    }

    private void Navigate(SidebarNode? node)
    {
        if (node?.TargetViewName is not { Length: > 0 } target) return;
        var parameters = new NavigationParameters { { ComingSoonViewModel.TitleParameterKey, node.Title } };
        _regionManager.RequestNavigate(ContentRegionName, target, parameters);
    }

    private void OnContentNavigated(object? sender, RegionNavigationEventArgs e)
    {
        var viewName = ViewNameFromUri(e.Uri);
        e.NavigationContext.Parameters.TryGetValue(ComingSoonViewModel.TitleParameterKey, out string? title);
        PublishShownView(viewName, title);
    }

    private void OnContentNavigationFailed(object? sender, RegionNavigationFailedEventArgs e)
    {
        // Re-apply what is still on screen — that IS the revert. Do not remember a prior leaf.
        // Title is unavailable here (only ActiveViews). Shared ComingSoonView therefore resolves to
        // null and the sidebar goes dark — acceptable today: placeholders have no leave gate, so a
        // veto never starts from one; a genuine exception self-heals on the next navigation. Do not
        // "fix" that by remembering the prior leaf.
        if (!_regionManager.Regions.ContainsRegionWithName(ContentRegionName))
        {
            PublishShownView(string.Empty, title: null);
            return;
        }

        var region = _regionManager.Regions[ContentRegionName];
        object? active = null;
        foreach (var view in region.ActiveViews)
        {
            active = view;
            break;
        }

        var viewName = active?.GetType().Name ?? string.Empty;
        PublishShownView(viewName, title: null);
    }

    private void PublishShownView(string viewName, string? title)
    {
        ShownViewChanged?.Invoke(new ShownView(viewName, ResolveLeaf(viewName, title)));
    }

    private IEnumerable<SidebarNode> EnumerateLeaves()
    {
        foreach (var node in MainMenu)
        {
            if (node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    yield return child;
                }
            }
            else
            {
                yield return node;
            }
        }

        foreach (var node in FooterMenu)
        {
            if (node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    yield return child;
                }
            }
            else
            {
                yield return node;
            }
        }
    }

    private static string ViewNameFromUri(Uri uri)
    {
        var s = uri.OriginalString;
        var q = s.IndexOfAny(['?', '#']);
        return q >= 0 ? s[..q] : s;
    }
}
