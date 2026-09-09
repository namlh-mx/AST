using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation;
using ErrorOr;
using Prism.Commands;
using Prism.Mvvm;

namespace AST.Shell.ViewModels.Iam;

public enum OrgUnitCardMode { ReadOnly, Adding, Editing, Closing, Replacing }

public enum ParentEligibilityState
{
    Incomplete,
    // Compatibility name for callers/tests written before the state became the phase of the
    // canonical decision. It is the same value, not a fifth state.
    Unresolved = Incomplete,
    Loading,
    Resolved,
    Failed,
}

public enum ParentPresentationDisposition { Display, Editable }

public enum ParentCommitDisposition
{
    NotApplicable,
    Allowed,
    BlockedIncomplete,
    BlockedLoading,
    BlockedFailed,
    BlockedSelectionRequired,
    BlockedNoEligibleParent,
    BlockedParentMismatch,
}

public readonly record struct ParentDecisionKey(
    long? OrgUnitId,
    OrgUnitCardMode Mode,
    EffectivePeriod? Period,
    int Generation);

// One immutable publication unit for every fact the parent surface and Save must agree on. The
// constructor is private so a caller cannot publish an id without its ordinary display row, or a
// display list that would make WPF's selector clear the live selection.
public sealed class ParentDecision
{
    private ParentDecision(
        ParentDecisionKey key,
        ParentEligibilityState phase,
        IReadOnlyList<OrgUnitPickerItem> realCandidates,
        IReadOnlyList<OrgUnitPickerItem> displayItems,
        long? parentId,
        OrgUnitPickerItem? selectedParentItem,
        string displayText,
        ParentPresentationDisposition presentation,
        ParentCommitDisposition commitDisposition,
        bool isContextLocked)
    {
        if (key.Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing
            && parentId is { } heldId
            && (selectedParentItem is null
                || selectedParentItem.Id != heldId
                || string.IsNullOrWhiteSpace(selectedParentItem.Display)
                || string.IsNullOrWhiteSpace(displayText)
                || displayText != selectedParentItem.Display
                || displayItems.All(item => item.Id != heldId)))
        {
            throw new InvalidOperationException(
                "An active parent decision cannot hold a parent without its non-empty ordinary display item.");
        }

        if (commitDisposition == ParentCommitDisposition.Allowed
            && key.Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing
            && phase != ParentEligibilityState.Resolved)
        {
            throw new InvalidOperationException(
                "An active parent decision cannot allow commit before eligibility is resolved.");
        }

        Key = key;
        Phase = phase;
        RealCandidates = realCandidates;
        DisplayItems = displayItems;
        ParentId = parentId;
        SelectedParentItem = selectedParentItem;
        DisplayText = displayText;
        Presentation = presentation;
        CommitDisposition = commitDisposition;
        IsContextLocked = isContextLocked;
    }

    public ParentDecisionKey Key { get; }
    public ParentEligibilityState Phase { get; }
    public IReadOnlyList<OrgUnitPickerItem> RealCandidates { get; }
    public IReadOnlyList<OrgUnitPickerItem> DisplayItems { get; }
    public long? ParentId { get; }
    public OrgUnitPickerItem? SelectedParentItem { get; }
    public string DisplayText { get; }
    public ParentPresentationDisposition Presentation { get; }
    public ParentCommitDisposition CommitDisposition { get; }
    public bool IsContextLocked { get; }
    public bool BlocksCommit => CommitDisposition is not (ParentCommitDisposition.NotApplicable or ParentCommitDisposition.Allowed);
    public bool AllowsCommit => CommitDisposition == ParentCommitDisposition.Allowed;

    internal static ParentDecision Create(
        ParentDecisionKey key,
        ParentEligibilityState phase,
        IEnumerable<OrgUnitPickerItem> realCandidates,
        IEnumerable<OrgUnitPickerItem> displayItems,
        long? parentId,
        OrgUnitPickerItem? selectedParentItem,
        string displayText,
        ParentPresentationDisposition presentation,
        ParentCommitDisposition commitDisposition,
        bool isContextLocked = false) =>
        new(
            key,
            phase,
            Array.AsReadOnly(realCandidates.ToArray()),
            Array.AsReadOnly(displayItems.ToArray()),
            parentId,
            selectedParentItem,
            displayText,
            presentation,
            commitDisposition,
            isContextLocked);
}

public enum CardLoadOutcome { Loaded, Failed, Superseded }

// Screen A declaration card: load-by-identity, dirty tracking, IDeclarationForm/IStatusBanner, the
// §2.7.10 button-matrix mode transitions, save/root-creation/parent-picker wiring, and the real
// tree/history data + post-save refresh (Phase 4d).
public sealed class OrgUnitDeclarationViewModel : BindableBase, IDeclarationForm, IStatusBanner
{
    private readonly IOrgUnitRepository _orgUnits;
    private readonly IOrgUnitDeclarationService _declaration;
    // an earlier ruling / card 238: needed for CanClose and CanReplace (and OffersRootParentOption). Same
    // injection as RoleDeclarationViewModel's own break-glass dependency -- the AUTHORITY is the
    // service's gate; this decides whether the button / picker affordance that reaches it is even shown.
    private readonly IBreakGlassPolicy _breakGlass;
    private readonly IBusinessDateProvider _dates;
    private readonly ICurrentWindowsUser _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IConfirmationPrompt _confirmation;

    // Function-level P7 (N8): ONE key gates every DB-mutating command on this screen
    // (Add/Edit/Close/Replace -- Replace joined 2026-09-04 and is gated by the same key), per
    // §2.7.9 -- there is no per-operation key. Registering this key into the live function catalog (so
    // AuthorizeAsync stops NotFound-ing) is Phase 4c/platform wiring, tracked there, not silently dropped.
    private const string FunctionKey = "Iam.OrgUnit.Declare";

    public OrgUnitDeclarationViewModel(
        IOrgUnitRepository orgUnits, IOrgUnitDeclarationService declaration, IBusinessDateProvider dates,
        ICurrentWindowsUser currentUser, IAuthorizationService authorization, IConfirmationPrompt confirmation,
        IBreakGlassPolicy breakGlass)
    {
        _orgUnits = orgUnits;
        _declaration = declaration;
        _breakGlass = breakGlass;
        _dates = dates;
        _currentUser = currentUser;
        _authorization = authorization;
        _confirmation = confirmation;

        BeginAddCommand = new DelegateCommand(ExecuteBeginAdd, () => CanAdd).ObservesProperty(() => Mode);
        BeginEditCommand = new DelegateCommand(ExecuteBeginEdit, () => CanEdit).ObservesProperty(() => Mode).ObservesProperty(() => Status);
        BeginReplaceCommand = new DelegateCommand(ExecuteBeginReplace, () => CanReplace).ObservesProperty(() => Mode).ObservesProperty(() => Status).ObservesProperty(() => IsRoot);
        BeginCloseCommand = new DelegateCommand(ExecuteBeginClose, () => CanClose).ObservesProperty(() => Mode).ObservesProperty(() => Status).ObservesProperty(() => IsRoot);
        CancelCommand = new AsyncDelegateCommand(ExecuteCancelAsync, () => CanCancel).ObservesProperty(() => Mode);
        SaveCommand = new AsyncDelegateCommand(ExecuteSaveAsync, () => CanSave)
            .ObservesProperty(() => Mode)
            .ObservesProperty(() => PeriodCommitBlocked)
            .ObservesProperty(() => IsDirty);
    }

    private bool _isLoading;

    private DateOnly? _lastTreeAsOf;

    // Stale-result guards for overlapping loads (same idiom as _parentRefreshGeneration).
    private int _treeLoadGeneration;
    private int _historyLoadGeneration;
    private int _cardLoadGeneration;

    // Entering a mutating mode takes ownership of the card; any in-flight LoadAsync must return
    // Superseded and write nothing. NOT called from Clear() — LoadAsync/LoadFromHistoryRow bump
    // before calling Clear(), so a bump inside Clear() would make every load supersede itself.
    private void InvalidateInFlightCardLoad() => ++_cardLoadGeneration;

    // Mode-entry field baseline for IsDirty (card 273). Distinct from _snapshot, which is the Hủy
    // recovery bundle captured BEFORE Add/Close transform the form.
    private EntryDirtyBaseline? _entryDirtyBaseline;
    private OrgUnitSupplementalDto? _liveSupplementalDraft;
    private string? _statusAtModeEntry;
    private StatusSeverity _severityAtModeEntry;
    private bool _publishingRevertCleanStatus;
    private bool _revertCleanOwnsStatus;

    private const string RevertToEntryStateMessage =
        "Thông tin đang khai báo không thay đổi so với thông tin hiện có của đơn vị.";

    private readonly record struct EntryDirtyBaseline(
        string OrgCode, string OrgNameFullVn, string OrgNameShortVn,
        DateOnly? EffectiveFrom, DateOnly? EffectiveTo, bool IsUndetermined, long? ParentId,
        string Reason, OrgUnitSupplementalDto Supplemental);

    private void MarkDirty()
    {
        if (_isLoading)
            return;

        RecomputeIsDirtyFromEntryBaseline();
        RefreshRevertCleanStatus();
    }

    // EffectiveFrom/To, IsUndetermined and ParentId run status writers after MarkDirty; publish the
    // revert sentence only once those writers have finished (card 273 / close-date gate ordering).
    private void MarkDirtyDeferringRevertStatus()
    {
        if (_isLoading)
            return;

        RecomputeIsDirtyFromEntryBaseline();
    }

    private void AfterFieldStatusSideEffects()
    {
        if (_isLoading)
            return;

        RefreshRevertCleanStatus();
    }

    private void RecomputeIsDirtyFromEntryBaseline()
    {
        if (_entryDirtyBaseline is not { } baseline)
        {
            IsDirty = true;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
            return;
        }

        var supplemental = _liveSupplementalDraft ?? Supplemental;
        var dirty = OrgCode != baseline.OrgCode
            || OrgNameFullVn != baseline.OrgNameFullVn
            || OrgNameShortVn != baseline.OrgNameShortVn
            || EffectiveFrom != baseline.EffectiveFrom
            || EffectiveTo != baseline.EffectiveTo
            || IsUndetermined != baseline.IsUndetermined
            || ParentId != baseline.ParentId
            || Reason != baseline.Reason
            || supplemental != baseline.Supplemental;

        IsDirty = dirty;
        RaisePropertyChanged(nameof(CanOpenSupplemental));
    }

    private void CaptureEntryDirtyBaseline()
    {
        _entryDirtyBaseline = new EntryDirtyBaseline(
            OrgCode, OrgNameFullVn, OrgNameShortVn,
            EffectiveFrom, EffectiveTo, IsUndetermined, ParentId,
            Reason, Supplemental);
        _liveSupplementalDraft = null;
        _statusAtModeEntry = StatusMessage;
        _severityAtModeEntry = Severity;
        IsDirty = false;
        RaisePropertyChanged(nameof(CanOpenSupplemental));
    }

    private void ReleaseEntryDirtyBaseline()
    {
        _entryDirtyBaseline = null;
        _liveSupplementalDraft = null;
        ClearRevertCleanOwnedStatus();
    }

    private void RefreshRevertCleanStatus()
    {
        if (_entryDirtyBaseline is null)
            return;

        if (IsDirty)
        {
            ClearRevertCleanOwnedStatus();
            return;
        }

        // Never overwrite Error / Warning / Success authored by anything else.
        if (!_revertCleanOwnsStatus
            && Severity is StatusSeverity.Error or StatusSeverity.Warning or StatusSeverity.Success)
        {
            return;
        }

        // Restore what mode entry left on the band; fill the settled sentence only when that was empty.
        if (_severityAtModeEntry == StatusSeverity.None
            && string.IsNullOrEmpty(_statusAtModeEntry))
        {
            PublishRevertCleanStatus();
            return;
        }

        _publishingRevertCleanStatus = true;
        try
        {
            StatusMessage = _statusAtModeEntry;
            Severity = _severityAtModeEntry;
            _revertCleanOwnsStatus = false;
        }
        finally
        {
            _publishingRevertCleanStatus = false;
        }
    }

    private void PublishRevertCleanStatus()
    {
        _publishingRevertCleanStatus = true;
        try
        {
            StatusMessage = RevertToEntryStateMessage;
            Severity = StatusSeverity.Info;
            _revertCleanOwnsStatus = true;
            _closeGateOwnsStatus = false;
        }
        finally
        {
            _publishingRevertCleanStatus = false;
        }
    }

    private void ClearRevertCleanOwnedStatus()
    {
        if (!_revertCleanOwnsStatus
            && StatusMessage != RevertToEntryStateMessage)
        {
            return;
        }

        _publishingRevertCleanStatus = true;
        try
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
            _revertCleanOwnsStatus = false;
        }
        finally
        {
            _publishingRevertCleanStatus = false;
        }
    }

    private long? _orgUnitId;
    private long? _currentVersionId;

    private string _orgCode = string.Empty;
    public string OrgCode
    {
        get => _orgCode;
        set { if (SetProperty(ref _orgCode, value)) MarkDirty(); }
    }

    private string _orgNameFullVn = string.Empty;
    public string OrgNameFullVn
    {
        get => _orgNameFullVn;
        set { if (SetProperty(ref _orgNameFullVn, value)) MarkDirty(); }
    }

    private string _orgNameShortVn = string.Empty;
    public string OrgNameShortVn
    {
        get => _orgNameShortVn;
        set { if (SetProperty(ref _orgNameShortVn, value)) MarkDirty(); }
    }

    private DateOnly? _effectiveFrom;
    public DateOnly? EffectiveFrom
    {
        get => _effectiveFrom;
        set
        {
            if (SetProperty(ref _effectiveFrom, value))
            {
                MarkDirtyDeferringRevertStatus();
                RecomputeParentEligibility();
                // Defensive: IsEffectivePeriodEnabled depends on Mode, _snapshot and _dates.Today (via
                // IsCloseCancelPlanBranch → TryBuildSnapshotTargetPeriod), not on this live value.
                // CaptureSnapshot / RestoreSnapshot own _snapshot; mode entry raises Mode afterwards.
                // Keep the raise so a future path that changes enablement without raising Mode cannot
                // leave the strip's IsEnabled binding stale while unit tests stay green.
                RaisePropertyChanged(nameof(IsEffectivePeriodEnabled));
                SyncCloseDateStatusHint();
                AfterFieldStatusSideEffects();
            }
        }
    }

    private DateOnly? _effectiveTo;
    public DateOnly? EffectiveTo
    {
        get => _effectiveTo;
        set
        {
            if (SetProperty(ref _effectiveTo, value))
            {
                MarkDirtyDeferringRevertStatus();
                RecomputeParentEligibility();
                OnCloseDateFieldEdited();
                AfterFieldStatusSideEffects();
            }
        }
    }

    private bool _isUndetermined;
    public bool IsUndetermined
    {
        get => _isUndetermined;
        set
        {
            if (SetProperty(ref _isUndetermined, value))
            {
                MarkDirtyDeferringRevertStatus();
                RecomputeParentEligibility();
                OnCloseDateFieldEdited();
                AfterFieldStatusSideEffects();
            }
        }
    }

    private long? _stagedParentId;
    public long? ParentId
    {
        get => ParentDecision.ParentId;
        set
        {
            // Card 261 Part 2: refuse a null write while Replacing outside a load/clear cycle.
            // Clear() sets _isLoading before ParentId = null, so the lifecycle null still lands.
            if (value is null
                && Mode == OrgUnitCardMode.Replacing
                && !_isLoading)
            {
                return;
            }

            if (_isLoading)
            {
                _stagedParentId = value;
                return;
            }

            if (ParentDecision.ParentId == value)
                return;

            var selectedItem = value is { } id ? ResolveParentItem(id) : null;
            PublishParentDecision(BuildParentDecision(
                ParentDecision.Key,
                ParentDecision.Phase,
                ParentDecision.RealCandidates,
                value,
                selectedItem,
                ParentDecision.IsContextLocked));
            MarkDirtyDeferringRevertStatus();
            SyncReplaceParentPeriodGate();
            AfterFieldStatusSideEffects();
        }
    }

    private string _reason = string.Empty;
    public string Reason
    {
        get => _reason;
        set { if (SetProperty(ref _reason, value)) MarkDirty(); }
    }

    private OrgUnitSupplementalDto _supplemental = new();
    public OrgUnitSupplementalDto Supplemental
    {
        get => _supplemental;
        set
        {
            if (SetProperty(ref _supplemental, value))
            {
                // DraftSaved commits here; live overlay draft no longer differs from Supplemental.
                _liveSupplementalDraft = null;
                MarkDirty();
            }
        }
    }

    // View passes the live overlay draft on every DraftChanged so dirty can see unsaved overlay edits
    // without committing Supplemental (still written only on DraftSaved).
    public void MarkSupplementalDirty(OrgUnitSupplementalDto liveDraft)
    {
        _liveSupplementalDraft = liveDraft;
        MarkDirty();
    }

    // Card 276 / backlog 3.58: discard-confirmed overlay close. Release the live cache (do not call
    // MarkSupplementalDirty — that would record a discard as an edit and leave the cache non-null),
    // recompute dirty against the mode-entry baseline, then refresh the revert-clean sentence.
    public void DiscardLiveSupplementalDraft()
    {
        if (_isLoading)
            return;

        _liveSupplementalDraft = null;
        RecomputeIsDirtyFromEntryBaseline();
        RefreshRevertCleanStatus();
    }

    // Real eligible-parent set from GetEligibleParentsAsync. The replace-parent gate reads this.
    public IReadOnlyList<OrgUnitPickerItem> ParentCandidates => ParentDecision.RealCandidates;

    // Display list bound by AstOrgUnitPicker.Items (cards 261/263). May carry the card's parent
    // and/or the live ParentId as extra rows when those ids are absent from ParentCandidates
    // (Branch B). The gate must NOT read this — only ParentCandidates.
    public IReadOnlyList<OrgUnitPickerItem> ParentPickerItems => ParentDecision.DisplayItems;

    // Captured once at BeginReplace. It is lifecycle input to decision construction, never a second
    // published surface fact.
    private OrgUnitPickerItem? _replaceCardParentItem;

    public bool IsParentLocked => ParentDecision.IsContextLocked;

    // Raised only after a successful save has cleared the card — not from Clear() itself
    // (LoadAsync calls Clear() as its first action).
    public event EventHandler? CardClearedAfterSave;

    private ParentDecision _parentDecision = ParentDecision.Create(
        new ParentDecisionKey(null, OrgUnitCardMode.ReadOnly, null, 0),
        ParentEligibilityState.Incomplete,
        [],
        [],
        null,
        null,
        string.Empty,
        ParentPresentationDisposition.Display,
        ParentCommitDisposition.NotApplicable);

    public ParentDecision ParentDecision => _parentDecision;
    public ParentEligibilityState ParentEligibility => ParentDecision.Phase;

    // Phase 4d: real tree/history data (sample placeholders removed in Task 3b; View binds these directly).
    private ObservableCollection<OrgUnitTreeNode> _treeRoots = [];
    public ObservableCollection<OrgUnitTreeNode> TreeRoots
    {
        get => _treeRoots;
        private set => SetProperty(ref _treeRoots, value);
    }

    private ObservableCollection<OrgUnitHistoryRow> _historyRows = [];
    public ObservableCollection<OrgUnitHistoryRow> HistoryRows
    {
        get => _historyRows;
        private set => SetProperty(ref _historyRows, value);
    }

    // Client-side History grid filter text. Filtering itself lives in the View (ICollectionView);
    // the VM only owns this string so Shell stays free of System.Windows.
    private string _historyFilterText = string.Empty;
    public string HistoryFilterText
    {
        get => _historyFilterText;
        set => SetProperty(ref _historyFilterText, value);
    }

    private VersionStatus _status = VersionStatus.None;
    public VersionStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(IsEffectivePeriodEnabled));
                SyncCloseDateStatusHint();
            }
        }
    }

    private bool _isRoot;
    public bool IsRoot
    {
        get => _isRoot;
        private set => SetProperty(ref _isRoot, value);
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool HasUnsavedInput => IsDirty;

    // Close-date gate may clear or overwrite only status it published (card 249 / F-246-01).
    private bool _publishingCloseGateStatus;
    private bool _closeGateOwnsStatus;

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value)
                && !_publishingCloseGateStatus
                && !_publishingRevertCleanStatus)
            {
                _closeGateOwnsStatus = false;
                _revertCleanOwnsStatus = false;
            }
        }
    }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity
    {
        get => _severity;
        private set
        {
            if (SetProperty(ref _severity, value)
                && !_publishingCloseGateStatus
                && !_publishingRevertCleanStatus)
            {
                _closeGateOwnsStatus = false;
                _revertCleanOwnsStatus = false;
            }
        }
    }

    // Captures EVERYTHING Begin*/Cancel must round-trip -- not just the editable form fields. Status/IsRoot
    // drive the button matrix and OrgUnitId is the card's identity; Clear() (called by ExecuteBeginAdd) wipes
    // all three, so Cancel-from-Add must restore them too or the card ends up showing record A's fields with
    // record A's Status/IsRoot/identity gone (button matrix and any later Save would silently disagree with
    // what's on screen).
    private readonly record struct FieldSnapshot(
        string OrgCode, string OrgNameFullVn, string OrgNameShortVn,
        DateOnly? EffectiveFrom, DateOnly? EffectiveTo, bool IsUndetermined, long? ParentId,
        VersionStatus Status, bool IsRoot, long? OrgUnitId, string Reason, long? VersionId,
        OrgUnitSupplementalDto Supplemental);

    private FieldSnapshot _snapshot;

    private FieldSnapshot CaptureSnapshot() =>
        new(OrgCode, OrgNameFullVn, OrgNameShortVn, EffectiveFrom, EffectiveTo, IsUndetermined, ParentId, Status, IsRoot, _orgUnitId, Reason, _currentVersionId, Supplemental);

    private void RestoreSnapshot(FieldSnapshot s)
    {
        var selectedParentItem = ResolveParentItem(s.ParentId, _replaceCardParentItem);
        _isLoading = true;
        _stagedParentId = ParentId;
        try
        {
            OrgCode = s.OrgCode;
            OrgNameFullVn = s.OrgNameFullVn;
            OrgNameShortVn = s.OrgNameShortVn;
            EffectiveFrom = s.EffectiveFrom;
            // AstEffectivePeriod only clears To when IsUndetermined becomes true; either assignment
            // order restores the same snapshot (IsUndetermined ⇒ To == null).
            EffectiveTo = s.EffectiveTo;
            IsUndetermined = s.IsUndetermined;
            ParentId = s.ParentId;
            Status = s.Status;
            IsRoot = s.IsRoot;
            _orgUnitId = s.OrgUnitId;
            Reason = s.Reason;
            _currentVersionId = s.VersionId;
            Supplemental = s.Supplemental;
        }
        finally
        {
            _isLoading = false;
            PublishInactiveParentDecision(selectedParentItem, useStagedParentId: true);
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }
    }

    private OrgUnitCardMode _mode = OrgUnitCardMode.ReadOnly;
    public OrgUnitCardMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value)
                return;

            _mode = value;
            AbandonParentCandidateQuery();

            // Publish the new mode's complete parent decision before any projection announces the
            // mode change. Every observer therefore reads one coherent snapshot.
            if (value is OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing)
                RecomputeParentEligibility();
            else
                PublishInactiveParentDecision();

            RaisePropertyChanged(nameof(Mode));
            RaisePropertyChanged(nameof(CanOpenSupplemental));
            RaisePropertyChanged(nameof(IsEffectivePeriodEnabled));
            RaisePropertyChanged(nameof(OffersRootParentOption));
            if (value == OrgUnitCardMode.Closing)
            {
                // Mode entry blanks To under _isLoading (skips OnCloseDateFieldEdited); still evaluate
                // Lưu enablement for CloseDateRequired without raising that sentence (earlier ruling).
                ApplyCloseDateCommitGate();
            }
            else
            {
                SyncCloseDateStatusHint();
                SetCloseDateCommitBlocked(false);
            }

            if (value is not OrgUnitCardMode.Replacing)
                _replaceCardParentItem = null;
        }
    }

    // AstEffectivePeriod.IsEnabled binding — single home for strip enablement (FR9).
    // On in Adding/Editing/Replacing/Closing; off in ReadOnly and Closing∧cancel-plan-branch. Literal
    // "enabled unless Closing∧cancel-plan" would wrongly enable ReadOnly — do not simplify that way.
    //
    // The cancel-plan-vs-retire branch is deliberately NOT `Status == VersionStatus.Pending` — that was
    // the pre-D1 defect (a same-day-effective version labels `Effective`, not `Pending`, yet the server
    // now cancels it too; see VersionCloseRules.Validate / D1). IsCloseCancelPlanBranch
    // consumes VersionCloseRules' own decision instead of re-deriving the `From >= today` comparison here.
    // Replacing enables the period for the same reason Editing already does today. The one surface
    // Replacing unlocks that Editing does not is the parent (card 238 / F-237-01) - not the period.
    public bool IsEffectivePeriodEnabled =>
        Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Editing or OrgUnitCardMode.Replacing
        || (Mode == OrgUnitCardMode.Closing && !IsCloseCancelPlanBranch());

    // Server-authoritative branch: consumes VersionCloseRules.BranchFor (the single home of the
    // Retire-vs-CancelPlan decision, D1 2026-08-10) instead of re-deriving `EffectiveFrom >= today`
    // here — that re-derivation is exactly the defect being fixed (the screen used to branch on the
    // STATUS LABEL, which diverges from the server's own boundary for a same-day-effective version).
    private bool IsCloseCancelPlanBranch() =>
        TryBuildSnapshotTargetPeriod(out var targetPeriod)
        && VersionCloseRules.BranchFor(_dates.Today, targetPeriod) == VersionCloseBranch.CancelPlan;

    // Closing blanks EffectiveTo on the form; the version's own end survives only in `_snapshot`.
    // Validate consults To — three of its rules do — so fabricated OpenEnd would be wrong on arrival.
    private bool TryBuildSnapshotTargetPeriod(out EffectivePeriod targetPeriod)
    {
        if (_snapshot.EffectiveFrom is not { } from)
        {
            targetPeriod = default;
            return false;
        }

        var to = _snapshot.IsUndetermined
            ? EffectivePeriod.OpenEnd
            : _snapshot.EffectiveTo ?? EffectivePeriod.OpenEnd;
        targetPeriod = new EffectivePeriod(from, to);
        return true;
    }

    // Closing cut-date explanation rides AstScreen's StatusMessage (Info) — never an in-card control that
    // would grow the declaration card and shift settled layout. The cancel-plan branch has no cut date, so
    // no hint. effective_to stays the inclusive last effective day; the wording states that convention.
    private string? BuildCloseDateEffectText()
    {
        if (Mode != OrgUnitCardMode.Closing)
            return null;
        if (IsCloseCancelPlanBranch())
            return null;
        if (IsUndetermined || EffectiveTo is null)
            return null;

        // effective_to (typed cut) = inclusive last effective day; cease-from = next calendar day.
        // Derivation lives in VersionCloseRules.CeaseFrom (single home — see its own comment for why).
        // CeaseFrom returns null for an open-ended EffectiveTo (no cessation day) — that null itself
        // carries the "suppress the hint" decision, so this method no longer re-checks
        // `EffectiveTo == EffectivePeriod.OpenEnd` separately.
        var endOn = EffectiveTo.Value;
        var ceaseFrom = VersionCloseRules.CeaseFrom(endOn);
        if (ceaseFrom is null)
            return null;

        var code = string.IsNullOrWhiteSpace(OrgCode) ? "—" : OrgCode.Trim();
        return
            $"Mã đơn vị {code} còn hiệu lực đến ngày {endOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}, chấm dứt hiệu lực từ ngày {ceaseFrom.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.";
    }

    // FR5: after a failed close, retyping the cut date must drop the stale Error so the hint can return
    // (same colour as Error under the locked red-for-non-success convention — no visual cue otherwise).
    private void OnCloseDateFieldEdited()
    {
        if (_isLoading)
            return;

        if (Mode == OrgUnitCardMode.Closing && Severity == StatusSeverity.Error)
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }

        SyncCloseDateStatusHint();
    }

    private void SyncCloseDateStatusHint()
    {
        if (Mode == OrgUnitCardMode.Closing)
        {
            ApplyCloseDateCommitGate();
            return;
        }

        var hint = BuildCloseDateEffectText();
        if (hint is not null)
        {
            // Never clobber Success/Warning — Error is cleared by OnCloseDateFieldEdited before this runs.
            if (Severity is StatusSeverity.None or StatusSeverity.Info)
            {
                StatusMessage = hint;
                Severity = StatusSeverity.Info;
            }

            return;
        }

        if (Severity == StatusSeverity.Info)
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }
    }

    // an earlier ruling: judge the close date at EffectiveTo commit via the whole of VersionCloseRules.Validate.
    // CloseDateRequired (empty To on retire, including mode entry) disables Lưu but raises no sentence.
    // Silence is not authorisation to delete — clear/overwrite only status this gate published (F-246-01).
    private void ApplyCloseDateCommitGate()
    {
        if (Mode != OrgUnitCardMode.Closing)
            return;

        if (!TryBuildSnapshotTargetPeriod(out var targetPeriod))
        {
            SetCloseDateCommitBlocked(true);
            return;
        }

        // FR6 / ExecuteSaveCloseAsync: a literal OpenEnd in the To box is CloseDateRequired wording,
        // not a Validate input — agree with Save exactly.
        if (EffectiveTo == EffectivePeriod.OpenEnd)
        {
            SetCloseDateCommitBlocked(true);
            PublishCloseGateStatus(CloseDateRequiredMessage, StatusSeverity.Error);
            return;
        }

        DateOnly? requestedCloseDate = IsUndetermined || EffectiveTo is null ? null : EffectiveTo;
        var validated = VersionCloseRules.Validate(_dates.Today, targetPeriod, requestedCloseDate);
        if (validated.IsError)
        {
            SetCloseDateCommitBlocked(true);
            var error = validated.FirstError;
            if (error.Code == VersionCloseRules.Codes.CloseDateRequired)
            {
                ClearCloseGateOwnedStatus();
                return;
            }

            PublishCloseGateStatus(FormatWriteError(error), StatusSeverity.Error);
            return;
        }

        SetCloseDateCommitBlocked(false);

        var hint = BuildCloseDateEffectText();
        if (hint is not null)
        {
            // None/Info: prior gate hint or empty. Error: only when this gate owns it (card 247 widened
            // Error into the overwrite set; ownership keeps an unrelated Error intact).
            if (Severity is StatusSeverity.None or StatusSeverity.Info
                || (Severity == StatusSeverity.Error && _closeGateOwnsStatus))
            {
                PublishCloseGateStatus(hint, StatusSeverity.Info);
            }

            return;
        }

        ClearCloseGateOwnedStatus();
    }

    private void PublishCloseGateStatus(string? message, StatusSeverity severity)
    {
        _publishingCloseGateStatus = true;
        try
        {
            StatusMessage = message;
            Severity = severity;
            _closeGateOwnsStatus = true;
            _revertCleanOwnsStatus = false;
        }
        finally
        {
            _publishingCloseGateStatus = false;
        }
    }

    private void ClearCloseGateOwnedStatus()
    {
        if (!_closeGateOwnsStatus)
            return;

        _publishingCloseGateStatus = true;
        try
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
            _closeGateOwnsStatus = false;
        }
        finally
        {
            _publishingCloseGateStatus = false;
        }
    }

    public bool CanAdd => Mode == OrgUnitCardMode.ReadOnly;

    public bool CanEdit => Mode == OrgUnitCardMode.ReadOnly && Status is VersionStatus.Effective or VersionStatus.Pending;

    // A root is closable ONLY by a break-glass rescuer (earlier ruling). This is
    // the button-disabling affordance, NOT the guard -- CloseOrgUnitDeclarationAsync re-reads the parent
    // under its own lock and refuses with OrgUnit.RootNotClosable regardless of what this property says.
    // Without this clause the service-side carve-out would be UNREACHABLE from the UI: this is the one
    // button that reaches the service, and the service derives close-vs-cancel itself, so a bare !IsRoot
    // blocks the cancel path too.
    //
    // DELIBERATELY UNLIKE CanReplace (card 259 / requester ruling 2026-09-06): a mis-declared root must
    // keep an in-app remedy, and that remedy is Đóng (break-glass), not Thay thế. Do not "restore"
    // symmetry with CanReplace — the asymmetry is the ruling.
    //
    // No ObservesProperty for break-glass membership: it comes from the signed §⑤ admin list and cannot
    // change within a session, so there is nothing to raise a change for. IsRoot IS observed (see
    // BeginCloseCommand) because loading a different card changes it.
    public bool CanClose =>
        Mode == OrgUnitCardMode.ReadOnly
        && (!IsRoot || _breakGlass.IsBreakGlassAdmin(_currentUser.Username ?? "unknown"))
        && Status is VersionStatus.Effective or VersionStatus.Pending;

    // Predecessor-is-root half of OrgUnit.RootNotReplaceable (card 238 / F-237-02 / F-237-03) — NOT CanEdit.
    // The successor-as-root half is the picker affordance OffersRootParentOption, not CanSave.
    //
    // DELIBERATELY UNLIKE CanClose (card 259 / requester ruling 2026-09-06): the root may only be
    // re-declared via Đóng cái cũ → tạo cái mới. Thay thế is never a back door — no break-glass carve-out
    // here. Do not copy CanClose's (!IsRoot || IsBreakGlassAdmin(...)) shape back onto this property.
    public bool CanReplace =>
        Mode == OrgUnitCardMode.ReadOnly
        && !IsRoot
        && Status is VersionStatus.Effective or VersionStatus.Pending;

    public bool CanCancel => Mode != OrgUnitCardMode.ReadOnly;

    // Save observes this projection. Parent readiness and close-date readiness remain different
    // domains, but each has one owner and the command consumes their combined fail-closed result.
    // HasUnsavedInput is the requester's 2026-09-06 dirty term (backlog 3.54): mode entry alone
    // must not light Lưu; MarkDirty stays suppressed while _isLoading (see ExecuteBeginClose).
    public bool CanSave =>
        Mode != OrgUnitCardMode.ReadOnly && !PeriodCommitBlocked && HasUnsavedInput;

    private bool _closeDateCommitBlocked;
    public bool PeriodCommitBlocked => _closeDateCommitBlocked || ParentDecisionBlocksCurrentCommit();

    private bool ParentDecisionBlocksCurrentCommit()
    {
        if (Mode is not (OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing))
            return false;

        var decision = ParentDecision;
        return !decision.AllowsCommit
            || decision.Key.OrgUnitId != _orgUnitId
            || decision.Key.Mode != Mode
            || decision.Key.Period != TryBuildFormPeriod();
    }

    private void SetCloseDateCommitBlocked(bool value)
    {
        if (_closeDateCommitBlocked == value)
            return;

        _closeDateCommitBlocked = value;
        RaisePropertyChanged(nameof(PeriodCommitBlocked));
        RaisePropertyChanged(nameof(CanSave));
    }

    // Supplemental affordance: always visible; enabled per settled matrix (Closing = view-only open).
    public bool CanOpenSupplemental => Mode switch
    {
        OrgUnitCardMode.Closing => true,
        OrgUnitCardMode.ReadOnly => _orgUnitId is not null,
        OrgUnitCardMode.Adding or OrgUnitCardMode.Editing or OrgUnitCardMode.Replacing => HasRequiredIdentityFields(),
        _ => false,
    };

    // Affordance for RootNotReplaceable's successor half (card 238 / 259): only Adding may land on the
    // empty-candidate root path (RootParentDisplayLabel). Replacing never offers it — not even to
    // break-glass (requester ruling 2026-09-06: re-declare the root via Đóng → tạo mới, not Thay thế).
    // That also makes !OffersRootParentOption always true in Replacing, so the 3.41/257 parent-period
    // gate applies to every actor there (earlier ruling).
    public bool OffersRootParentOption => Mode == OrgUnitCardMode.Adding;

    // Settled display label for a root's parent field (card 259). Not a message — inventory home is
    // the operator-message rules; do not lengthen back to "Đơn vị gốc (không có cha)".
    public const string RootParentDisplayLabel = "Đơn vị gốc";

    // an earlier ruling: single home for the replace-parent-period gate. Both SyncReplaceParentPeriodGate and
    // RefreshParentSurface read this — do not re-express the predicate in the view code-behind.
    // Card 261 Part 3: the former `ParentId is null ||` disjunct was redundant — long Id != null long?
    // is true for every row, and All is vacuously true on an empty list — so only the All remains.
    // A non-empty list that omits ParentId is still the discriminating 3.41 / Branch B state.
    public bool IsReplaceParentAbsentFromCandidates =>
        Mode == OrgUnitCardMode.Replacing
        && ParentDecision.CommitDisposition is ParentCommitDisposition.BlockedNoEligibleParent
            or ParentCommitDisposition.BlockedParentMismatch;

    public DelegateCommand BeginAddCommand { get; }
    public DelegateCommand BeginEditCommand { get; }
    public DelegateCommand BeginReplaceCommand { get; }
    public DelegateCommand BeginCloseCommand { get; }
    public AsyncDelegateCommand CancelCommand { get; }
    public AsyncDelegateCommand SaveCommand { get; }

    // Captured from the loaded card the instant BeginAdd runs, BEFORE Clear() wipes it -- this is how
    // Screen A remembers "the tree node that was selected" (N3/N7) across the Add flow; the real
    // tree-node-click (OrgUnitDeclarationView.CommitTreeSelectionAsync, reached through
    // TreeSelectionGate) calls LoadAsync exactly like today's tests do.
    private (long ParentId, EffectivePeriod Coverage, OrgUnitPickerItem Item)? _addParentContext;

    private void ExecuteBeginAdd()
    {
        InvalidateInFlightCardLoad();
        _snapshot = CaptureSnapshot();
        _addParentContext = _orgUnitId is { } loadedId
            ? (loadedId,
                new EffectivePeriod(EffectiveFrom ?? _dates.Today, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo ?? EffectivePeriod.OpenEnd),
                new OrgUnitPickerItem(loadedId, OrgUnitPickerItem.FormatDisplay(OrgCode, OrgNameShortVn)))
            : null;
        Clear();
        Mode = OrgUnitCardMode.Adding;
        // After Clear + mode init (parent context may pre-fill); not _snapshot (prior card).
        CaptureEntryDirtyBaseline();
    }

    private EffectivePeriod? TryBuildFormPeriod()
    {
        if (EffectiveFrom is null)
        {
            return null;
        }

        if (!IsUndetermined && EffectiveTo is null)
        {
            return null;
        }

        return new EffectivePeriod(EffectiveFrom.Value, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo!.Value);
    }

    // N3/N7: pre-fill+lock the parent ONLY while the tree-context node's coverage still covers the (possibly
    // still-incomplete) form EP; the instant it does not, unlock and switch to the N2 picker. Fires from the
    // EffectiveFrom/EffectiveTo/IsUndetermined setters above, so this re-evaluates on every edit, not just once.
    private void RecomputeParentEligibility()
    {
        if (_isLoading)
            return;

        // F-237-04: Replacing unlocks the parent the same way unlocked Add does. Editing keeps parent
        // Display-locked; that is the one surface Replacing opens that Editing does not (card 238).
        if (Mode is not (OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing))
        {
            return;
        }

        var formPeriod = TryBuildFormPeriod();

        if (Mode == OrgUnitCardMode.Adding
            && _addParentContext is { } ctx
            && (formPeriod is null || !CoverageGap.TryFind([ctx.Coverage], formPeriod.Value, out _)))
        {
            var key = NewParentDecisionKey(formPeriod);
            PublishParentDecision(BuildParentDecision(
                key,
                formPeriod is null ? ParentEligibilityState.Incomplete : ParentEligibilityState.Resolved,
                [],
                ctx.ParentId,
                ctx.Item,
                isContextLocked: true));
            SyncReplaceParentPeriodGate();
            return;
        }

        var parentId = ParentId;
        var selectedItem = ParentDecision.SelectedParentItem;
        if (Mode == OrgUnitCardMode.Adding && parentId == _addParentContext?.ParentId)
        {
            // Was locked to the tree-context candidate; the EP just typed no longer qualifies it -- clear the
            // stale pre-fill rather than leaving a picker-less selection standing.
            parentId = null;
            selectedItem = null;
        }

        if (formPeriod is null)
        {
            var key = NewParentDecisionKey(null);
            PublishParentDecision(BuildParentDecision(
                key,
                ParentEligibilityState.Incomplete,
                [],
                parentId,
                selectedItem));
            SyncReplaceParentPeriodGate();
        }
        else
        {
            StartParentCandidateQuery(formPeriod.Value, parentId, selectedItem);
        }
    }

    // Guards the fire-and-forget refresh below: a real GetEligibleParentsAsync call genuinely awaits I/O, so
    // rapid successive EP edits can start several overlapping calls whose completions may arrive out of
    // order. Each call captures its own generation number at dispatch time; only the result whose generation
    // still matches the current one (i.e. no newer edit has fired since) is allowed to write ParentCandidates.
    private int _parentRefreshGeneration;

    private void AbandonParentCandidateQuery() => _parentRefreshGeneration++;

    private ParentDecisionKey NewParentDecisionKey(EffectivePeriod? period) =>
        new(_orgUnitId, Mode, period, ++_parentRefreshGeneration);

    private void StartParentCandidateQuery(
        EffectivePeriod formPeriod,
        long? parentId,
        OrgUnitPickerItem? selectedItem)
    {
        var key = NewParentDecisionKey(formPeriod);
        Task<IReadOnlyList<OrgUnitPickerItem>> query;
        try
        {
            // Reads stay Global by policy (decision-log 2026-08-05, "Scope-checked writes" part 2): the
            // parent picker must offer every eligible parent regardless of the operator's own scope --
            // only the eventual write (Add/Edit/Close/Replace) is gated by the caller's resolved scope.
            var scope = new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown");
            query = _orgUnits.GetEligibleParentsAsync(
                scope, formPeriod, key.Mode == OrgUnitCardMode.Replacing ? key.OrgUnitId : null);
        }
        catch (Exception)
        {
            PublishParentCandidateFailure(key, parentId, selectedItem);
            return;
        }

        // Task.FromResult has no observable pending interval. Publish Resolved directly so old unit tests do
        // not depend on a synthetic transient; a real/delayed task publishes Loading before this method returns.
        if (query.IsCompletedSuccessfully)
        {
            PublishResolvedParentCandidates(key, query.Result, parentId, selectedItem);
            return;
        }

        selectedItem = ResolveParentItem(parentId, selectedItem);
        PublishParentDecision(BuildParentDecision(
            key,
            ParentEligibilityState.Loading,
            [],
            parentId,
            selectedItem));
        SyncReplaceParentPeriodGate();
        _ = CompleteParentCandidateQueryAsync(query, key, parentId, selectedItem);
    }

    private async Task CompleteParentCandidateQueryAsync(
        Task<IReadOnlyList<OrgUnitPickerItem>> query,
        ParentDecisionKey key,
        long? parentId,
        OrgUnitPickerItem? selectedItem)
    {
        try
        {
            var candidates = await query;
            PublishResolvedParentCandidates(key, candidates, parentId, selectedItem);
        }
        catch (Exception)
        {
            PublishParentCandidateFailure(key, parentId, selectedItem);
        }
    }

    private void PublishResolvedParentCandidates(
        ParentDecisionKey key,
        IReadOnlyList<OrgUnitPickerItem> candidates,
        long? parentId,
        OrgUnitPickerItem? selectedItem)
    {
        if (!IsCurrentParentRequest(key))
            return;

        selectedItem = ResolveParentItem(parentId, candidates.FirstOrDefault(item => item.Id == parentId) ?? selectedItem);
        if (_snapshot.ParentId is { } cardParentId)
            _replaceCardParentItem = ResolveParentItem(
                cardParentId,
                candidates.FirstOrDefault(item => item.Id == cardParentId) ?? _replaceCardParentItem);

        PublishParentDecision(BuildParentDecision(
            key,
            ParentEligibilityState.Resolved,
            candidates,
            parentId,
            selectedItem));
        SyncReplaceParentPeriodGate();
    }

    private void PublishParentCandidateFailure(
        ParentDecisionKey key,
        long? parentId,
        OrgUnitPickerItem? selectedItem)
    {
        if (!IsCurrentParentRequest(key))
            return;

        selectedItem = ResolveParentItem(parentId, selectedItem);
        PublishParentDecision(BuildParentDecision(
            key,
            ParentEligibilityState.Failed,
            [],
            parentId,
            selectedItem));
        StatusMessage = "Ứng dụng không tải được danh sách đơn vị cha.";
        Severity = StatusSeverity.Error;
    }

    private bool IsCurrentParentRequest(ParentDecisionKey key) =>
        key.Generation == _parentRefreshGeneration
        && key.OrgUnitId == _orgUnitId
        && key.Mode == Mode
        && key.Period == TryBuildFormPeriod();

    // Backlog 3.38: empty eligible-parent list in Replacing (ordinary actor) is a screen-visible state —
    // not a service error code. Sentence is requester verbatim (§1.8a); no code behind it.
    private void SyncReplaceParentPeriodGate()
    {
        if (Mode != OrgUnitCardMode.Replacing)
            return;

        if (IsReplaceParentAbsentFromCandidates)
        {
            StatusMessage = ParentDecision.CommitDisposition == ParentCommitDisposition.BlockedNoEligibleParent
                ? ReplacePeriodNoEligibleParentMessage
                : ReplacePeriodParentCoverageMismatchMessage;
            Severity = StatusSeverity.Error;
            return;
        }

        if (Severity == StatusSeverity.Error
            && IsReplaceParentPeriodGateMessage(StatusMessage))
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }

    }

    private void ExecuteBeginEdit()
    {
        InvalidateInFlightCardLoad();
        _snapshot = CaptureSnapshot();
        Mode = OrgUnitCardMode.Editing;
        CaptureEntryDirtyBaseline();
    }

    private void ExecuteBeginReplace()
    {
        InvalidateInFlightCardLoad();
        // Keep the current values on the card — do not Clear(). RecomputeParentEligibility unlocks the
        // parent picker; period/code/names are already editable in Editing today, so Replacing's
        // distinctive unlock against Editing is the parent only (card 238 / F-237-01).
        _snapshot = CaptureSnapshot();
        _replaceCardParentItem = ResolveParentItem(_snapshot.ParentId, ParentDecision.SelectedParentItem);
        Mode = OrgUnitCardMode.Replacing;
        CaptureEntryDirtyBaseline();
    }

    private void ExecuteBeginClose()
    {
        InvalidateInFlightCardLoad();
        _snapshot = CaptureSnapshot();
        // Close always needs a concrete end date — clear open-end so the EP To box is editable.
        // Suppress dirty-marking: these assignments are mode-entry defaults, not operator edits.
        _isLoading = true;
        try
        {
            IsUndetermined = false;
            EffectiveTo = null;
        }
        finally
        {
            _isLoading = false;
        }
        Mode = OrgUnitCardMode.Closing;
        // After open-end → blank To; baseline is that blank, not _snapshot's open end.
        CaptureEntryDirtyBaseline();
    }

    private async Task ExecuteCancelAsync()
    {
        // _snapshot was captured by the Begin* that entered mutating mode -- for Add it is whatever the card
        // showed BEFORE the blank new-entry form (e.g. the previously selected node, or nothing), so restoring
        // it is correct for ALL mutating modes -- Add, Edit, Close and Replace -- not just Edit/Close.
        // Cancel restores in-memory fields only (no write) — do not hit the DB for a tree/history refresh (FR6).
        var leftClosing = Mode == OrgUnitCardMode.Closing;
        Mode = OrgUnitCardMode.ReadOnly;
        RestoreSnapshot(_snapshot);
        ReleaseEntryDirtyBaseline();
        IsDirty = false;
        // FR13: a failed-close Error would otherwise stay on the read-only card (hint sync only clears Info).
        if (leftClosing && Severity == StatusSeverity.Error)
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }

        await Task.CompletedTask;
    }

    public async Task<CardLoadOutcome> LoadAsync(
        long orgUnitId,
        DateOnly asOf,
        OrgUnitPickerItem? knownParentItem = null)
    {
        // Bump before Clear/await so two clean-form clicks cannot let a slower GetByIdentityAsync
        // overwrite the card (or its error banner) after a newer load already owns it — same idiom
        // as _treeLoadGeneration / _historyLoadGeneration.
        var generation = ++_cardLoadGeneration;
        Clear();

        var result = await _orgUnits.GetByIdentityAsync(orgUnitId, asOf);
        if (generation != _cardLoadGeneration)
            return CardLoadOutcome.Superseded;   // a newer load already owns the card -- never write, not even the error banner

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatLoadError));
            Severity = StatusSeverity.Error;
            return CardLoadOutcome.Failed;
        }

        var dto = result.Value;
        var loadedParentItem = dto.ParentId is { } parentId
            && knownParentItem is not null
            && knownParentItem.Id == parentId
            && !string.IsNullOrWhiteSpace(knownParentItem.Display)
                ? knownParentItem
                : ResolveLoadedParentItem(dto);
        _isLoading = true;
        _stagedParentId = ParentId;
        try
        {
            _orgUnitId = dto.OrgUnitId;
            _currentVersionId = dto.Id;
            OrgCode = dto.OrgCode;
            OrgNameFullVn = dto.OrgNameFullVn;
            OrgNameShortVn = dto.OrgNameShortVn;
            EffectiveFrom = dto.EffectiveFrom;
            IsUndetermined = dto.EffectiveTo == EffectivePeriod.OpenEnd;
            EffectiveTo = IsUndetermined ? null : dto.EffectiveTo;
            ParentId = dto.ParentId;
            IsRoot = dto.ParentId is null;
            Status = VersionStatusResolver.Resolve(dto.IsActive, dto.Status, dto.EffectiveFrom, dto.EffectiveTo, _dates.Today);
            Reason = string.Empty;
            Supplemental = dto.Supplemental;
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }
        finally
        {
            _isLoading = false;
            PublishInactiveParentDecision(loadedParentItem, useStagedParentId: true);
            IsDirty = false;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }

        return CardLoadOutcome.Loaded;
    }

    // §B (2026-08-10): LoadAsync's error path reaches EffectivePeriodResolver.NoCoverage, whose message
    // is entity-agnostic shared code (names the C# TVersion type, calls it a "Tham số") -- not fit for an
    // operator screen. Mapped here, the screen-appropriate place to speak the operator's language, same
    // pattern as FormatWriteError. Do not change EffectivePeriodResolver's own message for this (other
    // callers share it).
    private static string FormatLoadError(Error error) => error.Code switch
    {
        "EffectivePeriod.NoCoverage" =>
            "Đơn vị không hiệu lực tại ngày đã chọn.",
        "EffectivePeriod.OverlappingVersions" =>
            "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
        // Authz.* cannot reach this map: callers are GetByIdentityAsync → resolver only (brief 162).
        _ => "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
    };

    // §A (2026-08-10): History "Xem" is a ROW-IDENTIFIED read ("show THIS version"), not a date-resolved
    // one ("show whatever is effective on date X") -- LoadAsync's GetByIdentityAsync/EffectivePeriodResolver
    // route requires SOME version to cover a date, which a lapsed identity (closed, no coverage as of
    // today) can never satisfy (defect A). A history row already carries everything the card shows
    // (extended 2026-08-10 for exactly this), so this populates the card directly from it -- no repository
    // call, so no coverage requirement, so a lapsed identity's history is always viewable.
    //
    // Read-only enforcement: Mode is left at ReadOnly by Clear() below, same as any other view. CanEdit/
    // CanClose are gated on Status (`Mode == ReadOnly && Status is Effective or Pending`) -- and Status here
    // is the row's OWN computed status (MapHistoryRow, via VersionStatusResolver on that row's own
    // isactive/status/dates). This is NOT restricted to Expired/Cancelled/Replaced rows: a future-dated PLAN row is
    // isactive=1, is not the version the card resolves at today, yet still resolves to Pending here -- and
    // CanEdit/CanClose being reachable for it is intentional (cancelling a pending plan from its own history
    // row is a real operation, same as reaching it via the tree). What this path guarantees is narrower:
    // whatever Status the row resolves to is that row's OWN true status as of today, computed the same way
    // LoadAsync computes it for a tree-driven load -- so CanEdit/CanClose reflect real edit/close eligibility
    // for THIS specific version, not a stale or borrowed one.
    public void LoadFromHistoryRow(OrgUnitHistoryRow row)
    {
        // Bump so a slower in-flight LoadAsync (e.g. a tree click racing this) cannot overwrite this
        // synchronous load after it lands -- same generation idiom LoadAsync uses.
        ++_cardLoadGeneration;
        Clear();

        var loadedParentItem = row.ParentId is { } parentId && !string.IsNullOrWhiteSpace(row.ParentLabel)
            ? new OrgUnitPickerItem(parentId, row.ParentLabel)
            : ResolveParentItem(row.ParentId);
        _isLoading = true;
        _stagedParentId = ParentId;
        try
        {
            _orgUnitId = row.OrgUnitId;
            _currentVersionId = row.Id;
            OrgCode = row.OrgCode;
            OrgNameFullVn = row.NameFull;
            OrgNameShortVn = row.NameShort;
            EffectiveFrom = row.EffectiveFrom;
            IsUndetermined = row.EffectiveTo == EffectivePeriod.OpenEnd;
            EffectiveTo = IsUndetermined ? null : row.EffectiveTo;
            ParentId = row.ParentId;
            IsRoot = row.ParentId is null;
            Status = row.Status;
            Reason = string.Empty;
            Supplemental = row.Supplemental;
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }
        finally
        {
            _isLoading = false;
            PublishInactiveParentDecision(loadedParentItem, useStagedParentId: true);
            IsDirty = false;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }
    }

    private Task<ErrorOr<DataScope>> ResolveScopeAsync() =>
        _authorization.AuthorizeAsync(_currentUser.Username ?? "unknown", FunctionKey);

    // Builds a parent/child hierarchy from GetInScopeAsync's flat, scope-filtered result -- a unit whose
    // ParentId does not resolve within that same result set (root, or an out-of-scope/not-yet-effective
    // parent) becomes a root node rather than silently disappearing from the tree. Replaces TreeRoots'
    // contents wholesale. Scope comes from AuthorizeAsync (not a hardcoded Global).
    public async Task LoadTreeAsync(DateOnly asOf)
    {
        var generation = ++_treeLoadGeneration;
        try
        {
            var scopeResult = await ResolveScopeAsync();
            if (scopeResult.IsError)
            {
                // FR3: fail closed — never leave a stale as-of behind after an auth/scope failure.
                _lastTreeAsOf = null;
                StatusMessage = "Ứng dụng không tải được cây đơn vị.";
                Severity = StatusSeverity.Error;
                return;
            }

            await LoadTreeCoreAsync(scopeResult.Value, asOf, generation);
        }
        catch (Exception)
        {
            if (generation == _treeLoadGeneration)
            {
                _lastTreeAsOf = null;
                StatusMessage = "Ứng dụng không tải được cây đơn vị.";
                Severity = StatusSeverity.Error;
            }
        }
    }

    // Real tree build (dup-guard + cycle cut at attach + cache). Caller supplies an already-resolved scope.
    // generation == null means "unconditional write" (post-save self-refresh via RefreshTreeAndHistoryAsync).
    private async Task LoadTreeCoreAsync(DataScope scope, DateOnly asOf, int? generation = null)
    {
        var units = await _orgUnits.GetInScopeAsync(scope, asOf);

        // Keep-first on duplicate OrgUnitId (app-enforced invariant, not DB-enforced) — never throw.
        var uniqueUnits = units.GroupBy(u => u.OrgUnitId).Select(g => g.First()).ToList();
        var parentById = uniqueUnits.ToDictionary(u => u.OrgUnitId, u => u.ParentId);
        var nodesById = uniqueUnits.ToDictionary(
            u => u.OrgUnitId,
            u => new OrgUnitTreeNode(
                u.OrgUnitId,
                OrgUnitPickerItem.FormatDisplay(u.OrgCode, u.OrgNameShortVn)) { IsExpanded = true });
        var roots = new List<OrgUnitTreeNode>();

        foreach (var unit in uniqueUnits)
        {
            var node = nodesById[unit.OrgUnitId];
            // FR2: cut cyclic edges at attach time — walk the declared parent chain; if we revisit this
            // unit, attaching would leave a self-descendant and crash HierarchicalDataTemplate.
            if (unit.ParentId is { } parentId
                && nodesById.TryGetValue(parentId, out var parentNode)
                && !ParentChainContains(parentById, parentId, unit.OrgUnitId, uniqueUnits.Count))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        if (generation is { } g && g != _treeLoadGeneration)
            return;

        _lastTreeAsOf = asOf;
        TreeRoots = new ObservableCollection<OrgUnitTreeNode>(roots);
    }

    // True when walking ParentId links from `startParentId` upward revisits `originId` within `maxHops`.
    private static bool ParentChainContains(
        IReadOnlyDictionary<long, long?> parentById, long startParentId, long originId, int maxHops)
    {
        var current = startParentId;
        for (var hops = 0; hops < maxHops; hops++)
        {
            if (current == originId)
                return true;
            if (!parentById.TryGetValue(current, out var next) || next is not { } nextId)
                return false;
            current = nextId;
        }

        return false;
    }

    // Maps GetHistoryInScopeAsync's full timeline (already ordered RecordedAt descending) onto
    // the history-grid row shape 1:1 -- parent-as-of and operation-kind are both pre-resolved upstream (the
    // repository's JOIN / the write call sites), this method only formats them for display. Replaces
    // HistoryRows' contents wholesale. Scope is enforced SERVER-side by the repository predicate: an
    // out-of-scope id simply returns no rows. There is deliberately no client-side membership gate --
    // a unit that is closed today is absent from today's tree yet its history must stay visible (spec 2.7.6).
    public async Task LoadAllHistoryAsync()
    {
        var generation = ++_historyLoadGeneration;
        try
        {
            await LoadHistoryCoreAsync(null, generation);
        }
        catch (Exception)
        {
            if (generation == _historyLoadGeneration)
            {
                StatusMessage = "Ứng dụng không tải được dữ liệu lịch sử.";
                Severity = StatusSeverity.Error;
            }
        }
    }

    public async Task RefreshHistoryAsync() => await LoadAllHistoryAsync();

    private async Task LoadHistoryCoreAsync(long? orgUnitId, int? generation = null)
    {
        // Reads stay Global by policy (decision-log 2026-08-05, "Scope-checked writes" part 2): only
        // WRITES (Add/Edit/Close/Replace) are gated by the caller's resolved scope -- history is a read-only
        // audit trail and is deliberately shown system-wide regardless of who is viewing it.
        var scope = new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown");
        var versions = await _orgUnits.GetHistoryInScopeAsync(scope, orgUnitId);
        if (generation is { } g && g != _historyLoadGeneration)
            return;

        HistoryRows = new ObservableCollection<OrgUnitHistoryRow>(versions.Select(MapHistoryRow));
    }

    private OrgUnitHistoryRow MapHistoryRow(OrgUnitVersionDto dto)
    {
        // Never guess a label from a null AS-OF field (prefer a clear absence over a misleading value):
        // both null means "no parent (root) as of this row" or "the parent has no version covering that
        // date" (see OrgUnitVersionDto's own doc comment) -- either way there is nothing to show.
        var parentLabel = OrgUnitPickerItem.FormatDisplay(
            dto.ParentOrgCodeAsOf,
            dto.ParentOrgNameShortVnAsOf);

        var status = VersionStatusResolver.Resolve(dto.IsActive, dto.Status, dto.EffectiveFrom, dto.EffectiveTo, _dates.Today);

        return new OrgUnitHistoryRow(
            Id: dto.Id,
            OrgUnitId: dto.OrgUnitId,
            EffectiveFrom: dto.EffectiveFrom,
            EffectiveTo: dto.EffectiveTo,
            FromText: FormatDate(dto.EffectiveFrom),
            ToText: FormatDate(dto.EffectiveTo),
            RecordedAtText: dto.RecordedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            StatusText: VersionStatusPresentation.DisplayText(status),
            Status: status,
            OrgCode: dto.OrgCode,
            NameFull: dto.OrgNameFullVn,
            NameShort: dto.OrgNameShortVn,
            ParentId: dto.ParentId,
            ParentLabel: parentLabel,
            // dto.OperationKind is null only for theoretical pre-4d rows (see OrgUnitVersionDto's doc comment)
            // -- map that to an empty label rather than defaulting to a specific kind that never happened.
            Operation: dto.OperationKind is { } kind ? VersionOperationKindPresentation.ToVietnameseText(kind) : string.Empty,
            RecordedBy: dto.RecordedBy,
            Reason: dto.Reason ?? string.Empty,
            Supplemental: dto.Supplemental);
    }

    // Best-effort UI refresh after a successful write. A thrown refresh failure OR a soft
    // ResolveScopeAsync IsError (no throw) both set refreshFailed so finally REPLACES Save/Close's
    // Success banner with a Warning — a stale tree is never silently hidden behind "Đã lưu."
    // (thrown path 2026-08-05; soft scope path closes FR1.)
    private async Task RefreshTreeAndHistoryAsync(long? orgUnitId)
    {
        var preservedMessage = StatusMessage;
        var preservedSeverity = Severity;
        var refreshFailed = false;
        try
        {
            var scope = await ResolveScopeAsync();
            if (!scope.IsError)
                await LoadTreeCoreAsync(scope.Value, _lastTreeAsOf ?? _dates.Today);
            else
                refreshFailed = true;

            if (orgUnitId is { })
            {
                // Bump the generation guard directly (rather than delegating to LoadAllHistoryAsync, which
                // would swallow a failure into its own banner and short-circuit this method's own refreshFailed
                // handling below) -- otherwise an older in-flight full-history load (or a refresh click that
                // arrived before this save-driven reload) keeps a generation number that still matches
                // _historyLoadGeneration and can win the race and overwrite these fresh rows.
                var historyGeneration = ++_historyLoadGeneration;
                await LoadHistoryCoreAsync(null, historyGeneration); // bypass membership gate: reload the full
                                                                      // audit trail (brief 049)
            }
        }
        catch (Exception)
        {
            // AST.Shell has no Serilog reference (Scope forbids adding one); swallow — surface via banner in finally.
            refreshFailed = true;
        }
        finally
        {
            if (refreshFailed)
            {
                StatusMessage = "Đã lưu. Dữ liệu hiển thị chưa cập nhật.";
                Severity = StatusSeverity.Warning;
            }
            else
            {
                StatusMessage = preservedMessage;
                Severity = preservedSeverity;
            }
        }
    }

    public void Clear()
    {
        AbandonParentCandidateQuery();
        _isLoading = true;
        _stagedParentId = ParentId;
        try
        {
            _orgUnitId = null;
            _currentVersionId = null;
            OrgCode = string.Empty;
            OrgNameFullVn = string.Empty;
            OrgNameShortVn = string.Empty;
            EffectiveFrom = null;
            EffectiveTo = null;
            IsUndetermined = false;
            ParentId = null;
            IsRoot = false;
            Status = VersionStatus.None;
            Reason = string.Empty;
            Supplemental = new();
            StatusMessage = null;
            Severity = StatusSeverity.None;
            Mode = OrgUnitCardMode.ReadOnly;
        }
        finally
        {
            _isLoading = false;
            PublishInactiveParentDecision(useStagedParentId: true);
            ReleaseEntryDirtyBaseline();
            IsDirty = false;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }
    }

    private ParentDecision BuildParentDecision(
        ParentDecisionKey key,
        ParentEligibilityState phase,
        IReadOnlyList<OrgUnitPickerItem> realCandidates,
        long? parentId,
        OrgUnitPickerItem? selectedItem,
        bool isContextLocked = false)
    {
        selectedItem = ResolveParentItem(parentId, selectedItem);

        var displayItems = realCandidates.ToList();
        static void AddIfAbsent(List<OrgUnitPickerItem> items, OrgUnitPickerItem? item)
        {
            if (item is not null && items.All(candidate => candidate.Id != item.Id))
                items.Add(item);
        }

        // Cards 261/263: the real set remains gate input; the display set additionally retains the
        // card parent and the different live selection. Keeping the live row even in Branch A/pending
        // also prevents SelectedValue's TwoWay binding from manufacturing a null selection.
        if (key.Mode == OrgUnitCardMode.Replacing)
            AddIfAbsent(displayItems, _replaceCardParentItem);
        AddIfAbsent(displayItems, selectedItem);
        selectedItem = parentId is { } selectedParentId
            ? displayItems.FirstOrDefault(item => item.Id == selectedParentId)
            : null;

        var presentation = ParentPresentationDisposition.Display;
        var commit = ParentCommitDisposition.NotApplicable;

        if (key.Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Replacing)
        {
            commit = phase switch
            {
                ParentEligibilityState.Incomplete => ParentCommitDisposition.BlockedIncomplete,
                ParentEligibilityState.Loading => ParentCommitDisposition.BlockedLoading,
                ParentEligibilityState.Failed => ParentCommitDisposition.BlockedFailed,
                _ => ParentCommitDisposition.Allowed,
            };

            if (phase == ParentEligibilityState.Resolved)
            {
                if (key.Mode == OrgUnitCardMode.Adding)
                {
                    presentation = isContextLocked || (realCandidates.Count == 0 && parentId is null)
                        ? ParentPresentationDisposition.Display
                        : ParentPresentationDisposition.Editable;
                    if (!isContextLocked && realCandidates.Count > 0 && parentId is null)
                        commit = ParentCommitDisposition.BlockedSelectionRequired;
                    else if (parentId is { } addParentId
                        && !isContextLocked
                        && realCandidates.All(candidate => candidate.Id != addParentId))
                        commit = ParentCommitDisposition.BlockedSelectionRequired;
                }
                else if (realCandidates.Count == 0)
                {
                    commit = ParentCommitDisposition.BlockedNoEligibleParent;
                }
                else
                {
                    presentation = ParentPresentationDisposition.Editable;
                    if (parentId is null || realCandidates.All(candidate => candidate.Id != parentId))
                        commit = ParentCommitDisposition.BlockedParentMismatch;
                }
            }
        }

        var displayText = selectedItem?.Display
            ?? ((key.Mode == OrgUnitCardMode.Adding
                    && phase == ParentEligibilityState.Resolved
                    && realCandidates.Count == 0)
                || (key.Mode == OrgUnitCardMode.ReadOnly && IsRoot)
                ? RootParentDisplayLabel
                : string.Empty);

        return ParentDecision.Create(
            key,
            phase,
            realCandidates,
            displayItems,
            parentId,
            selectedItem,
            displayText,
            presentation,
            commit,
            isContextLocked);
    }

    private void PublishInactiveParentDecision(
        OrgUnitPickerItem? selectedItem = null,
        bool useStagedParentId = false)
    {
        var parentId = _isLoading || useStagedParentId ? _stagedParentId : ParentDecision.ParentId;
        selectedItem = ResolveParentItem(parentId, selectedItem);
        PublishParentDecision(BuildParentDecision(
            new ParentDecisionKey(_orgUnitId, Mode, TryBuildFormPeriod(), _parentRefreshGeneration),
            ParentEligibilityState.Incomplete,
            [],
            parentId,
            selectedItem));
    }

    private void PublishParentDecision(ParentDecision decision)
    {
        _parentDecision = decision;
        _stagedParentId = decision.ParentId;

        // All projections below read the already-published immutable value. Their notification order
        // cannot expose a mixed phase/list/label/readiness combination.
        RaisePropertyChanged(nameof(ParentDecision));
        RaisePropertyChanged(nameof(ParentCandidates));
        RaisePropertyChanged(nameof(ParentPickerItems));
        RaisePropertyChanged(nameof(ParentEligibility));
        RaisePropertyChanged(nameof(ParentId));
        RaisePropertyChanged(nameof(IsParentLocked));
        RaisePropertyChanged(nameof(IsReplaceParentAbsentFromCandidates));
        RaisePropertyChanged(nameof(PeriodCommitBlocked));
        RaisePropertyChanged(nameof(CanSave));
    }

    private OrgUnitPickerItem? ResolveParentItem(long? id, OrgUnitPickerItem? preferred = null)
    {
        if (id is null)
            return null;

        if (preferred is not null && preferred.Id == id && !string.IsNullOrWhiteSpace(preferred.Display))
            return preferred;

        var existing = ParentDecision.SelectedParentItem;
        if (existing is not null && existing.Id == id && !string.IsNullOrWhiteSpace(existing.Display))
            return existing;

        var listed = ParentDecision.DisplayItems.FirstOrDefault(
            item => item.Id == id && !string.IsNullOrWhiteSpace(item.Display));
        return listed ?? FindTreePickerItem(id.Value);
    }

    private OrgUnitPickerItem? ResolveLoadedParentItem(OrgUnitVersionDto dto)
    {
        if (dto.ParentId is not { } parentId)
            return null;

        if (dto.ParentOrgCodeAsOf is not null || dto.ParentOrgNameShortVnAsOf is not null)
        {
            var label = OrgUnitPickerItem.FormatDisplay(
                dto.ParentOrgCodeAsOf,
                dto.ParentOrgNameShortVnAsOf);
            if (!string.IsNullOrWhiteSpace(label))
                return new OrgUnitPickerItem(parentId, label);
        }

        return FindTreePickerItem(parentId);
    }

    private OrgUnitPickerItem? FindTreePickerItem(long id)
    {
        var label = FindTreeNodeLabel(TreeRoots, id);
        return label is null ? null : new OrgUnitPickerItem(id, label);
    }

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

    private void ClearAfterSuccessfulSave()
    {
        Clear();
        CardClearedAfterSave?.Invoke(this, EventArgs.Empty);
    }

    private async Task CompleteSaveAfterVerificationAsync(long orgUnitId, CardLoadOutcome verification, string successMessage)
    {
        if (verification == CardLoadOutcome.Failed)
            return;

        if (verification == CardLoadOutcome.Loaded)
        {
            ClearAfterSuccessfulSave();
        }
        else
        {
            // SUPERSEDED: the operator navigated away; their click wins. Do not clear, do not write
            // the card. The write already succeeded — this is only the read-back. Still publish the
            // success banner and refresh tree/history. A newer load that completes after this publish
            // will blank the banner as part of loading its own record; accepted residual, no queue.
        }

        StatusMessage = successMessage;
        Severity = StatusSeverity.Success;
        await RefreshTreeAndHistoryAsync(orgUnitId);
    }

    public void ClearHistory()
    {
        // Bump the same generation guard LoadHistoryCoreAsync checks -- otherwise an in-flight
        // LoadAllHistoryAsync (started by the Refresh button just before this ran) can complete
        // AFTER this clear and silently repopulate HistoryRows -- the exact class of staleness
        // bug this method exists to close.
        ++_historyLoadGeneration;
        HistoryRows = new ObservableCollection<OrgUnitHistoryRow>();
    }

    private static readonly Regex OrgCodePattern = new(@"^[A-Z0-9]{4,8}$", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"^[\p{L}\p{N} .\-]{3,100}$", RegexOptions.Compiled);

    // §2.2 identity fields without reason — gates the supplemental open affordance on Add/Edit/Replace.
    private bool HasRequiredIdentityFields()
    {
        if (!OrgCodePattern.IsMatch(OrgCode.Trim()))
            return false;
        if (!NamePattern.IsMatch(OrgNameFullVn.Trim()))
            return false;
        if (!NamePattern.IsMatch(OrgNameShortVn.Trim()))
            return false;
        if (EffectiveFrom is null)
            return false;
        if (!IsUndetermined && EffectiveTo is null)
            return false;
        if (!IsUndetermined && EffectiveTo < EffectiveFrom)
            return false;
        return true;
    }

    // §2.2 (org_code / names) + §2.5 (reason required). Returns the first VN error to show, or null when the
    // form is valid. Deliberately mirrors ConnectionDeclarationViewModel's shape: a coarse CanExecute
    // ("mutating mode") plus the real validation inside Execute, not a fully reactive per-keystroke gate --
    // the Phase 4c View's own live-typing transforms (ALL CAPS) are a separate, later concern.
    private string? ValidateFields()
    {
        // Note (Reason) is OPTIONAL on every card mode — close/cancel audit_log records the actor
        // regardless; Add/Edit/Replace persist an empty reason rather than blocking the operator (requester F5).
        // Close-date rules live in VersionCloseRules via the service (not re-validated here).
        if (Mode == OrgUnitCardMode.Closing)
            return null;

        if (!HasRequiredIdentityFields())
        {
            if (!OrgCodePattern.IsMatch(OrgCode.Trim()))
            {
                // Case is checked here, not normalized: §2.2 says the LIVE-TYPING transform (Phase 4c's TextBox)
                // keeps OrgCode ALL CAPS as the operator types/pastes -- a lowercase value reaching Save means
                // that transform did not run, and normalizing it silently here would hide that instead of failing
                // clearly (rule-platform-infra #1).
                return "Mã đơn vị phải 4-8 ký tự chữ/số IN HOA, không dấu, không khoảng trắng.";
            }

            if (!NamePattern.IsMatch(OrgNameFullVn.Trim()))
            {
                return "Tên đầy đủ phải 3-100 ký tự (chữ, số, khoảng trắng, '.', '-').";
            }

            if (!NamePattern.IsMatch(OrgNameShortVn.Trim()))
            {
                return "Tên tắt phải 3-100 ký tự (chữ, số, khoảng trắng, '.', '-').";
            }

            if (EffectiveFrom is null)
            {
                return "Cần nhập ngày hiệu lực Từ.";
            }

            if (!IsUndetermined && EffectiveTo is null)
            {
                return "Cần nhập ngày Đến hoặc chọn 'Không xác định'.";
            }

            if (!IsUndetermined && EffectiveTo < EffectiveFrom)
            {
                return "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.";
            }
        }

        return null;
    }

    private async Task ExecuteSaveAsync()
    {
        // Re-read Save preconditions at execution time. CanExecute is not a guarantee: a click
        // queued while Lưu was enabled must not cross a later clean/cancel or decision change.
        if (!HasUnsavedInput)
            return;
        if (ParentDecisionBlocksCurrentCommit())
            return;

        var validationError = ValidateFields();
        if (validationError is not null)
        {
            StatusMessage = validationError;
            Severity = StatusSeverity.Error;
            return;
        }

        // Close/cancel: IOrgUnitDeclarationService owns P7 + scope + date rules unbypassably — do not
        // ResolveScopeAsync / IsWithinScopeAsync here (that would duplicate the service and leave Authz
        // errors as English Description text instead of the VM's VN map).
        if (Mode == OrgUnitCardMode.Closing)
        {
            await ExecuteSaveCloseAsync();
            return;
        }

        // Add: IOrgUnitDeclarationService owns P7, the Global-scope gate and root uniqueness unbypassably —
        // do not ResolveScopeAsync here, for the same reason as the close branch above.
        if (Mode == OrgUnitCardMode.Adding)
        {
            var addPeriod = new EffectivePeriod(EffectiveFrom!.Value, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo!.Value);
            await ExecuteSaveAddAsync(addPeriod);
            return;
        }

        // Replace: same posture as Add - the service owns authz, Global scope, root break-glass and the
        // empty-predecessor probe. Do not ResolveScopeAsync here.
        if (Mode == OrgUnitCardMode.Replacing)
        {
            var replacePeriod = new EffectivePeriod(EffectiveFrom!.Value, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo!.Value);
            await ExecuteSaveReplaceAsync(replacePeriod);
            return;
        }

        var username = _currentUser.Username ?? "unknown";

        var authz = await ResolveScopeAsync();
        if (authz.IsError)
        {
            // Same map as every other error surface on this screen. Its Authz.* branch already keeps a real
            // Description and substitutes a Vietnamese sentence only when the Description is empty, so no
            // authorization detail is lost by routing through it.
            StatusMessage = string.Join("; ", authz.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        if (Mode == OrgUnitCardMode.Editing)
        {
            var period = new EffectivePeriod(EffectiveFrom!.Value, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo!.Value);
            await ExecuteSaveEditAsync(period, username, authz.Value);
        }
    }

    // Close/cancel write path: one call into IOrgUnitDeclarationService. EffectiveThrough is shaped for
    // display Status (null = not-yet-effective plan; typed cut date = effective/past) — the service
    // alone derives WHICH repository operation runs. Do not call CancelPlanAsync/CloseVersionAsync here.
    private async Task ExecuteSaveCloseAsync()
    {
        var orgUnitId = _orgUnitId!.Value;
        var versionId = _currentVersionId!.Value;

        // Cancel-plan branch (server-authoritative, see IsCloseCancelPlanBranch) — requester-locked.
        // Confirm before writing; abort leaves the form as-is.
        if (IsCloseCancelPlanBranch())
        {
            // Reworded, not added. The old sentence said the close would
            // "hủy kỳ hiệu lực" without saying that this version never completed an effective day, which is
            // the whole reason this branch exists and the reason nothing is being cut. It also gave the
            // operator no way out when the real mistake was the dates themselves.
            //
            // NO date in this sentence, deliberately: on this branch there is no cut date at all, and
            // EffectiveThrough must be null. Naming one would describe an operation that is not running.
            var confirmed = await _confirmation.ConfirmAsync(
                "Kỳ hiệu lực này chưa hoàn tất ngày hiệu lực nào. Thao tác này hủy toàn bộ kỳ hiệu lực đã khai. "
                + "Nếu thực ra kỳ hiệu lực đã nhập sai, hãy dùng chức năng Sửa.",
                Array.Empty<string>());
            if (!confirmed)
                return;

            var notePending = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim();
            var cancelResult = await _declaration.CloseOrgUnitDeclarationAsync(
                new CloseOrgUnitDeclarationRequest(orgUnitId, versionId, EffectiveThrough: null, notePending));

            if (cancelResult.IsError)
            {
                StatusMessage = string.Join("; ", cancelResult.Errors.Select(FormatWriteError));
                Severity = StatusSeverity.Error;
                return;
            }

            await FinishCloseSuccessAsync(orgUnitId, "Đã hủy.");
            return;
        }

        // FR6: a literal OpenEnd in the To box maps to null at the service and would yield CloseDateRequired
        // while a date is visibly present — block with wording that matches what the operator sees.
        if (EffectiveTo == EffectivePeriod.OpenEnd)
        {
            StatusMessage = CloseDateRequiredMessage;
            Severity = StatusSeverity.Error;
            return;
        }

        DateOnly? effectiveThrough = IsUndetermined || EffectiveTo is null ? null : EffectiveTo;

        // Hardening: never let a blank date reach the service on the retire branch. Today the server
        // rejects a null EffectiveThrough with CloseDateRequired, but if the VM and server ever disagree
        // on the branch (e.g. a concurrent edit of the version's From between load and save), a null
        // date here could land on a server that has since switched to CancelPlan and execute an
        // UNCONFIRMED cancel. Make that unreachable by construction: fail clear in the VM instead, reusing
        // the same message FormatWriteError already maps CloseDateRequired to.
        if (effectiveThrough is null)
        {
            StatusMessage = CloseDateRequiredMessage;
            Severity = StatusSeverity.Error;
            return;
        }

        // Until now the RETIRE branch wrote with no confirmation at all, while
        // the cancel branch had one. Both branches end a unit's life, so both ask first.
        //
        // NO date, on either branch: the no-data rule that already governed
        // error text now covers confirms too. The two sentences still differ, because the two operations differ: this one says the
        // unit STOPS after the end date it already shows, and says when the operator should be reaching for
        // this button at all. The cancel sentence below says the period never completed a day and the whole
        // declaration is being withdrawn.
        //
        // IsCloseCancelPlanBranch chose the wording; it never chose the operation. The service derives the
        // branch itself from its own read and refuses if it disagrees, so a stale card fails clearly
        // instead of silently running the other operation.
        var retireConfirmed = await _confirmation.ConfirmAsync(
            "Đơn vị được chấm dứt hoạt động sau ngày kết thúc hiệu lực. Người dùng chỉ sử dụng chức năng "
            + "đóng khi cần chấm dứt tình trạng hoạt động của đơn vị.",
            Array.Empty<string>());
        if (!retireConfirmed)
        {
            return;
        }

        var note = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim();
        var result = await _declaration.CloseOrgUnitDeclarationAsync(
            new CloseOrgUnitDeclarationRequest(orgUnitId, versionId, effectiveThrough, note));

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        await FinishCloseSuccessAsync(orgUnitId, "Đã lưu.");
    }

    private async Task FinishCloseSuccessAsync(long orgUnitId, string successMessage)
    {
        Mode = OrgUnitCardMode.ReadOnly;
        // Capture before the probe await: a tree click during it bumps _cardLoadGeneration via LoadAsync.
        var ownership = _cardLoadGeneration;
        var stillVisible = await _orgUnits.GetByIdentityAsync(orgUnitId, _dates.Today);
        if (stillVisible.IsError)
        {
            if (!IsExpectedCloseAbsence(stillVisible.Errors))
            {
                StatusMessage = string.Join("; ", stillVisible.Errors.Select(FormatLoadError));
                Severity = StatusSeverity.Error;
                return;
            }

            // Expected absence (N4/N5): nothing left to show today. Still-owning maps to Loaded so
            // CompleteSaveAfterVerificationAsync is the only place that decides a superseded save.
            var verification = ownership == _cardLoadGeneration
                ? CardLoadOutcome.Loaded
                : CardLoadOutcome.Superseded;
            await CompleteSaveAfterVerificationAsync(orgUnitId, verification, successMessage);
            return;
        }

        var loaded = await LoadAsync(orgUnitId, _dates.Today);
        await CompleteSaveAfterVerificationAsync(orgUnitId, loaded, successMessage);
    }

    private static bool IsExpectedCloseAbsence(IReadOnlyList<Error> errors) =>
        errors.Count > 0 && errors.All(e => e.Type == ErrorType.NotFound);

    // Reused by both FormatWriteError's CloseDateRequired mapping and the VM-side retire-branch
    // null-date guard above — one wording, one home (rule-prefer-existing).
    private const string CloseDateRequiredMessage = "Ngày kết thúc hiệu lực chưa được khai báo.";

    // the operator-message rules — screen-authored; no service error code behind either arm.
    private const string ReplacePeriodNoEligibleParentMessage =
        "Kỳ hiệu lực thay thế không có đơn vị cấp trên phù hợp.";

    private const string ReplacePeriodParentCoverageMismatchMessage =
        "Kỳ hiệu lực của đơn vị cấp trên không phù hợp với kỳ hiệu lực thay thế.";

    // Single home for "did this gate publish StatusMessage?" — both §1.8a arms.
    private static bool IsReplaceParentPeriodGateMessage(string? message) =>
        message is ReplacePeriodNoEligibleParentMessage
            or ReplacePeriodParentCoverageMismatchMessage;

    // Brief 163 FR1: one permission-family sentence for every Authz / scope / admin-flag denial on this screen.
    private const string PermissionDeniedMessage = "Người dùng không được cấp quyền.";

    // Presentation map for ErrorOr codes from all four service write gestures on this screen —
    // Add, Edit, close/cancel, and Replace. Codes are the contract; VN wording is the VM's job.
    // Reuse existing screen strings wherever an equivalent already existed. Engine races /
    // CompositeWrite can still surface VersionedRepository.* (table-naming LockTimeout,
    // InvalidShrink, …) — map those so raw engine text never reaches the operator.
    private string FormatWriteError(Error error) => error.Code switch
    {
        // ---- Add path (IOrgUnitDeclarationService.AddOrgUnitDeclarationAsync) ----
        "OrgUnit.AddRequiresGlobalScope" =>
            PermissionDeniedMessage,
        "OrgUnit.ReplaceRequiresGlobalScope" =>
            PermissionDeniedMessage,
        // N1 as amended 2026-08-17: the rule is about the PERIOD, not about "a root exists". A root that has
        // been retired may be succeeded by a new one, so wording that said a root already exists permanently
        // would be wrong — it would tell the operator something is permanently impossible when only these dates are.
        //
        // It names BOTH remedies on purpose (QA Reviewer LOW-4, re-verified 2026-08-27): this code is what an
        // operator gets when they simply FORGOT to pick a parent while a root exists — the form has no
        // "parent required" rule of its own (ValidateFields / Add call the service with no parent check), and
        // it must not grow one, because deciding whether a root already exists is exactly the probe that moved
        // server-side. Naming only the dates would send that operator to change the effective period, which
        // is not their problem. Settled sentence (brief 163 FR2):
        // "Đơn vị gốc bị trùng lặp, người dùng kiểm tra thông tin đơn vị cấp trên và kỳ hiệu lực."
        "OrgUnit.RootPeriodOverlaps" =>
            "Đơn vị gốc bị trùng lặp, người dùng kiểm tra thông tin đơn vị cấp trên và kỳ hiệu lực.",
        "OrgUnit.CodeInUse" =>
            "Mã đơn vị này đã được dùng cho một đơn vị khác trong khoảng thời gian trùng nhau.",
        "TemporalFk.ParentGap" =>
            "Kỳ hiệu lực của đơn vị vượt ngoài kỳ hiệu lực của đơn vị cấp trên.",
        // ---- close/cancel path ----
        VersionCloseRules.Codes.CloseDateRequired =>
            CloseDateRequiredMessage,
        // Brief 163: no data inside operator messages (requester 2026-08-26) — floor date not interpolated.
        VersionCloseRules.Codes.CloseDateInPast =>
            "Ngày kết thúc hiệu lực không được khai báo trước ngày hôm qua.",
        VersionCloseRules.Codes.CloseDateEqualsVersionEnd =>
            "Ngày kết thúc hiệu lực đã được khai báo trước đó.",
        VersionCloseRules.Codes.CloseDateOutsideVersionPeriod =>
            "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
        VersionCloseRules.Codes.VersionAlreadyEnded =>
            "Đơn vị đã hết hiệu lực.",
        VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan =>
            "Thao tác hủy kỳ hiệu lực không yêu cầu nhập ngày kết thúc hiệu lực.",
        "OrgUnit.NotInScope" =>
            PermissionDeniedMessage,
        // an earlier ruling: the parent a stale card echoed is not the one stored. There is no "chọn lại đơn vị
        // cha" advice to give -- the parent is immutable, so reloading is the only move.
        "OrgUnit.ParentMismatch" =>
            "Đơn vị cha đã thay đổi ở nơi khác - hãy tải lại thẻ rồi lưu lại.",
        // Distinct from the code above ON PURPOSE (QA Reviewer G-22): this one is not a stale card, so
        // "tải lại rồi lưu lại" would be advice that cannot work. The unit's own history disagrees with
        // itself and only an administrator can resolve which parent is correct.
        "OrgUnit.ParentNotWellDefined" =>
            "Đơn vị này có nhiều đơn vị cha khác nhau trong lịch sử nên không xác định được cha hiện tại - báo quản trị viên trước khi sửa.",
        // Widened when Edit joined the service (earlier ruling): these two used to say "để đóng/hủy" because
        // close/cancel was the only branch that could produce them. Edit reaches both now, so naming a
        // single operation would have made the message wrong on the branch that was just added.
        "OrgUnit.VersionNotFound" or "VersionedRepository.VersionNotFound" =>
            "Không tìm thấy phiên bản đơn vị cho thao tác này.",
        "VersionedRepository.NotAFuturePlan" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "VersionedRepository.DependentSetChanged" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "VersionedRepository.DependentNotEnlisted" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "VersionedRepository.LockTimeout" =>
            "Dữ liệu đang được người dùng khác khai báo.",
        "VersionedRepository.InvalidShrink" =>
            "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
        "OrgUnit.GapNotAllowed" =>
            "Kỳ hiệu lực không liên tục.",
        "OrgUnit.RootNotClosable" =>
            "Không thể đóng hoặc hủy đơn vị gốc.",
        // The root org unit may be declared and adjusted only by a break-glass
        // rescuer. Two codes, two sentences, because the operator's next move differs -- one is "you cannot
        // create this", the other is "you cannot change this".
        // Names BOTH readings on purpose, for the same reason OrgUnit.RootPeriodOverlaps does: the form has
        // no "parent required" rule of its own, so the commonest way to reach this code is an ordinary
        // admin who simply FORGOT to pick a parent. Naming only the rescuer would tell that operator
        // something is permanently impossible when their real problem is one empty field.
        "OrgUnit.RootNotDeclarable" =>
            "Chỉ quản trị viên cứu hộ mới được khai báo đơn vị gốc - nếu bạn khai báo đơn vị cấp dưới, hãy chọn đơn vị cấp trên.",
        "OrgUnit.RootNotEditable" =>
            "Chỉ quản trị viên cứu hộ mới được sửa đơn vị gốc.",
        // RootNotReplaceable names the operator's permission, not a class of administrator.
        // With the picker affordance in place this code is reachable mainly on a race.
        "OrgUnit.RootNotReplaceable" =>
            "Người dùng không có quyền thay thế đơn vị gốc.",
        // Same house sentence as NotAFuturePlan / DependentSetChanged: stale card, reload the screen.
        "OrgUnit.PredecessorMarksNothing" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "OrgUnit.ParentWithinPredecessor" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        // Already the service Description and pinned by IAM integration tests 12/13; arm so the generic
        // fallback cannot swallow it.
        "OrgUnit.PredecessorNotEmpty" =>
            "Đơn vị còn tham số phụ thuộc, người dùng cần xử lý tham số phụ thuộc trước khi thực hiện thao tác.",
        // The note is the only carrier of "why the period changed".
        "OrgUnit.ReasonRequiredForPeriodChange" =>
            "Khi thay đổi kỳ hiệu lực, người dùng phải nhập lý do.",
        // Reachable from this screen: the unit still has a later stretch, so it does not end on the date
        // the operator just confirmed.
        "OrgUnit.EndsOnLeavesLaterCoverage" =>
            "Đơn vị còn giai đoạn hiệu lực sau ngày này nên chưa thể kết thúc.",
        // Not reachable from this screen -- the confirm derives both values from the SAME date box, and it
        // only offers the route when a tail exists. Mapped anyway so a future caller cannot re-open the
        // English Description leak; this is not a claim that a route exists.
        "OrgUnit.EndsOnDisagreesWithPeriod" or "OrgUnit.EndsOnNotBeforeStoredEnd" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "EffectivePeriod.OverlappingVersions" =>
            "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
        // Brief 160: unreachable on this screen's save path today; arm kept so a later route cannot
        // re-open the Description leak. Not a claim that the route exists.
        "EffectivePeriod.NoCoverage" =>
            "Đơn vị không hiệu lực tại ngày đã chọn.",
        // Brief 160: unreachable on this screen's save path today; arm kept so a later route cannot
        // re-open the Description leak. Not a claim that the route exists.
        "EffectivePeriod.InvalidRange" =>
            "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.",
        "TemporalFk.DependentsUncovered" =>
            "Đơn vị không được đóng do còn đơn vị cấp dưới hoặc còn người dùng phụ thuộc.",
        "Authz.ScopeInsufficient" =>
            PermissionDeniedMessage,
        "Authz.NotGranted" =>
            PermissionDeniedMessage,
        // Explicit R-SYS arms: reachable via DenyOrPropagate / CompositeWrite / audit (brief 162);
        // completeness tests prove they are handled deliberately, not by accident.
        "Function.DuplicateKey" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "User.DuplicateUsername" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "RolePermission.DuplicateGrant" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "CompositeWrite.NotEnlisted" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "AuditLogWriter.NoAmbientConnection" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        // Any other Authz.* — generic only; never pass through Description (English raise sites exist).
        _ when error.Code.StartsWith("Authz.", StringComparison.Ordinal) =>
            PermissionDeniedMessage,
        _ => "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
    };

    // Test-only public wrappers — completeness tests call the private maps without InternalsVisibleTo.
    public string FormatWriteErrorPublic(Error error) => FormatWriteError(error);
    public string FormatLoadErrorPublic(Error error) => FormatLoadError(error);

    // Edit write path: one call into IOrgUnitDeclarationService (earlier ruling). The service owns
    // P7, the scope-membership check and the parent-immutability guard unbypassably -- this screen used to
    // hold the first two itself and write straight to the repository, so any other caller got neither.
    //
    // `username` and `scope` stay as parameters for signature parity with the other write helpers.
    // ExecuteSaveAsync resolves them once only on the Editing path — Closing, Adding and Replacing
    // return earlier with their own resolution. This branch no longer USES them for a gate — the
    // service re-derives both server-side — and they are deliberately not forwarded on the request.
    private async Task ExecuteSaveEditAsync(EffectivePeriod period, string username, DataScope scope)
    {
        var orgUnitId = _orgUnitId!.Value;

        // PreviewUpsertAsync returns every isactive=1 version whose period overlaps the new one -- including
        // the version currently loaded on the card (it always overlaps an in-place edit of itself). Exclude
        // it: H2 is about OTHER versions being affected, not the one the operator is deliberately editing.
        var affected = (await _orgUnits.PreviewUpsertAsync(orgUnitId, period))
            .Where(a => a.Id != _currentVersionId)
            .ToList();
        if (affected.Count > 0)
        {
            var details = affected
                .Select(a => $"{a.OrgCode} — {FormatDate(a.EffectiveFrom)} → {FormatDate(a.EffectiveTo)}")
                .ToList();
            var confirmed = await _confirmation.ConfirmAsync(
                "Thao tác này sẽ ảnh hưởng các phiên bản hiệu lực khác của đơn vị này. Tiếp tục?", details);
            if (!confirmed)
            {
                return;
            }
        }

        // The SECOND confirm, and it answers a different question from the one above.
        // H2 asks "which OTHER versions does this touch"; this asks "what will this save
        // LEAVE BEHIND on this very unit". The exclusion at the top of this method is precisely why the
        // remnant shape gets no confirmation today -- both confirms survive, neither replaces the other.
        //
        // The remnant list comes from the service's canonical preview, never from a derivation here: the
        // 8-case algebra is LOCKED and keeps ONE caller layer. Whatever this method
        // displays and whatever the write performs then come from the same planner.
        var preview = await _declaration.PreviewEditAsync(orgUnitId, _currentVersionId!.Value, period, endsOn: null);
        if (preview.IsError)
        {
            StatusMessage = string.Join("; ", preview.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        // A remnant sitting BEFORE the new period is a head; one sitting AFTER it is a tail. Keyed to where
        // each operation actually falls, never to the algebra's case number: case 8 yields zero, one or two
        // remnants depending on where the span's boundaries land, so a branch on "which case is this" is
        // wrong by construction.
        var head = preview.Value.FirstOrDefault(r => r.Period.To < period.From);
        var tail = preview.Value.FirstOrDefault(r => r.Period.From > period.To);

        // ONE sentence for every period-changing edit, carrying NO dates: the no-data rule that
        // already governed error text now covers this confirm too. The branch below still
        // decides what the save DOES - a tail means the operator is ending the unit - but what they are
        // shown no longer varies with the shape.
        //
        // ⚠️ The cost is stated where it is paid: on the tail branch, Tiếp tục ENDS the unit, and this
        // sentence does not say so.
        const string PeriodChangeConfirm =
            "Đơn vị đang được điều chỉnh kỳ hiệu lực, người dùng cần xác nhận.";

        DateOnly? endsOn = null;
        if (tail is not null)
        {
            if (!await _confirmation.ConfirmAsync(PeriodChangeConfirm, Array.Empty<string>()))
            {
                return;
            }

            // A tail keeps the OLD values, which is never what
            // Sửa means: editing changes the content that is there, it does not also declare a second
            // stretch. So confirming IS the operator saying the unit ends here.
            endsOn = period.To;
        }
        else if (head is not null)
        {
            // Head only: warn, and offer NO route. Đóng cannot move effective_from, so
            // pointing the operator there would send them to an operation that cannot do what they want.
            if (!await _confirmation.ConfirmAsync(PeriodChangeConfirm, Array.Empty<string>()))
            {
                return;
            }
        }

        var code = OrgCode.Trim().ToUpperInvariant();

        // ParentId is ECHOED, never proposed: the request has no "desired parent" field, and the picker is
        // Display-only outside Add (OrgUnitDeclarationView.xaml.cs RefreshParentSurface), so this is the
        // value that was LOADED. The service verifies it against its own read under the identity lock and
        // rejects a mismatch -- so a stale card fails cleanly instead of writing against a parent that moved.
        var result = await _declaration.EditOrgUnitDeclarationAsync(
            new EditOrgUnitDeclarationRequest(
                orgUnitId, _currentVersionId!.Value, ParentId, period, endsOn, code, OrgNameFullVn.Trim(),
                OrgNameShortVn.Trim(), Reason.Trim(), Supplemental));

        if (result.IsError)
        {
            // Edit goes through the SAME presentation map as Add, Close and Replace (requester F5,
            // 2026-08-17). It used to dump the raw English Description, which was survivable while no
            // write code had VN wording — but once `OrgUnit.CodeInUse` and `TemporalFk.ParentGap` were
            // mapped for Add, the one screen showed the SAME code in Vietnamese from one button and in
            // English from another.
            // ⚠ CORRECTED 2026-09-05 (F-244-03, sixth anchor). This used to say the map "falls through
            // to Description for anything it does not know". It does NOT, and never did on this screen:
            // the catch-all is `_ => "Lỗi hệ thống, …"`, nothing here reads Error.Description, and the map's
            // own arms say "never pass through Description". So an UNMAPPED code shows the generic system
            // sentence, not English — which means adding a service error code without adding its arm here
            // is silent, not loud. `FormatWriteErrorCompleteness` in the Shell tests is the gate that
            // catches that.
            StatusMessage = string.Join("; ", result.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        Mode = OrgUnitCardMode.ReadOnly;
        var verification = await LoadAsync(orgUnitId, period.From);
        await CompleteSaveAfterVerificationAsync(orgUnitId, verification, "Đã lưu.");
    }

    private static string FormatDate(DateOnly d) => d == EffectivePeriod.OpenEnd ? "Không xác định" : d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private async Task ExecuteSaveReplaceAsync(EffectivePeriod period)
    {
        // Settled confirm (card 238): what the gesture does, no dates and no values.
        const string ReplaceConfirm =
            "Đơn vị sẽ được thay thế toàn bộ thông tin. Người dùng cần xác nhận tiếp tục trước khi lưu.";
        if (!await _confirmation.ConfirmAsync(ReplaceConfirm, Array.Empty<string>()))
        {
            return;
        }

        var result = await _declaration.ReplaceOrgUnitDeclarationAsync(
            new ReplaceOrgUnitDeclarationRequest(
                _orgUnitId!.Value,
                period,
                OrgCode.Trim().ToUpperInvariant(),
                ParentId,
                OrgNameFullVn.Trim(),
                OrgNameShortVn.Trim(),
                Reason.Trim(),
                Supplemental));

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        var newId = result.Value.OrgUnitId;
        Mode = OrgUnitCardMode.ReadOnly;
        var verification = await LoadAsync(newId, period.From);
        await CompleteSaveAfterVerificationAsync(newId, verification, "Đã lưu.");
    }

    // Add write path: one call into IOrgUnitDeclarationService. The service owns P7, the Global-scope gate,
    // root uniqueness, the identity mint, the first version and the audit row — all in ONE transaction
    // (an earlier ruling, 2026-08-17). Do NOT reintroduce any of them here: this screen used to mint the header
    // on its own connection and hand-compensate with DeleteEmptyIdentityAsync when the version write failed,
    // which design-effective-period.md §7 forbids and which left an orphan identity whenever the
    // compensation itself did not run.
    private async Task ExecuteSaveAddAsync(EffectivePeriod period)
    {
        var result = await _declaration.AddOrgUnitDeclarationAsync(
            new AddOrgUnitDeclarationRequest(
                period,
                OrgCode.Trim().ToUpperInvariant(),
                OrgNameFullVn.Trim(),
                OrgNameShortVn.Trim(),
                ParentId,
                Reason.Trim(),
                Supplemental));

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        var newId = result.Value.OrgUnitId;

        Mode = OrgUnitCardMode.ReadOnly;
        // Reload at the just-saved period's own start, not "today": a future-dated Add (N4) has no coverage
        // AT today, so resolving at today would wrongly NotFound right after a successful save.
        var verification = await LoadAsync(newId, period.From);
        await CompleteSaveAfterVerificationAsync(newId, verification, "Đã lưu.");
    }
}
