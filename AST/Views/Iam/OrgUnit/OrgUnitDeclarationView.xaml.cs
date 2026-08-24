using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AST.Controls;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation.Iam;
using AST.Shell.ViewModels.Iam;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Serilog;
using Wpf.Ui;

namespace AST.Views.Iam.OrgUnit;

// Screen A chrome. Leave-confirm + wipe come from DeclarationFormView via ConfirmLeaveAsync /
// TreeSelectionGate. Tree expand≠select / collapse-keeps-selection live in OrgUnitTreeView +
// OrgUnitTreeViewItem (single selection origin). TreeRoots/HistoryRows live on the VM; LocalChrome
// keeps View-local visual state.
public partial class OrgUnitDeclarationView : DeclarationFormView
{
    private readonly IBusinessDateProvider _dates;
    private readonly TreeSelectionGate _treeSelection;

    // Last as-of the operator successfully applied (dirty-confirm accepted). Revert snaps back here.
    private bool _lastAcceptedTreeAsOfToday = true;
    private DateOnly? _lastAcceptedTreeAsOfDate;
    // Set around every programmatic write to the as-of chrome, always in the same try/finally —
    // revert after rejected leave, apply after confirmed leave, and OnLeaving's D1 reset.
    private bool _programmaticTreeAsOfWrite;
    // View owns accepted-as-of bookkeeping; VM._treeLoadGeneration cannot cover a stale post-await write here.
    private int _treeAsOfGeneration;

    // Item 3 (2026-08-10 fix round): RefreshParentSurface resolves the parent label from TODAY's tree
    // (FindTreeNodeLabel against vm.TreeRoots) -- correct for a tree/LoadAsync-driven card, but wrong for
    // a history-row-driven one (LoadFromHistoryRow): a lapsed unit's parent is often absent from today's
    // tree, so the label came up blank. Set to the row's own as-of ParentLabel exactly when a history row
    // is the current card's source; cleared at every site that also calls Chrome.ClearHistoryViewConsumed()
    // -- those are exactly the points where the card source stops being "the viewed history row."
    private string? _historyRowParentLabel;

    // Local chrome (selection / supplemental progress / as-of) — tree/history collections are on the VM.
    public LocalChrome Chrome { get; } = new();

    public OrgUnitDeclarationView(IContentDialogService dialogService, IBusinessDateProvider dates)
        : base(dialogService)
    {
        _dates = dates;
        Chrome.BusinessToday = dates.Today;
        Chrome.TreeAsOfDate = dates.Today;
        _lastAcceptedTreeAsOfDate = dates.Today;
        Chrome.PropertyChanged += OnChromeTreeAsOfDateChanged;
        InitializeComponent();
        _treeSelection = new TreeSelectionGate(
            () => Chrome.SelectedTreeNode?.Id,
            ConfirmLeaveAsync,
            CommitTreeSelectionAsync);
        OrgUnitTree.SelectionRequested += OnTreeSelectionRequestedAsync;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is OrgUnitDeclarationViewModel vm)
            {
                // Defensive: Loaded can fire more than once for a region-hosted view.
                vm.PropertyChanged -= OnViewModelPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.CardClearedAfterSave -= OnCardClearedAfterSave;
                vm.CardClearedAfterSave += OnCardClearedAfterSave;
                Chrome.FormInEditing = vm.Mode == OrgUnitCardMode.Editing;
                RefreshParentSurface(vm);
                RebuildHistoryRowsView(vm);
                await vm.LoadTreeAsync(_dates.Today);
                await vm.LoadAllHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Screen A");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is OrgUnitDeclarationViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.CardClearedAfterSave -= OnCardClearedAfterSave;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not OrgUnitDeclarationViewModel vm)
            return;

        if (e.PropertyName is nameof(OrgUnitDeclarationViewModel.Mode)
            or nameof(OrgUnitDeclarationViewModel.IsParentLocked)
            or nameof(OrgUnitDeclarationViewModel.ParentCandidates)
            or nameof(OrgUnitDeclarationViewModel.ParentId)
            or nameof(OrgUnitDeclarationViewModel.IsRoot)
            or nameof(OrgUnitDeclarationViewModel.ParentEligibility))
        {
            RefreshParentSurface(vm);
        }

        if (e.PropertyName is nameof(OrgUnitDeclarationViewModel.Mode))
        {
            Chrome.FormInEditing = vm.Mode == OrgUnitCardMode.Editing;
            // After History→View, BeginAdd blanks the card — allow Xem again on the same row.
            if (vm.Mode == OrgUnitCardMode.Adding)
            {
                Chrome.ClearHistoryViewConsumed();
                _historyRowParentLabel = null;
            }
        }

        if (e.PropertyName is nameof(OrgUnitDeclarationViewModel.Supplemental))
        {
            Chrome.SupplementalDraft = SupplementalDraft.FromDto(vm.Supplemental);
            var (filled, total) = Chrome.SupplementalDraft.CountProgress();
            Chrome.SupplementalFilledCount = filled;
            Chrome.SupplementalTotalCount = total;
        }

        if (e.PropertyName is nameof(OrgUnitDeclarationViewModel.HistoryRows))
            RebuildHistoryRowsView(vm);
        else if (e.PropertyName is nameof(OrgUnitDeclarationViewModel.HistoryFilterText))
            Chrome.HistoryRowsView?.Refresh();
    }

    private void RebuildHistoryRowsView(OrgUnitDeclarationViewModel vm)
    {
        var view = CollectionViewSource.GetDefaultView(vm.HistoryRows);
        view.Filter = item => MatchesHistoryFilter(item as OrgUnitHistoryRow, vm.HistoryFilterText);
        Chrome.HistoryRowsView = view;
    }

    private static bool MatchesHistoryFilter(OrgUnitHistoryRow? row, string? filter)
    {
        if (row is null)
            return false;
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var needle = filter.Trim();
        return ContainsIgnoreCase(row.OrgCode, needle)
            || ContainsIgnoreCase(row.NameFull, needle)
            || ContainsIgnoreCase(row.NameShort, needle)
            || ContainsIgnoreCase(row.ParentLabel, needle)
            || ContainsIgnoreCase(row.Operation, needle)
            || ContainsIgnoreCase(row.RecordedBy, needle)
            || ContainsIgnoreCase(row.Reason, needle);
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void CloseSupplementalOverlayIfOpen()
    {
        if (SupplementalOverlay.Visibility == Visibility.Visible)
            SupplementalOverlay.Visibility = Visibility.Collapsed;
    }

    protected override void OnLeaving(NavigationContext navigationContext)
    {
        // FR13: navigate-away means the whole edit session is cancelled, including any still-open
        // Supplemental dialog and its unsaved draft — it must not survive to the next visit. By the
        // time this runs, ConfirmNavigationRequest (base class) has already resolved "yes, leave."
        CloseSupplementalOverlayIfOpen();
        SupplementalHost.CloseRequested -= OnSupplementalCloseRequested;
        SupplementalHost.DraftSaved -= OnSupplementalDraftSaved;
        SupplementalHost.DraftChanged -= OnSupplementalDraftChanged;

        OrgUnitTree.SelectSilently(null);
        Chrome.SelectedTreeNode = null;
        Chrome.ClearHistoryViewConsumed();
        Chrome.SelectedHistoryRow = null;
        _historyRowParentLabel = null;
        if (DataContext is OrgUnitDeclarationViewModel vm)
        {
            // Brief 045 FR2 D1: sticky filter within the screen; reset on leave of Screen A.
            // ClearHistory() now runs ONLY here (screen leave) -- tree/as-of actions no longer touch
            // History at all (brief 049 decouple), and Show All only clears HistoryFilterText, not
            // HistoryRows (brief 049 step 3). Filter reset here is leave-scope, same as ClearHistory.
            vm.HistoryFilterText = string.Empty;
            vm.ClearHistory();
        }

        // D1 (requester, 2026-08-04): re-entering the screen always starts at today. The region keeps this
        // view alive, so without this reset OnLoaded's LoadTreeAsync(today) would disagree with a stale
        // "Ngày cụ thể" chrome -- the card would then load a PAST version of a node resolved from TODAY's
        // tree, with no banner. The as-of chrome and the tree's actual as-of must never diverge.
        _programmaticTreeAsOfWrite = true;
        try
        {
            Chrome.TreeAsOfToday = true;
            Chrome.TreeAsOfDate = _dates.Today;
        }
        finally
        {
            _programmaticTreeAsOfWrite = false;
        }

        _lastAcceptedTreeAsOfToday = true;
        _lastAcceptedTreeAsOfDate = _dates.Today;
    }

    private void OnCardClearedAfterSave(object? sender, EventArgs e)
    {
        CloseSupplementalOverlayIfOpen();
        OrgUnitTree.SelectSilently(null);
        Chrome.SelectedTreeNode = null;
        Chrome.ClearHistoryViewConsumed();
        Chrome.SelectedHistoryRow = null;
        _historyRowParentLabel = null;
    }

    private void RefreshParentSurface(OrgUnitDeclarationViewModel vm)
    {
        // Unlocked Add: three eligibility states, not a boolean. Unresolved (no complete period yet)
        // and Loading (query in flight, candidates still empty) must stay blank Display — folding them
        // into isRootCreation via Count==0, or merely AND-ing a completeness flag onto isRootCreation,
        // flips showPicker to Editable-with-nothing (the original trap) or announces root creation while
        // candidates are still loading (the same bug one step later).
        if (vm.Mode == OrgUnitCardMode.Adding && !vm.IsParentLocked)
        {
            if (vm.ParentEligibility != ParentEligibilityState.Resolved)
            {
                Chrome.ParentMode = AstOrgUnitPickerMode.Display;
                Chrome.ParentDisplayText = string.Empty;
                return;
            }

            if (vm.ParentId is null && vm.ParentCandidates.Count == 0)
            {
                Chrome.ParentMode = AstOrgUnitPickerMode.Display;
                Chrome.ParentDisplayText = "Đơn vị gốc (không có cha)";
                return;
            }

            Chrome.ParentMode = AstOrgUnitPickerMode.Editable;
            return;
        }

        Chrome.ParentMode = AstOrgUnitPickerMode.Display;

        // Resolve locked-parent / viewing text from ParentId against vm.TreeRoots -- never from
        // whichever tree node happens to be selected (History→View leaves selection cleared).
        if (vm.IsRoot)
        {
            Chrome.ParentDisplayText = "Đơn vị gốc (không có cha)";
            return;
        }

        Chrome.ParentDisplayText = vm.ParentId is { } parentId
            ? _historyRowParentLabel ?? FindTreeNodeLabel(vm.TreeRoots, parentId) ?? string.Empty
            : string.Empty;
    }

    private DateOnly CurrentTreeAsOf() =>
        Chrome.TreeAsOfToday ? _dates.Today : Chrome.TreeAsOfDate ?? _dates.Today;

    private async Task OnTreeSelectionRequestedAsync(OrgUnitTreeNode node)
    {
        try
        {
            await _treeSelection.RequestAsync(node);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle tree selection request");
        }
    }

    private async Task CommitTreeSelectionAsync(OrgUnitTreeNode node)
    {
        CloseSupplementalOverlayIfOpen();
        Chrome.SelectedTreeNode = node;
        Chrome.ClearHistoryViewConsumed();
        _historyRowParentLabel = null;
        OrgUnitTree.SelectSilently(node);

        if (DataContext is OrgUnitDeclarationViewModel vm)
        {
            await vm.LoadAsync(node.Id, CurrentTreeAsOf());
        }
    }

    private async void OnTreePanelPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // Empty-area click inside the tree panel (not on a node / as-of chrome) → Clear (§2.7.4 / §2.7.5).
            if (e.OriginalSource is not DependencyObject source)
                return;
            if (FindAncestor<TreeViewItem>(source) is not null)
                return;
            if (FindAncestor<RadioButton>(source) is not null)
                return;
            if (FindAncestor<AstDateBox>(source) is not null)
                return;
            if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
                && FindAncestor<TreeView>(source) is null)
                return;

            if (OrgUnitTree.SelectedItem is null && DataContext is OrgUnitDeclarationViewModel { IsDirty: false })
                return;

            if (!await ConfirmLeaveAsync())
                return;

            CloseSupplementalOverlayIfOpen();
            OrgUnitTree.SelectSilently(null);
            Chrome.SelectedTreeNode = null;
            Chrome.ClearHistoryViewConsumed();
            Chrome.SelectedHistoryRow = null;
            _historyRowParentLabel = null;
            if (DataContext is OrgUnitDeclarationViewModel vm)
            {
                vm.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle empty-area tree panel click");
        }
    }

    private async void OnChromeTreeAsOfDateChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            if (_programmaticTreeAsOfWrite || !IsLoaded)
                return;
            if (e.PropertyName == nameof(LocalChrome.TreeAsOfDate) && Chrome.TreeAsOfSpecific)
                await RebuildTreeAsOfAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rebuild tree after as-of date chrome changed");
        }
    }

    // FR15: single entry point for "operator wants to change as-of via a radio, form is dirty."
    // Confirms exactly once, then drives the radio + rebuild on one call stack (Clarification 2).
    // Callers: OnTreeAsOfRadioPreviewMouseDown / OnTreeAsOfRadioPreviewKeyDown only.
    private async Task<bool> TryApplyTreeAsOfFromRadioAsync(RadioButton radio)
    {
        if (!await ConfirmLeaveAsync())
            return false; // "Ở lại" — caller must leave the radio untouched.

        // The radio is TwoWay-bound to Chrome.TreeAsOf*, so this write updates the chrome by itself. We
        // suppress the Checked/PropertyChanged reaction rather than let it drive the rebuild, because that
        // indirect route would arrive at RebuildTreeAsOfAsync with no way to say "this gesture is already
        // confirmed" except ambient state -- which is what this rewrite removes. One gesture, one confirm,
        // one rebuild, all on one call stack.
        _programmaticTreeAsOfWrite = true;
        try { radio.IsChecked = true; }
        finally { _programmaticTreeAsOfWrite = false; }

        await RebuildTreeAsOfAsync(leaveAlreadyConfirmed: true);
        radio.Focus();
        return true;
    }

    private async void OnTreeAsOfRadioPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // FR12: WPF RadioButton checks itself synchronously on click, before any handler runs — gate
            // HERE (tunneling, before Click/Checked bubble) so a "stay" answer never lets the radio flip
            // visually in the first place. Do not attempt to fix this inside OnTreeAsOfTodayChecked/
            // OnTreeAsOfSpecificChecked; by the time those run, WPF has already committed IsChecked.
            if (!IsLoaded || _programmaticTreeAsOfWrite)
                return;
            if (sender is not RadioButton radio || radio.IsChecked == true)
                return; // already the checked one — no state transition to gate

            if (DataContext is not OrgUnitDeclarationViewModel { IsDirty: true })
                return; // not dirty — let the click proceed normally, no dialog needed

            e.Handled = true; // suppress WPF's native check-on-click while we ask
            await TryApplyTreeAsOfFromRadioAsync(radio); // FR15: confirms once, drives the rest itself
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to gate dirty as-of radio mouse activation");
        }
    }

    private async void OnTreeAsOfRadioPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            // FR12 / M5: keyboard Space also checks the radio synchronously — same gate as mouse.
            // Enter must keep routing to the window's default button (not a leave dialog).
            if (e.Key != Key.Space)
                return;
            if (!IsLoaded || _programmaticTreeAsOfWrite)
                return;
            if (sender is not RadioButton radio || radio.IsChecked == true)
                return;
            if (DataContext is not OrgUnitDeclarationViewModel { IsDirty: true })
                return;

            e.Handled = true;
            await TryApplyTreeAsOfFromRadioAsync(radio);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to gate dirty as-of radio keyboard activation");
        }
    }

    private async void OnTreeAsOfTodayChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _programmaticTreeAsOfWrite)
                return;
            Chrome.TreeAsOfToday = true;
            await RebuildTreeAsOfAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rebuild tree after 'Hôm nay' as-of checked");
        }
    }

    private async void OnTreeAsOfSpecificChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _programmaticTreeAsOfWrite)
                return;
            Chrome.TreeAsOfToday = false;
            await RebuildTreeAsOfAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rebuild tree after 'Ngày cụ thể' as-of checked");
        }
    }

    private async Task RebuildTreeAsOfAsync(bool leaveAlreadyConfirmed = false)
    {
        // §2.7.5: change as-of → rebuild tree + unselect. Snapshot BEFORE the dirty-confirm await
        // so a value read post-await can never disagree with what the operator actually picked.
        // Generation bumps before that snapshot so a slow rebuild finishing after a newer one cannot
        // overwrite _lastAcceptedTreeAsOf* with a stale value (VM._treeLoadGeneration cannot cover
        // this — accepted-as-of state lives in the View).
        var generation = ++_treeAsOfGeneration;
        var targetToday = Chrome.TreeAsOfToday;
        var targetDate = Chrome.TreeAsOfDate;
        var targetAsOf = targetToday ? _dates.Today : targetDate ?? _dates.Today;

        // leaveAlreadyConfirmed: the Preview radio path already asked on this same call stack
        // (Clarification 2). Default false for Checked / date-box paths.
        if (!leaveAlreadyConfirmed && !await ConfirmLeaveAsync())
        {
            RevertTreeAsOfSelection();
            return;
        }

        CloseSupplementalOverlayIfOpen();
        OrgUnitTree.SelectSilently(null);
        Chrome.SelectedTreeNode = null;
        Chrome.ClearHistoryViewConsumed();
        Chrome.SelectedHistoryRow = null;
        _historyRowParentLabel = null;
        if (DataContext is OrgUnitDeclarationViewModel vm)
        {
            vm.Clear();
            await vm.LoadTreeAsync(targetAsOf);
        }

        if (generation == _treeAsOfGeneration)
        {
            _lastAcceptedTreeAsOfToday = targetToday;
            _lastAcceptedTreeAsOfDate = targetDate;
        }
    }

    private void RevertTreeAsOfSelection()
    {
        _programmaticTreeAsOfWrite = true;
        try
        {
            Chrome.TreeAsOfToday = _lastAcceptedTreeAsOfToday;
            Chrome.TreeAsOfDate = _lastAcceptedTreeAsOfDate;
        }
        finally
        {
            _programmaticTreeAsOfWrite = false;
        }
    }

    private void OnHistoryShowAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is OrgUnitDeclarationViewModel vm)
                vm.HistoryFilterText = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle history Show All");
        }
    }

    private async void OnHistoryRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is OrgUnitDeclarationViewModel vm)
                await vm.RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh history");
        }
    }

    private async void OnHistoryViewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Chrome.SelectedHistoryRow is not OrgUnitHistoryRow row || !Chrome.CanViewHistory)
                return;

            if (!await ConfirmLeaveAsync())
                return;

            CloseSupplementalOverlayIfOpen();
            // §2.7.4: History View loads that row's version; tree selection cleared.
            OrgUnitTree.SelectSilently(null);
            Chrome.SelectedTreeNode = null;

            if (DataContext is not OrgUnitDeclarationViewModel vm)
                return;

            // Item 2 (2026-08-10 fix round): split by whether the row is ACTIONABLE -- Effective/Pending,
            // i.e. CanEdit/CanClose could become true for it.
            //
            // HistoryRows is only refilled on load/Refresh, so routing an actionable row through the
            // synchronous LoadFromHistoryRow below could hand the operator a card whose Edit/Close buttons
            // are live against state that changed since the grid loaded (~30 concurrent operators). The
            // write is still server-guarded and fails clearly, but staleness could waste a whole edit
            // session -- something the tree-driven path never risked. Route these through LoadAsync
            // instead, which re-reads the row fresh; they resolve correctly through the date-coverage
            // route because they are isactive=1 as of today.
            //
            // Expired/Cancelled rows are NOT actionable (CanEdit/CanClose already exclude them), so
            // staleness cannot hurt them here -- and they are exactly the rows LoadAsync's date-resolved
            // route gets WRONG for a lapsed identity (defect A): GetByIdentityAsync/EffectivePeriodResolver
            // requires SOME version to cover a date, which a superseded (isactive=0) or fully-lapsed
            // identity can never satisfy, either erroring outright or resolving a DIFFERENT live version at
            // that date and mislabelling _currentVersionId. Route these through LoadFromHistoryRow, which
            // reads the row's own already-fetched data with no repository round trip and no coverage
            // requirement, so it never fails on them.
            if (row.Status is VersionStatus.Effective or VersionStatus.Pending)
            {
                // Not a history-row-driven load -- the parent label resolves from today's tree same as
                // any other LoadAsync (item 3's override applies only to the LoadFromHistoryRow branch).
                _historyRowParentLabel = null;
                await vm.LoadAsync(row.OrgUnitId, row.EffectiveFrom);
            }
            else
            {
                // Set BEFORE LoadFromHistoryRow (item 3): its Clear()+field assignments raise
                // PropertyChanged synchronously, and RefreshParentSurface must see the override the first
                // time it reacts to this load, not one round-trip later.
                _historyRowParentLabel = row.ParentId is null ? null : row.ParentLabel;
                vm.LoadFromHistoryRow(row);
            }

            Chrome.MarkHistoryViewConsumed(row);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to view history row");
        }
    }

    private void OnSupplementalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not OrgUnitDeclarationViewModel vm || !vm.CanOpenSupplemental)
                return;

            // ReadOnly/Closing = view-locked draft; Adding/Editing = unlocked entry.
            // ReadOnly/Closing both forbid [Sửa] unlock inside the dialog.
            var lockFields = vm.Mode is OrgUnitCardMode.ReadOnly or OrgUnitCardMode.Closing;
            var allowUnlock = vm.Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Editing;

            SupplementalHost.Dialogs = Dialogs;
            SupplementalHost.LoadDraft(Chrome.SupplementalDraft, lockFields, allowUnlock);
            SupplementalHost.CloseRequested -= OnSupplementalCloseRequested;
            SupplementalHost.DraftSaved -= OnSupplementalDraftSaved;
            SupplementalHost.DraftChanged -= OnSupplementalDraftChanged;
            SupplementalHost.CloseRequested += OnSupplementalCloseRequested;
            SupplementalHost.DraftSaved += OnSupplementalDraftSaved;
            SupplementalHost.DraftChanged += OnSupplementalDraftChanged;
            SupplementalOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show the supplemental dialog");
        }
    }

    private void OnSupplementalDraftSaved(object? sender, EventArgs e)
    {
        Chrome.SupplementalDraft = SupplementalHost.Draft.Clone();
        var (filled, total) = Chrome.SupplementalDraft.CountProgress();
        Chrome.SupplementalFilledCount = filled;
        Chrome.SupplementalTotalCount = total;
        if (DataContext is OrgUnitDeclarationViewModel vm)
            vm.Supplemental = Chrome.SupplementalDraft.ToDto();
    }

    private void OnSupplementalDraftChanged(object? sender, EventArgs e)
    {
        if (DataContext is OrgUnitDeclarationViewModel vm)
            vm.MarkSupplementalDirty();
    }

    private async void OnSupplementalCloseRequested(object? sender, EventArgs e)
    {
        try
        {
            if (SupplementalHost.IsDirtyUnlocked)
            {
                // D4: same single leave question as the card, through the shared base gate.
                if (!await ConfirmLeaveAsync())
                    return;
            }
            else if (SupplementalHost.IsDraftLocked)
            {
                Chrome.SupplementalDraft = SupplementalHost.Draft.Clone();
                var (filled, total) = Chrome.SupplementalDraft.CountProgress();
                Chrome.SupplementalFilledCount = filled;
                Chrome.SupplementalTotalCount = total;
            }

            SupplementalOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to close supplemental overlay");
        }
    }

    // Resolve a tree node's Label by org-unit id (recursive). Visited-set guards against any future
    // non-tree data (FR2 defense-in-depth — LoadTreeCoreAsync already cuts cycles at attach time).
    private static string? FindTreeNodeLabel(IEnumerable<OrgUnitTreeNode> roots, long id) =>
        FindTreeNodeLabel(roots, id, new HashSet<long>());

    private static string? FindTreeNodeLabel(IEnumerable<OrgUnitTreeNode> roots, long id, HashSet<long> visited)
    {
        foreach (var node in roots)
        {
            if (!visited.Add(node.Id))
                continue;
            if (node.Id == id)
                return node.Label;
            var nested = FindTreeNodeLabel(node.Children, id, visited);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = OrgUnitTreeView.GetVisualOrLogicalParent(node);
        }

        return null;
    }

    public sealed class LocalChrome : BindableBase
    {
        private DateOnly _businessToday;
        public DateOnly BusinessToday
        {
            get => _businessToday;
            set => SetProperty(ref _businessToday, value);
        }

        private DateOnly? _treeAsOfDate;
        public DateOnly? TreeAsOfDate
        {
            get => _treeAsOfDate;
            set => SetProperty(ref _treeAsOfDate, value);
        }

        private bool _treeAsOfToday = true;
        public bool TreeAsOfToday
        {
            get => _treeAsOfToday;
            set
            {
                if (SetProperty(ref _treeAsOfToday, value))
                    RaisePropertyChanged(nameof(TreeAsOfSpecific));
            }
        }

        public bool TreeAsOfSpecific
        {
            get => !_treeAsOfToday;
            set => TreeAsOfToday = !value;
        }

        private OrgUnitTreeNode? _selectedTreeNode;
        public OrgUnitTreeNode? SelectedTreeNode
        {
            get => _selectedTreeNode;
            set => SetProperty(ref _selectedTreeNode, value);
        }

        private string _parentDisplayText = string.Empty;
        public string ParentDisplayText
        {
            get => _parentDisplayText;
            set => SetProperty(ref _parentDisplayText, value);
        }

        private AstOrgUnitPickerMode _parentMode = AstOrgUnitPickerMode.Display;
        public AstOrgUnitPickerMode ParentMode
        {
            get => _parentMode;
            set => SetProperty(ref _parentMode, value);
        }

        private int _supplementalFilledCount;
        public int SupplementalFilledCount
        {
            get => _supplementalFilledCount;
            set
            {
                if (SetProperty(ref _supplementalFilledCount, value))
                {
                    RaisePropertyChanged(nameof(SupplementalProgressText));
                    RaisePropertyChanged(nameof(SupplementalProgressMajority));
                }
            }
        }

        private int _supplementalTotalCount = new SupplementalDraft().CountProgress().Total;
        public int SupplementalTotalCount
        {
            get => _supplementalTotalCount;
            set
            {
                if (SetProperty(ref _supplementalTotalCount, value))
                {
                    RaisePropertyChanged(nameof(SupplementalProgressText));
                    RaisePropertyChanged(nameof(SupplementalProgressMajority));
                }
            }
        }

        public string SupplementalProgressText =>
            $"đã khai báo {SupplementalFilledCount}/{SupplementalTotalCount} thông tin.";

        public bool SupplementalProgressMajority => SupplementalFilledCount * 2 > SupplementalTotalCount;

        public SupplementalDraft SupplementalDraft { get; set; } = new();

        // Filtered view over VM.HistoryRows (View-owned — Shell cannot reference ICollectionView).
        private ICollectionView? _historyRowsView;
        public ICollectionView? HistoryRowsView
        {
            get => _historyRowsView;
            set => SetProperty(ref _historyRowsView, value);
        }

        // History View affordance: stay visible; disable after a successful View of the
        // selected row until the selection changes or that row enters Editing.
        private OrgUnitHistoryRow? _selectedHistoryRow;
        public OrgUnitHistoryRow? SelectedHistoryRow
        {
            get => _selectedHistoryRow;
            set
            {
                if (SetProperty(ref _selectedHistoryRow, value))
                {
                    // Selecting ANY different row resets "already viewed," regardless of whether
                    // the newly-selected row is itself successfully viewable -- Xem must re-enable
                    // on every selection change, not stay stuck disabled by a stale prior success.
                    _viewedHistoryRow = null;
                    RaisePropertyChanged(nameof(CanViewHistory));
                }
            }
        }

        private OrgUnitHistoryRow? _viewedHistoryRow;

        private bool _formInEditing;
        public bool FormInEditing
        {
            get => _formInEditing;
            set
            {
                if (SetProperty(ref _formInEditing, value))
                    RaisePropertyChanged(nameof(CanViewHistory));
            }
        }

        public bool CanViewHistory =>
            SelectedHistoryRow is not null
            && (SelectedHistoryRow != _viewedHistoryRow || FormInEditing);

        public void MarkHistoryViewConsumed(OrgUnitHistoryRow row)
        {
            _viewedHistoryRow = row;
            RaisePropertyChanged(nameof(CanViewHistory));
        }

        public void ClearHistoryViewConsumed()
        {
            if (_viewedHistoryRow is null)
                return;
            _viewedHistoryRow = null;
            RaisePropertyChanged(nameof(CanViewHistory));
        }
    }
}
