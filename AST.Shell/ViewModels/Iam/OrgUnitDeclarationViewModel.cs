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

public enum OrgUnitCardMode { ReadOnly, Adding, Editing, Closing }

public enum ParentEligibilityState { Unresolved, Loading, Resolved, Failed }

public enum CardLoadOutcome { Loaded, Failed, Superseded }

// Screen A declaration card: load-by-identity, dirty tracking, IDeclarationForm/IStatusBanner, the
// §2.7.10 button-matrix mode transitions, save/root-creation/parent-picker wiring, and the real
// tree/history data + post-save refresh (Phase 4d).
public sealed class OrgUnitDeclarationViewModel : BindableBase, IDeclarationForm, IStatusBanner
{
    private readonly IOrgUnitRepository _orgUnits;
    private readonly IOrgUnitDeclarationService _declaration;
    // Backlog 0.8: needed for CanClose only. Same injection as RoleDeclarationViewModel's own break-glass
    // dependency -- the AUTHORITY is the service's gate; this decides whether the button that reaches it
    // is even enabled.
    private readonly IBreakGlassPolicy _breakGlass;
    private readonly IBusinessDateProvider _dates;
    private readonly ICurrentWindowsUser _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IConfirmationPrompt _confirmation;

    // Function-level P7 (N8): ONE key gates every DB-mutating command on this screen (Add/Edit/Close), per
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
        BeginCloseCommand = new DelegateCommand(ExecuteBeginClose, () => CanClose).ObservesProperty(() => Mode).ObservesProperty(() => Status).ObservesProperty(() => IsRoot);
        CancelCommand = new AsyncDelegateCommand(ExecuteCancelAsync, () => CanCancel).ObservesProperty(() => Mode);
        SaveCommand = new AsyncDelegateCommand(ExecuteSaveAsync, () => CanSave).ObservesProperty(() => Mode);
    }

    private bool _isLoading;

    private DateOnly? _lastTreeAsOf;

    // Stale-result guards for overlapping loads (same idiom as _parentRefreshGeneration).
    private int _treeLoadGeneration;
    private int _historyLoadGeneration;
    private int _cardLoadGeneration;

    private void MarkDirty()
    {
        if (!_isLoading)
        {
            IsDirty = true;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
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
                MarkDirty();
                RecomputeParentEligibility();
                // EffectiveFrom is an INPUT to IsCloseCancelPlanBranch (via VersionCloseRules.BranchFor), so
                // it is an input to IsEffectivePeriodEnabled's formula too — must raise the same way Mode's
                // and Status's setters do, or the strip's IsEnabled binding can go stale after an edit here.
                RaisePropertyChanged(nameof(IsEffectivePeriodEnabled));
                SyncCloseDateStatusHint();
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
                MarkDirty();
                RecomputeParentEligibility();
                OnCloseDateFieldEdited();
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
                MarkDirty();
                RecomputeParentEligibility();
                OnCloseDateFieldEdited();
            }
        }
    }

    private long? _parentId;
    public long? ParentId
    {
        get => _parentId;
        set { if (SetProperty(ref _parentId, value)) MarkDirty(); }
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
        set { if (SetProperty(ref _supplemental, value)) MarkDirty(); }
    }

    public void MarkSupplementalDirty() => MarkDirty();

    private IReadOnlyList<OrgUnitPickerItem> _parentCandidates = [];
    public IReadOnlyList<OrgUnitPickerItem> ParentCandidates
    {
        get => _parentCandidates;
        private set => SetProperty(ref _parentCandidates, value);
    }

    private bool _isParentLocked;
    public bool IsParentLocked
    {
        get => _isParentLocked;
        private set => SetProperty(ref _isParentLocked, value);
    }

    // Raised only after a successful save has cleared the card — not from Clear() itself
    // (LoadAsync calls Clear() as its first action).
    public event EventHandler? CardClearedAfterSave;

    private ParentEligibilityState _parentEligibility = ParentEligibilityState.Unresolved;
    public ParentEligibilityState ParentEligibility
    {
        get => _parentEligibility;
        private set => SetProperty(ref _parentEligibility, value);
    }

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

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private StatusSeverity _severity = StatusSeverity.None;
    public StatusSeverity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
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
        _isLoading = true;
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
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }
    }

    private OrgUnitCardMode _mode = OrgUnitCardMode.ReadOnly;
    public OrgUnitCardMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                RaisePropertyChanged(nameof(CanOpenSupplemental));
                RaisePropertyChanged(nameof(IsEffectivePeriodEnabled));
                SyncCloseDateStatusHint();
            }
        }
    }

    // AstEffectivePeriod.IsEnabled binding — single home for strip enablement (FR9).
    // On in Adding/Editing/Closing; off in ReadOnly and Closing∧cancel-plan-branch. Literal
    // "enabled unless Closing∧cancel-plan" would wrongly enable ReadOnly — do not simplify that way.
    //
    // The cancel-plan-vs-retire branch is deliberately NOT `Status == VersionStatus.Pending` — that was
    // the pre-D1 defect (a same-day-effective version labels `Effective`, not `Pending`, yet the server
    // now cancels it too; see VersionCloseRules.Validate / D1). IsCloseCancelPlanBranch
    // consumes VersionCloseRules' own decision instead of re-deriving the `From >= today` comparison here.
    public bool IsEffectivePeriodEnabled =>
        Mode is OrgUnitCardMode.Adding or OrgUnitCardMode.Editing
        || (Mode == OrgUnitCardMode.Closing && !IsCloseCancelPlanBranch());

    // Server-authoritative branch: consumes VersionCloseRules.BranchFor (the single home of the
    // Retire-vs-CancelPlan decision, D1 2026-08-10) instead of re-deriving `EffectiveFrom >= today`
    // here — that re-derivation is exactly the defect being fixed (the screen used to branch on the
    // STATUS LABEL, which diverges from the server's own boundary for a same-day-effective version).
    private bool IsCloseCancelPlanBranch()
    {
        if (EffectiveFrom is null)
            return false;

        // OpenEnd here is a fabricated placeholder, not the target version's real end — correct today
        // ONLY because BranchFor reads `targetPeriod.From` alone. The real end is not on the form while
        // Closing (ExecuteBeginClose blanks EffectiveTo); it survives solely in `_snapshot.EffectiveTo`.
        // If BranchFor is ever changed to also consult `To`, this must read `_snapshot.EffectiveTo` instead.
        var targetPeriod = new EffectivePeriod(EffectiveFrom.Value, EffectivePeriod.OpenEnd);
        return VersionCloseRules.BranchFor(_dates.Today, targetPeriod) == VersionCloseBranch.CancelPlan;
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

    public bool CanAdd => Mode == OrgUnitCardMode.ReadOnly;

    public bool CanEdit => Mode == OrgUnitCardMode.ReadOnly && Status is VersionStatus.Effective or VersionStatus.Pending;

    // A root is closable ONLY by a break-glass rescuer (backlog 0.8, requester ruling 2026-08-21). This is
    // the button-disabling affordance, NOT the guard -- CloseOrgUnitDeclarationAsync re-reads the parent
    // under its own lock and refuses with OrgUnit.RootNotClosable regardless of what this property says.
    // Without this clause the service-side carve-out would be UNREACHABLE from the UI: this is the one
    // button that reaches the service, and the service derives close-vs-cancel itself, so a bare !IsRoot
    // blocks the cancel path too.
    //
    // No ObservesProperty for break-glass membership: it comes from the signed §⑤ admin list and cannot
    // change within a session, so there is nothing to raise a change for. IsRoot IS observed (see
    // BeginCloseCommand) because loading a different card changes it.
    public bool CanClose =>
        Mode == OrgUnitCardMode.ReadOnly
        && (!IsRoot || _breakGlass.IsBreakGlassAdmin(_currentUser.Username ?? "unknown"))
        && Status is VersionStatus.Effective or VersionStatus.Pending;

    public bool CanCancel => Mode != OrgUnitCardMode.ReadOnly;

    public bool CanSave => Mode != OrgUnitCardMode.ReadOnly;

    // Supplemental affordance: always visible; enabled per settled matrix (Closing = view-only open).
    public bool CanOpenSupplemental => Mode switch
    {
        OrgUnitCardMode.Closing => true,
        OrgUnitCardMode.ReadOnly => _orgUnitId is not null,
        OrgUnitCardMode.Adding or OrgUnitCardMode.Editing => HasRequiredIdentityFields(),
        _ => false,
    };

    public DelegateCommand BeginAddCommand { get; }
    public DelegateCommand BeginEditCommand { get; }
    public DelegateCommand BeginCloseCommand { get; }
    public AsyncDelegateCommand CancelCommand { get; }
    public AsyncDelegateCommand SaveCommand { get; }

    // Captured from the loaded card the instant BeginAdd runs, BEFORE Clear() wipes it -- this is how
    // Screen A remembers "the tree node that was selected" (N3/N7) across the Add flow; the real
    // tree-node-click (OrgUnitDeclarationView.CommitTreeSelectionAsync, reached through
    // TreeSelectionGate) calls LoadAsync exactly like today's tests do.
    private (long ParentId, EffectivePeriod Coverage)? _addParentContext;

    private void ExecuteBeginAdd()
    {
        _snapshot = CaptureSnapshot();
        _addParentContext = _orgUnitId is { } loadedId
            ? (loadedId, new EffectivePeriod(EffectiveFrom ?? _dates.Today, IsUndetermined ? EffectivePeriod.OpenEnd : EffectiveTo ?? EffectivePeriod.OpenEnd))
            : null;
        Clear();
        Mode = OrgUnitCardMode.Adding;
        RecomputeParentEligibility();
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
        if (Mode != OrgUnitCardMode.Adding)
        {
            return;
        }

        var formPeriod = TryBuildFormPeriod();

        if (_addParentContext is { } ctx && (formPeriod is null || !CoverageGap.TryFind([ctx.Coverage], formPeriod.Value, out _)))
        {
            IsParentLocked = true;
            ParentId = ctx.ParentId;
            ParentCandidates = [];
            AbandonParentCandidateQuery();
            ParentEligibility = ParentEligibilityState.Unresolved;
            return;
        }

        IsParentLocked = false;
        if (ParentId == _addParentContext?.ParentId)
        {
            // Was locked to the tree-context candidate; the EP just typed no longer qualifies it -- clear the
            // stale pre-fill rather than leaving a picker-less selection standing.
            ParentId = null;
        }

        if (formPeriod is null)
        {
            ParentCandidates = [];
            AbandonParentCandidateQuery();
            ParentEligibility = ParentEligibilityState.Unresolved;
        }
        else
        {
            // The fakes' Task.FromResult results complete synchronously, so ParentCandidates is already
            // updated by the time this (synchronous) method returns -- no await needed by callers/tests.
            // Loading is still a real state: a genuine GetEligibleParentsAsync awaits I/O, and treating
            // Count==0 during that await as root-creation is the same misleading-text bug one step later.
            ParentEligibility = ParentEligibilityState.Loading;
            _ = RefreshParentCandidatesAsync(formPeriod.Value, ++_parentRefreshGeneration);
        }
    }

    // Guards the fire-and-forget refresh below: a real GetEligibleParentsAsync call genuinely awaits I/O, so
    // rapid successive EP edits can start several overlapping calls whose completions may arrive out of
    // order. Each call captures its own generation number at dispatch time; only the result whose generation
    // still matches the current one (i.e. no newer edit has fired since) is allowed to write ParentCandidates.
    private int _parentRefreshGeneration;

    private void AbandonParentCandidateQuery() => _parentRefreshGeneration++;

    private async Task RefreshParentCandidatesAsync(EffectivePeriod formPeriod, int generation)
    {
        try
        {
            // Reads stay Global by policy (decision-log 2026-08-05, "Scope-checked writes" part 2): the
            // parent picker must offer every eligible parent regardless of the operator's own scope --
            // only the eventual write (Add/Edit/Close) is gated by the caller's resolved scope.
            var scope = new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown");
            var candidates = await _orgUnits.GetEligibleParentsAsync(scope, formPeriod);
            if (generation == _parentRefreshGeneration)
            {
                ParentCandidates = candidates;
                ParentEligibility = ParentEligibilityState.Resolved;
            }
        }
        catch (Exception)
        {
            // A discarded task's exception would otherwise be lost entirely, leaving the picker silently
            // never populated with no diagnostic (prefer a clear failure over silent ambiguity).
            if (generation == _parentRefreshGeneration)
            {
                StatusMessage = "Ứng dụng không tải được danh sách đơn vị cha.";
                Severity = StatusSeverity.Error;
                ParentEligibility = ParentEligibilityState.Failed;
            }
        }
    }

    private void ExecuteBeginEdit()
    {
        _snapshot = CaptureSnapshot();
        Mode = OrgUnitCardMode.Editing;
    }

    private void ExecuteBeginClose()
    {
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
    }

    private async Task ExecuteCancelAsync()
    {
        // _snapshot was captured by the Begin* that entered mutating mode -- for Add it is whatever the card
        // showed BEFORE the blank new-entry form (e.g. the previously selected node, or nothing), so restoring
        // it is correct for all three mutating modes, not just Edit/Close.
        // Cancel restores in-memory fields only (no write) — do not hit the DB for a tree/history refresh (FR6).
        var leftClosing = Mode == OrgUnitCardMode.Closing;
        Mode = OrgUnitCardMode.ReadOnly;
        RestoreSnapshot(_snapshot);
        IsDirty = false;
        // FR13: a failed-close Error would otherwise stay on the read-only card (hint sync only clears Info).
        if (leftClosing && Severity == StatusSeverity.Error)
        {
            StatusMessage = null;
            Severity = StatusSeverity.None;
        }

        await Task.CompletedTask;
    }

    public async Task<CardLoadOutcome> LoadAsync(long orgUnitId, DateOnly asOf)
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
        _isLoading = true;
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

        _isLoading = true;
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
            u => new OrgUnitTreeNode(u.OrgUnitId, $"{u.OrgCode} — {u.OrgNameShortVn}") { IsExpanded = true });
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
        // WRITES (Add/Edit/Close) are gated by the caller's resolved scope -- history is a read-only
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
        var parentLabel = dto.ParentOrgCodeAsOf is null && dto.ParentOrgNameFullVnAsOf is null
            ? string.Empty
            : $"{dto.ParentOrgCodeAsOf} — {dto.ParentOrgNameFullVnAsOf}";

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
        _isLoading = true;
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
            IsDirty = false;
            RaisePropertyChanged(nameof(CanOpenSupplemental));
        }
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

    // §2.2 identity fields without reason — gates the supplemental open affordance on Add/Edit.
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
        // regardless; Add/Edit persist an empty reason rather than blocking the operator (requester F5).
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
        // The date IS named here, unlike on the cancel branch above, because on this branch there really is
        // a cut date and it is the single fact the operator has to check. (Brief 163's "no data in the
        // message" ruling governs ERROR text, where the data would be the system explaining its own
        // refusal; a confirm is the operator re-reading their own input before it is written.)
        //
        // IsCloseCancelPlanBranch chose the wording; it never chose the operation. The service derives the
        // branch itself from its own read and refuses if it disagrees, so a stale card fails clearly
        // instead of silently running the other operation.
        var retireConfirmed = await _confirmation.ConfirmAsync(
            $"Đơn vị này sẽ hết hiệu lực sau ngày {FormatDate(effectiveThrough.Value)}. "
            + "Nếu thực ra kỳ hiệu lực đã nhập sai, hãy dùng chức năng Sửa.",
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

    // Brief 163 FR1: one permission-family sentence for every Authz / scope / admin-flag denial on this screen.
    private const string PermissionDeniedMessage = "Người dùng không được cấp quyền.";

    // Presentation map for the ErrorOr codes of BOTH service write paths, Add and close/cancel — codes are
    // the contract; VN wording is the VM's job. Reuse existing screen strings wherever an equivalent already
    // existed. Engine races / CompositeWrite can still surface VersionedRepository.* (table-naming
    // LockTimeout, InvalidShrink, …) — map those so raw engine text never reaches the operator.
    private string FormatWriteError(Error error) => error.Code switch
    {
        // ---- Add path (IOrgUnitDeclarationService.AddOrgUnitDeclarationAsync) ----
        "OrgUnit.AddRequiresGlobalScope" =>
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
        // Backlog 0.7: the parent a stale card echoed is not the one stored. There is no "chọn lại đơn vị
        // cha" advice to give -- the parent is immutable, so reloading is the only move.
        "OrgUnit.ParentMismatch" =>
            "Đơn vị cha đã thay đổi ở nơi khác - hãy tải lại thẻ rồi lưu lại.",
        // Distinct from the code above ON PURPOSE (QA Reviewer G-22): this one is not a stale card, so
        // "tải lại rồi lưu lại" would be advice that cannot work. The unit's own history disagrees with
        // itself and only an administrator can resolve which parent is correct.
        "OrgUnit.ParentNotWellDefined" =>
            "Đơn vị này có nhiều đơn vị cha khác nhau trong lịch sử nên không xác định được cha hiện tại - báo quản trị viên trước khi sửa.",
        // Widened when Edit joined the service (backlog 0.7): these two used to say "để đóng/hủy" because
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

    // Edit write path: one call into IOrgUnitDeclarationService (backlog 0.7, 2026-08-21). The service owns
    // P7, the scope-membership check and the parent-immutability guard unbypassably -- this screen used to
    // hold the first two itself and write straight to the repository, so any other caller got neither.
    //
    // `username` and `scope` stay as parameters because the shared Save entry point resolves them once for
    // all three branches; this branch no longer USES them for a gate -- the service re-derives both
    // server-side -- and they are deliberately not forwarded on the request.
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

        DateOnly? endsOn = null;
        if (tail is not null)
        {
            // The tail keeps the OLD values, which is never what
            // Sửa means: editing changes the content that is there, it does not also declare a second
            // stretch. So the two buttons are "end the unit here" and "do not write" -- there is no third
            // outcome that keeps the tail. The sentence has to carry that, because the shared dialog's own
            // buttons read "Tiếp tục" and "Hủy" and this screen does not get to rename them.
            var message = head is null
                ? $"Đơn vị sẽ còn một giai đoạn sau ngày {FormatDate(period.To)} vẫn giữ thông tin cũ. "
                  + $"Chọn Tiếp tục để đơn vị kết thúc ngày {FormatDate(period.To)}, hoặc Hủy để sửa lại."
                : $"Đơn vị sẽ còn một giai đoạn trước ngày {FormatDate(period.From)} và một giai đoạn sau ngày "
                  + $"{FormatDate(period.To)} vẫn giữ thông tin cũ. Chọn Tiếp tục để đơn vị kết thúc ngày "
                  + $"{FormatDate(period.To)}, hoặc Hủy để sửa lại.";

            if (!await _confirmation.ConfirmAsync(message, Array.Empty<string>()))
            {
                return;
            }

            endsOn = period.To;
        }
        else if (head is not null)
        {
            // Head only: warn, and offer NO route. Đóng cannot move effective_from, so
            // pointing the operator there would send them to an operation that cannot do what they want.
            var confirmed = await _confirmation.ConfirmAsync(
                $"Đơn vị sẽ còn một giai đoạn trước ngày {FormatDate(period.From)} vẫn giữ thông tin cũ. "
                + "Chọn Tiếp tục để lưu, hoặc Hủy để sửa lại.",
                Array.Empty<string>());
            if (!confirmed)
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
            // Edit goes through the SAME presentation map as Add and Close (requester F5, 2026-08-17). It
            // used to dump the raw English Description, which was survivable while no write code had VN
            // wording — but once `OrgUnit.CodeInUse` and `TemporalFk.ParentGap` were mapped for Add, the one
            // screen showed the SAME code in Vietnamese from one button and in English from another. The map
            // falls through to Description for anything it does not know, so this is strictly a widening.
            StatusMessage = string.Join("; ", result.Errors.Select(FormatWriteError));
            Severity = StatusSeverity.Error;
            return;
        }

        Mode = OrgUnitCardMode.ReadOnly;
        var verification = await LoadAsync(orgUnitId, period.From);
        await CompleteSaveAfterVerificationAsync(orgUnitId, verification, "Đã lưu.");
    }

    private static string FormatDate(DateOnly d) => d == EffectivePeriod.OpenEnd ? "Không xác định" : d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    // Add write path: one call into IOrgUnitDeclarationService. The service owns P7, the Global-scope gate,
    // root uniqueness, the identity mint, the first version and the audit row — all in ONE transaction
    // (backlog 0.4b, 2026-08-17). Do NOT reintroduce any of them here: this screen used to mint the header
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
