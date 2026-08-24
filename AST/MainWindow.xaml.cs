using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AST.Core.Presentation;
using AST.Navigation;
using AST.Shell.Navigation;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace AST;

public partial class MainWindow : FluentWindow
{
    // Brand accent applied to the icon + label of the active leaf/group/Home (#89002a = AstPrimaryDarkBrush).
    // Driven from code-behind because WPF-UI has no reliable "selected foreground". The active leaf's pink
    // background is NOT set here: ApplyHighlight raises IsActive so the stock template paints
    // NavigationViewItemBackgroundSelected — a local Background would outrank the hover trigger. Frozen: shared
    // across many items, never mutated.
    private static readonly Brush ActiveLeafBrush = CreateFrozenBrush("#89002a");

    // Top-level menu + footer items, kept so the accordion and pane-collapse handlers can iterate them.
    private readonly List<NavigationViewItem> _topLevelItems = [];

    // Maps each L1 child leaf to its top-level group item, so the shown leaf's parent group icon can be filled.
    private readonly Dictionary<NavigationViewItem, NavigationViewItem> _leafParent = [];

    // The default (Home) navigation target reached by clicking the "AST" title text. Home is NOT a sidebar item —
    // the content region shows it on startup (default navigation) and whenever AST is clicked.
    private readonly SidebarNode _homeNode = new("Trang chủ", null, ViewNames.Dashboard, []);

    // Cache of the leaf item whose screen is currently shown — written only by ApplyHighlight, read by the hover
    // handlers so they skip the already-lit leaf. Not a second source of highlight truth.
    private NavigationViewItem? _activeLeafItem;

    // Re-entrancy guard: collapsing sibling items programmatically re-enters the IsExpanded change handler.
    private bool _syncingExpansion;

    // True while the pane was auto-opened by clicking a group on the collapsed rail (no native flyout exists in
    // WPF-UI 4.3). It is a transient overlay: it auto-collapses back after a screen opens or an outside click. A
    // manual toggle clears it so a deliberately opened pane stays open (sticky).
    private bool _railAutoExpanded;

    // WPF-UI's own dialog service (registered as a singleton in App.xaml.cs), wired to RootDialogHost in OnLoaded.
    private readonly IContentDialogService _dialogService;

    public MainWindow(IContentDialogService dialogService)
    {
        _dialogService = dialogService;
        InitializeComponent();
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // Build the NavigationView chrome from the ViewModel's SidebarNode trees once the Prism-wired DataContext is
    // available (menu items are WPF-UI types, so they are created here in the View, not in the ViewModel). Runs once:
    // the handler detaches itself after a successful build so a second Loaded (element re-connected to a
    // PresentationSource) cannot duplicate items/handlers.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire the Fluent dialog host first: it has nothing to do with the ViewModel, and hanging it off the
        // menu-build path would leave every screen's dialogs broken if the DataContext guard below ever bailed.
        // RootDialogHost is in the visual tree by Loaded (rule-module-boundary §1c: chrome only, not a Prism
        // region). SetDialogHost(ContentDialogHost) is the current, non-obsolete overload.
        _dialogService.SetDialogHost(RootDialogHost);

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Loaded -= OnLoaded;

        AddMenu(NavigationMenuBuilder.Build(vm.MainMenu, vm.NavigateCommand), RootNavigation.MenuItems);
        AddMenu(NavigationMenuBuilder.Build(vm.FooterMenu, vm.NavigateCommand), RootNavigation.FooterMenuItems);

        // Observe the pane open/closed state (IsPaneOpen is a DP with no CLR event). On change we mirror the state
        // onto each item and collapse the accordion when the pane closes. Descriptors root their targets for the
        // app lifetime — fine only because the menu is built exactly once (Loaded -= above) and never rebuilt.
        // If a menu rebuild is ever added, teardown of these descriptors (and clearing _topLevelItems/_leafParent)
        // must be added with it — otherwise discarded items leak and ApplyHighlight iterates stale entries.
        DependencyPropertyDescriptor
            .FromProperty(NavigationView.IsPaneOpenProperty, typeof(NavigationView))
            .AddValueChanged(RootNavigation, OnPaneOpenChanged);
        SyncPaneState();

        // Collapse an auto-expanded rail when the user clicks anywhere outside the sidebar (there is no native
        // flyout to dismiss itself). Preview so we see the click before it is handled; we never mark it handled.
        PreviewMouseDown += OnWindowPreviewMouseDown;

        // Subscribe before tracking so the first region navigation cannot be missed. Load-bearing order (untested):
        // Prism creates the content region in Initialize(); base.OnInitialized() shows the window and raises
        // Loaded synchronously; only then does the startup RequestNavigate run. Moving that navigate above
        // base.OnInitialized() would leave the sidebar dark with no error. Seeding IsActive is unnecessary.
        vm.ShownViewChanged += OnShownViewChanged;
        vm.StartTrackingShownView();
    }

    private void OnShownViewChanged(MainWindowViewModel.ShownView shown)
    {
        try
        {
            ApplyHighlight(shown);
        }
        catch (Exception ex)
        {
            // Prism raises Navigated inside try/catch; a throw here becomes NavigationFailed, which re-publishes
            // and calls ApplyHighlight again on the same input. A second throw then escapes that catch and
            // RequestNavigate, reaching the app-level DispatcherUnhandledException net (App.xaml.cs) -- logged
            // and a controlled shutdown, not a silent kill, but still an unrecoverable exit for what should be
            // a cosmetic failure. Wrong chrome is acceptable; a dead shell is not.
            Log.Error(ex, "Sidebar chrome failed to apply highlight for view {ViewName}", shown.ViewName);
        }
    }

    private void AddMenu(IReadOnlyList<NavigationViewItem> items, IList target)
    {
        foreach (var item in items)
        {
            _topLevelItems.Add(item);
            target.Add(item);

            // Single-expand accordion: IsExpanded is a DP with no CLR change event, so observe it via descriptor.
            DependencyPropertyDescriptor
                .FromProperty(NavigationViewItem.IsExpandedProperty, typeof(NavigationViewItem))
                .AddValueChanged(item, OnTopLevelExpandedChanged);

            // A top-level group (has children) gets the rail-collapsed auto-open+expand behaviour, and its children
            // are mapped back to it so the shown leaf can fill this group's icon. Also observe IsActive: WPF-UI's
            // OnMouseDown chevron branch writes IsActive=true and marks the event handled, so our handlers never see
            // the click — we own group IsActive (always false; the group is marked by its icon) and force it back.
            // Invariant this depends on: every item carrying the IsActive descriptor is a group, and no SidebarNode
            // has both a TargetViewName and Children. A hybrid would be lit by ApplyItemHighlight's leaf branch and
            // immediately erased by this normalizer — cause split across two handlers.
            if (item.MenuItems.Count > 0)
            {
                item.Click += OnGroupClicked;
                DependencyPropertyDescriptor
                    .FromProperty(NavigationViewItem.IsActiveProperty, typeof(NavigationViewItem))
                    .AddValueChanged(item, OnGroupIsActiveChanged);
                foreach (var child in item.MenuItems)
                {
                    if (child is NavigationViewItem childItem)
                    {
                        _leafParent[childItem] = item;
                    }
                }
            }

            HookLeafClicks(item);
        }
    }

    // Attach the rail-collapse click + hover-highlight to every leaf (a navigable item carries its SidebarNode as
    // CommandParameter); group headers have no target and are skipped. The hover handlers give a child leaf the same
    // brand-red label+icon on pointer-over that an L1 group gets from its template — WPF-UI's child-item template
    // only changes Background on hover, not Foreground, so we drive the foreground from code-behind (chrome only).
    private void HookLeafClicks(NavigationViewItem item)
    {
        if (item.CommandParameter is SidebarNode { TargetViewName.Length: > 0 })
        {
            item.Click += OnLeafClicked;
            item.MouseEnter += OnLeafMouseEnter;
            item.MouseLeave += OnLeafMouseLeave;
        }

        foreach (var child in item.MenuItems)
        {
            if (child is NavigationViewItem childItem)
            {
                HookLeafClicks(childItem);
            }
        }
    }

    // Brand-red the leaf's label + icon while hovered. The shown leaf is already red (and must stay red when the
    // pointer leaves), so it is skipped by both handlers.
    private void OnLeafMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not NavigationViewItem leaf || ReferenceEquals(leaf, _activeLeafItem))
        {
            return;
        }

        ApplyHoverForeground(leaf);
    }

    private void OnLeafMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not NavigationViewItem leaf || ReferenceEquals(leaf, _activeLeafItem))
        {
            return;
        }

        ClearHoverForeground(leaf);
    }

    // Leaf click does not paint the highlight — that comes from the region's Navigated/NavigationFailed via
    // ApplyHighlight. This handler only collapses an auto-expanded rail after the user picked a leaf.
    private void OnLeafClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem)
        {
            return;
        }

        if (_railAutoExpanded)
        {
            _railAutoExpanded = false;
            RootNavigation.IsPaneOpen = false;
        }
    }

    // Click on a group header. This only expands/browses the group — it does NOT fill the group's icon. The icon
    // fill tracks the SHOWN screen (ApplyHighlight's parent co-highlight), so merely browsing a group never leaves
    // a stuck fill and the active screen's group stays marked. Click is a bubbling routed event, so a child-leaf
    // click also reaches this handler on the parent group; guard against that by resolving the click's own
    // NavigationViewItem and proceeding only when it IS this group (a leaf click resolves to the leaf and is
    // skipped — otherwise it re-opens the just-collapsed rail). On the compact rail we auto-open+expand the group
    // (WPF-UI's own OnClick toggles IsExpanded only when the pane is already open), marking the open as a transient
    // auto-expansion so it collapses back once the user picks a leaf or clicks away.
    private void OnGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem group)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            !ReferenceEquals(NearestNavigationViewItem(source), group))
        {
            return;
        }

        if (!RootNavigation.IsPaneOpen)
        {
            _railAutoExpanded = true;
            RootNavigation.IsPaneOpen = true;
            group.IsExpanded = true;
        }
    }

    // WPF-UI writes IsActive=true from NavigationViewItem.OnMouseDown's chevron-collapse branch and marks the
    // event Handled, so Click never reaches us. We own group IsActive (always false — the group is marked by its
    // filled brand-red icon instead) and erase the foreign local write with ClearValue. Re-entrancy: after our
    // ClearValue the change notification re-enters with !IsActive and returns — that early return is the guard.
    private void OnGroupIsActiveChanged(object? sender, EventArgs e)
    {
        if (sender is not NavigationViewItem group || !group.IsActive)
        {
            return;
        }

        group.ClearValue(NavigationViewItem.IsActiveProperty);
    }

    // Chrome reflects the shown screen, absolutely: every item's state is written on every navigation. There is
    // no revert path because a failed navigation just re-applies the truth. Do not make this incremental.
    private void ApplyHighlight(MainWindowViewModel.ShownView shown)
    {
        _activeLeafItem = null;

        foreach (var item in _topLevelItems)
        {
            ApplySubtreeHighlight(item, shown);
        }

        SetAstActive(shown.ViewName == _homeNode.TargetViewName);

        // Absolute repaint clears Foreground on every non-shown leaf, including one under the pointer. MouseEnter
        // will not re-fire until the pointer leaves — re-apply hover chrome so "Ở lại" does not leave a grey leaf
        // under an unchanged cursor. The shown leaf (already painted) wins and is skipped.
        RestoreHoverUnderPointer();
    }

    private void ApplySubtreeHighlight(NavigationViewItem item, MainWindowViewModel.ShownView shown)
    {
        ApplyItemHighlight(item, shown);

        foreach (var child in item.MenuItems)
        {
            if (child is NavigationViewItem childItem)
            {
                ApplySubtreeHighlight(childItem, shown);
            }
        }
    }

    private void ApplyItemHighlight(NavigationViewItem item, MainWindowViewModel.ShownView shown)
    {
        if (item.CommandParameter is SidebarNode { TargetViewName.Length: > 0 })
        {
            // Leaf: View owns IsActive outright (items have no binding/style/animation on it). A direct set is the
            // contract; SetCurrentValue would only veneer over an unknown base. Group asymmetry is below.
            var isShown = ReferenceEquals(item.CommandParameter, shown.Leaf);
            item.IsActive = isShown;
            if (isShown)
            {
                item.Foreground = ActiveLeafBrush;
                if (item.Icon is SymbolIcon icon)
                {
                    icon.Filled = true;
                    icon.Foreground = ActiveLeafBrush;
                }

                _activeLeafItem = item;
            }
            else
            {
                item.ClearValue(ForegroundProperty);
                if (item.Icon is SymbolIcon icon)
                {
                    icon.Filled = false;
                    icon.ClearValue(ForegroundProperty);
                }
            }

            return;
        }

        // Group: erase WPF-UI's local IsActive=true (ClearValue, not SetCurrentValue — the latter masks and leaves
        // the local true waiting to resurrect). Filled brand icon iff shown leaf is a direct child.
        item.ClearValue(NavigationViewItem.IsActiveProperty);
        var fillsGroup = shown.Leaf is not null && IsParentOfShownLeaf(item, shown.Leaf);
        if (item.Icon is SymbolIcon groupIcon)
        {
            if (fillsGroup)
            {
                groupIcon.Filled = true;
                groupIcon.Foreground = ActiveLeafBrush;
            }
            else
            {
                groupIcon.Filled = false;
                groupIcon.ClearValue(ForegroundProperty);
            }
        }
    }

    private void RestoreHoverUnderPointer()
    {
        foreach (var item in _topLevelItems)
        {
            RestoreHoverUnderPointer(item);
        }
    }

    private void RestoreHoverUnderPointer(NavigationViewItem item)
    {
        if (item.IsMouseOver &&
            !ReferenceEquals(item, _activeLeafItem) &&
            item.CommandParameter is SidebarNode { TargetViewName.Length: > 0 })
        {
            ApplyHoverForeground(item);
        }

        foreach (var child in item.MenuItems)
        {
            if (child is NavigationViewItem childItem)
            {
                RestoreHoverUnderPointer(childItem);
            }
        }
    }

    private static void ApplyHoverForeground(NavigationViewItem leaf)
    {
        leaf.Foreground = ActiveLeafBrush;
        if (leaf.Icon is SymbolIcon icon)
        {
            icon.Foreground = ActiveLeafBrush;
        }
    }

    private static void ClearHoverForeground(NavigationViewItem leaf)
    {
        leaf.ClearValue(ForegroundProperty);
        if (leaf.Icon is SymbolIcon icon)
        {
            icon.ClearValue(ForegroundProperty);
        }
    }

    private bool IsParentOfShownLeaf(NavigationViewItem group, SidebarNode shownLeaf)
    {
        foreach (var (leafItem, parent) in _leafParent)
        {
            if (ReferenceEquals(parent, group) &&
                ReferenceEquals(leafItem.CommandParameter, shownLeaf))
            {
                return true;
            }
        }

        return false;
    }

    // Single-expand accordion: when one top-level item expands, collapse every other one.
    private void OnTopLevelExpandedChanged(object? sender, EventArgs e)
    {
        if (_syncingExpansion || sender is not NavigationViewItem expanded || !expanded.IsExpanded)
        {
            return;
        }

        _syncingExpansion = true;
        try
        {
            foreach (var item in _topLevelItems)
            {
                if (!ReferenceEquals(item, expanded) && item.IsExpanded)
                {
                    item.IsExpanded = false;
                }
            }
        }
        finally
        {
            _syncingExpansion = false;
        }
    }

    // Our own pane toggle (the built-in one is hidden). A manual open/close is sticky: clear the auto-expand flag so
    // it is not collapsed by the next outside click.
    private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
    {
        _railAutoExpanded = false;
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_railAutoExpanded)
        {
            return;
        }

        // Clicks inside the sidebar are handled by the item/toggle logic; only collapse on a click elsewhere. Do
        // NOT mark the event handled -- the click must still reach whatever the user actually clicked.
        if (e.OriginalSource is DependencyObject source && IsWithin(source, RootNavigation))
        {
            return;
        }

        _railAutoExpanded = false;
        RootNavigation.IsPaneOpen = false;
    }

    // Walk the visual tree upward to test whether node is RootNavigation or one of its descendants.
    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null;)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            // VisualTreeHelper.GetParent only accepts Visual/Visual3D; stop the walk at a non-visual node.
            if (current is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                return false;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // Walk the visual tree upward from a click's OriginalSource to the nearest enclosing NavigationViewItem, used to
    // tell a group-header click apart from a bubbled child-leaf click. Returns null if none is found.
    private static NavigationViewItem? NearestNavigationViewItem(DependencyObject node)
    {
        for (var current = node; current is not null;)
        {
            if (current is NavigationViewItem item)
            {
                return item;
            }

            if (current is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                return null;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // "AST" title text = the Home affordance (no separate Home button): navigate to the default screen via the same
    // NavigateCommand the sidebar leaves use. Highlight for Home comes from the region's Navigated handler.
    private void OnAstClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.NavigateCommand.Execute(_homeNode);
        }
    }

    // Bold + brand-red the AST title text while Dashboard is the shown screen; ApplyHighlight clears it whenever a
    // sidebar leaf is shown, so exactly one target (AST or a sidebar path) reads as active.
    private void SetAstActive(bool active)
    {
        AstTitleText.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        if (active)
        {
            AstTitleText.Foreground = ActiveLeafBrush;
        }
        else
        {
            AstTitleText.ClearValue(ForegroundProperty);
        }
    }

    private void OnPaneOpenChanged(object? sender, EventArgs e) => SyncPaneState();

    // Mirror the NavigationView pane state onto each top-level item (child items render inline only when
    // IsPaneOpen is set), and collapse every accordion when the pane closes so it does not reopen expanded.
    private void SyncPaneState()
    {
        var paneOpen = RootNavigation.IsPaneOpen;

        _syncingExpansion = true;
        try
        {
            foreach (var item in _topLevelItems)
            {
                item.IsPaneOpen = paneOpen;
                if (!paneOpen)
                {
                    item.IsExpanded = false;
                }
            }
        }
        finally
        {
            _syncingExpansion = false;
        }
    }
}
