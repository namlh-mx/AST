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
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Shell.ViewModels.Iam.Role;

public enum RoleCardMode { ReadOnly, Adding, Editing, Closing }

public enum GrantsReadiness { Unresolved, Loading, Resolved, Failed }

public enum CardLoadOutcome { Loaded, Failed, Superseded }

public sealed record RoleHistoryRow(
    long RoleId,
    long VersionId,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    string FromText,
    string ToText,
    string RecordedAtText,
    string StatusText,
    VersionStatus Status,
    string RoleCode,
    string RoleName,
    bool IsAdminRole,
    string Operation,
    string RecordedBy,
    string Note);

// Currently-effective grant row on the declaration card (saved identity).
public sealed record RoleGrantRow(
    long RolePermissionId,
    long FunctionId,
    string FunctionKey,
    string DisplayName,
    ScopeLevel ScopeLevel,
    string ScopeText);

// Unsaved draft grant the operator is adding on this Save.
public sealed record RoleGrantDraftRow(
    long FunctionId,
    string FunctionKey,
    string DisplayName,
    ScopeLevel ScopeLevel,
    string ScopeText,
    string? Note);

public sealed record RolePermissionJournalRow(
    long RolePermissionId,
    string WhenText,
    string Who,
    string Action,
    string FunctionKey,
    string ScopeText,
    string Note);

public sealed record RoleFunctionPickerItem(
    long FunctionId,
    string FunctionKey,
    string DisplayName,
    string Group);

// Screen B — Role declaration card + history (spec §3.2). Save mutations go through
// IRoleDeclarationService only — role version creation is not on IRoleRepository (read-only). Close/Cancel go through
// IRoleDeclarationService.CloseRoleDeclarationAsync only (never IRoleRepository.CloseVersionAsync/
// CancelPlanAsync directly — those composite-context overloads are intentionally off the public
// interface, see IRoleDeclarationService.cs's own doc comment). The screen writes no dates: `role` is
// Immediate, so the service derives [operationDate, OpenEnd] (and the close inclusive-end) itself.
public sealed class RoleDeclarationViewModel : BindableBase, IDeclarationForm, IStatusBanner
{
    private const string FunctionKey = "Iam.Role.Declare";

    // Fix Round 1 §1.5/§2: reused identically by both FormatSaveError (Edit/Save admin-flag denial) and
    // FormatCloseError (Close/Cancel admin-flag denial) — same code, same UX, never branch on message
    // text. Public because no AST.Shell.Tests InternalsVisibleTo exists on AST.Shell.csproj and adding
    // one is out of this round's Scope (VM + tests only).
    // Brief 163 FR1: permission-family sentence. Public AdminFlag alias kept for tests / same-code UX.
    private const string PermissionDeniedMessage = "Người dùng không được cấp quyền.";

    public const string AdminFlagChangeNotAuthorizedMessage = PermissionDeniedMessage;

    private const string StaleCardReloadMessage =
        "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.";
    private const string CloseSuccessMessage = "Đã cập nhật hiệu lực vai trò.";

    private static readonly Regex RoleCodePattern = new(@"^[a-z0-9_]{5,20}$", RegexOptions.Compiled);
    private static readonly Regex RoleNamePattern = new(@"^[\p{L}\p{N} \-\|()_]{5,100}$", RegexOptions.Compiled);

    private readonly IRoleRepository _roles;
    private readonly IRolePermissionRepository _rolePermissions;
    private readonly IFunctionRepository _functions;
    private readonly IRoleDeclarationService _declaration;
    private readonly IBusinessDateProvider _dates;
    private readonly ICurrentWindowsUser _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IBreakGlassPolicy _breakGlass;
    private readonly IConfirmationPrompt _confirmation;

    private bool _isLoading;
    private int _cardLoadGeneration;
    private int _historyLoadGeneration;
    private int _autoLoadGeneration;
    private int _functionCatalogGeneration;
    private int _saveGeneration;
    private long? _roleId;
    private long? _currentVersionId;
    private Period? _loadedPeriod;
    private FieldSnapshot _snapshot;
    private List<long> _grantIdsToRevoke = [];

    public RoleDeclarationViewModel(
        IRoleRepository roles,
        IRolePermissionRepository rolePermissions,
        IFunctionRepository functions,
        IRoleDeclarationService declaration,
        IBusinessDateProvider dates,
        ICurrentWindowsUser currentUser,
        IAuthorizationService authorization,
        IBreakGlassPolicy breakGlass,
        IConfirmationPrompt confirmation)
    {
        _roles = roles;
        _rolePermissions = rolePermissions;
        _functions = functions;
        _declaration = declaration;
        _dates = dates;
        _currentUser = currentUser;
        _authorization = authorization;
        _breakGlass = breakGlass;
        _confirmation = confirmation;

        BeginAddCommand = new DelegateCommand(ExecuteBeginAdd, () => CanAdd).ObservesProperty(() => Mode);
        BeginEditCommand = new DelegateCommand(ExecuteBeginEdit, () => CanEdit)
            .ObservesProperty(() => Mode).ObservesProperty(() => Status).ObservesProperty(() => GrantsReadiness);
        BeginCloseCommand = new DelegateCommand(ExecuteBeginClose, () => CanClose)
            .ObservesProperty(() => Mode).ObservesProperty(() => Status);
        CancelCommand = new AsyncDelegateCommand(ExecuteCancelAsync, () => CanCancel).ObservesProperty(() => Mode);
        SaveCommand = new AsyncDelegateCommand(ExecuteSaveAsync, () => CanSave)
            .ObservesProperty(() => Mode).ObservesProperty(() => GrantsReadiness);
        AddGrantCommand = new DelegateCommand(ExecuteAddGrant, () => CanMutateGrants)
            .ObservesProperty(() => Mode).ObservesProperty(() => GrantsReadiness);
        RemoveEffectiveGrantCommand = new DelegateCommand<RoleGrantRow>(ExecuteRemoveEffectiveGrant, _ => CanMutateGrants)
            .ObservesProperty(() => Mode).ObservesProperty(() => GrantsReadiness);
        RemoveDraftGrantCommand = new DelegateCommand<RoleGrantDraftRow>(ExecuteRemoveDraftGrant, _ => CanMutateGrants)
            .ObservesProperty(() => Mode).ObservesProperty(() => GrantsReadiness);

        RefreshAdminFlagEditable();
    }

    private void MarkDirty()
    {
        if (_isLoading)
            return;
        IsDirty = true;
    }

    private string _roleCode = string.Empty;
    public string RoleCode
    {
        get => _roleCode;
        set
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (!SetProperty(ref _roleCode, normalized))
                return;
            MarkDirty();
            _ = TryAutoLoadByCodeAsync();
        }
    }

    private string _roleName = string.Empty;
    public string RoleName
    {
        get => _roleName;
        set { if (SetProperty(ref _roleName, value ?? string.Empty)) MarkDirty(); }
    }

    private string _effectivePeriodText = string.Empty;
    public string EffectivePeriodText
    {
        get => _effectivePeriodText;
        private set => SetProperty(ref _effectivePeriodText, value);
    }

    private bool _isAdminRole;
    public bool IsAdminRole
    {
        get => _isAdminRole;
        set
        {
            if (!IsAdminFlagEditable && !_isLoading)
                return;
            if (SetProperty(ref _isAdminRole, value))
                MarkDirty();
        }
    }

    private bool _isAdminFlagEditable;
    public bool IsAdminFlagEditable
    {
        get => _isAdminFlagEditable;
        private set => SetProperty(ref _isAdminFlagEditable, value);
    }

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value ?? string.Empty)) MarkDirty(); }
    }

    private ObservableCollection<RoleGrantRow> _effectiveGrants = [];
    public ObservableCollection<RoleGrantRow> EffectiveGrants
    {
        get => _effectiveGrants;
        private set => SetProperty(ref _effectiveGrants, value);
    }

    private ObservableCollection<RoleGrantDraftRow> _draftGrants = [];
    public ObservableCollection<RoleGrantDraftRow> DraftGrants
    {
        get => _draftGrants;
        private set => SetProperty(ref _draftGrants, value);
    }

    private ObservableCollection<RolePermissionJournalRow> _permissionJournal = [];
    public ObservableCollection<RolePermissionJournalRow> PermissionJournal
    {
        get => _permissionJournal;
        private set => SetProperty(ref _permissionJournal, value);
    }

    private ObservableCollection<RoleFunctionPickerItem> _functionPickerItems = [];
    public ObservableCollection<RoleFunctionPickerItem> FunctionPickerItems
    {
        get => _functionPickerItems;
        private set => SetProperty(ref _functionPickerItems, value);
    }

    private RoleFunctionPickerItem? _selectedFunctionToAdd;
    public RoleFunctionPickerItem? SelectedFunctionToAdd
    {
        get => _selectedFunctionToAdd;
        set => SetProperty(ref _selectedFunctionToAdd, value);
    }

    private ScopeLevel _selectedScopeToAdd = ScopeLevel.Self;
    public ScopeLevel SelectedScopeToAdd
    {
        get => _selectedScopeToAdd;
        set => SetProperty(ref _selectedScopeToAdd, value);
    }

    private ObservableCollection<RoleHistoryRow> _historyRows = [];
    public ObservableCollection<RoleHistoryRow> HistoryRows
    {
        get => _historyRows;
        private set => SetProperty(ref _historyRows, value);
    }

    private string _historyFilterText = string.Empty;
    public string HistoryFilterText
    {
        get => _historyFilterText;
        set => SetProperty(ref _historyFilterText, value ?? string.Empty);
    }

    private VersionStatus _status = VersionStatus.None;
    public VersionStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
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

    private GrantsReadiness _grantsReadiness = GrantsReadiness.Unresolved;
    public GrantsReadiness GrantsReadiness
    {
        get => _grantsReadiness;
        private set
        {
            if (SetProperty(ref _grantsReadiness, value))
            {
                RaisePropertyChanged(nameof(CanEdit));
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(CanMutateGrants));
                RaisePropertyChanged(nameof(GrantsGridOverlayText));
            }
        }
    }

    // FR-7b: a blank card (no role loaded) has nothing to be ready — do not paint
    // "Danh sách quyền chưa sẵn sàng." on first open / after Clear.
    public string? GrantsGridOverlayText => _roleId is null
        ? null
        : GrantsReadiness switch
        {
            GrantsReadiness.Resolved => null,
            GrantsReadiness.Loading => "Đang tải danh sách quyền...",
            GrantsReadiness.Failed => "Ứng dụng không tải được danh sách quyền.",
            _ => "Danh sách quyền chưa sẵn sàng.",
        };

    // FR-5: AST.Shell.Tests has no InternalsVisibleTo; the matrix must place every
    // Mode × readiness cell, including ones unreachable via CanEdit/BeginAdd.
    public void OverrideGrantsReadinessForTest(GrantsReadiness value) => GrantsReadiness = value;

    private readonly record struct FieldSnapshot(
        string RoleCode,
        string RoleName,
        Period? LoadedPeriod,
        bool IsAdminRole,
        VersionStatus Status,
        long? RoleId,
        string Note,
        long? VersionId,
        GrantsReadiness GrantsReadiness,
        IReadOnlyList<RoleGrantRow> EffectiveGrants,
        IReadOnlyList<RoleGrantDraftRow> DraftGrants,
        IReadOnlyList<long> GrantIdsToRevoke,
        IReadOnlyList<RolePermissionJournalRow> Journal,
        RoleFunctionPickerItem? SelectedFunctionToAdd,
        ScopeLevel SelectedScopeToAdd);

    private FieldSnapshot CaptureSnapshot() =>
        new(
            RoleCode, RoleName, _loadedPeriod, IsAdminRole, Status,
            _roleId, Note, _currentVersionId, GrantsReadiness,
            EffectiveGrants.ToList(), DraftGrants.ToList(), _grantIdsToRevoke.ToList(),
            PermissionJournal.ToList(), SelectedFunctionToAdd, SelectedScopeToAdd);

    private void RestoreSnapshot(FieldSnapshot s)
    {
        _isLoading = true;
        try
        {
            RoleCode = s.RoleCode;
            RoleName = s.RoleName;
            SetLoadedPeriod(s.LoadedPeriod);
            _isAdminRole = s.IsAdminRole;
            RaisePropertyChanged(nameof(IsAdminRole));
            Status = s.Status;
            _roleId = s.RoleId;
            Note = s.Note;
            _currentVersionId = s.VersionId;
            GrantsReadiness = s.GrantsReadiness;
            RaisePropertyChanged(nameof(GrantsGridOverlayText));
            EffectiveGrants = new ObservableCollection<RoleGrantRow>(s.EffectiveGrants);
            DraftGrants = new ObservableCollection<RoleGrantDraftRow>(s.DraftGrants);
            _grantIdsToRevoke = s.GrantIdsToRevoke.ToList();
            PermissionJournal = new ObservableCollection<RolePermissionJournalRow>(s.Journal);
            // F4: the in-progress picker selection (never "Thêm dòng"-ed into a draft row) must also
            // revert on Cancel — otherwise it survives into the next Edit and risks an unintended grant.
            SelectedFunctionToAdd = s.SelectedFunctionToAdd;
            SelectedScopeToAdd = s.SelectedScopeToAdd;
            RefreshFunctionPicker();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private RoleCardMode _mode = RoleCardMode.ReadOnly;
    public RoleCardMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                RaisePropertyChanged(nameof(CanAdd));
                RaisePropertyChanged(nameof(CanEdit));
                RaisePropertyChanged(nameof(CanClose));
                RaisePropertyChanged(nameof(CanCancel));
                RaisePropertyChanged(nameof(CanSave));
                RaisePropertyChanged(nameof(CanMutateGrants));
                RefreshAdminFlagEditable();
            }
        }
    }

    public bool CanAdd => Mode == RoleCardMode.ReadOnly;
    public bool CanEdit =>
        Mode == RoleCardMode.ReadOnly
        && Status is VersionStatus.Effective or VersionStatus.Pending
        && GrantsReadiness == GrantsReadiness.Resolved;
    public bool CanClose =>
        Mode == RoleCardMode.ReadOnly && Status is VersionStatus.Effective or VersionStatus.Pending;
    public bool CanCancel => Mode != RoleCardMode.ReadOnly;
    public bool CanSave => Mode switch
    {
        RoleCardMode.Adding or RoleCardMode.Editing => GrantsReadiness == GrantsReadiness.Resolved,
        RoleCardMode.Closing => true,
        _ => false,
    };
    public bool CanMutateGrants =>
        Mode is RoleCardMode.Adding or RoleCardMode.Editing
        && GrantsReadiness == GrantsReadiness.Resolved;

    public DelegateCommand BeginAddCommand { get; }
    public DelegateCommand BeginEditCommand { get; }
    public DelegateCommand BeginCloseCommand { get; }
    public AsyncDelegateCommand CancelCommand { get; }
    public AsyncDelegateCommand SaveCommand { get; }
    public DelegateCommand AddGrantCommand { get; }
    public DelegateCommand<RoleGrantRow> RemoveEffectiveGrantCommand { get; }
    public DelegateCommand<RoleGrantDraftRow> RemoveDraftGrantCommand { get; }

    public IReadOnlyList<ScopeLevel> ScopeChoices { get; } =
        [ScopeLevel.Self, ScopeLevel.OwnOrgUnit, ScopeLevel.OwnOrgUnitAndDescendants, ScopeLevel.Global];

    private void RefreshAdminFlagEditable()
    {
        var username = _currentUser.Username ?? "unknown";
        IsAdminFlagEditable = _breakGlass.IsBreakGlassAdmin(username)
            && Mode is RoleCardMode.Adding or RoleCardMode.Editing;
    }

    private void ExecuteBeginAdd()
    {
        _snapshot = CaptureSnapshot();
        Clear();
        Mode = RoleCardMode.Adding;
        RefreshAdminFlagEditable();
        _ = RefreshFunctionCatalogAsync();
    }

    private void ExecuteBeginEdit()
    {
        _snapshot = CaptureSnapshot();
        Mode = RoleCardMode.Editing;
        RefreshAdminFlagEditable();
        _ = RefreshFunctionCatalogAsync();
    }

    private void ExecuteBeginClose()
    {
        _snapshot = CaptureSnapshot();
        Mode = RoleCardMode.Closing;
        if (Severity is StatusSeverity.None or StatusSeverity.Info)
        {
            StatusMessage = BuildCloseEffectText();
            Severity = StatusSeverity.Info;
        }
    }

    // Single home of "which close branch are we in" for THIS screen — consumes the shared
    // VersionCloseRules.BranchFor, never re-derives it from Status/EffectiveFrom comparisons locally
    // (mirrors OrgUnitDeclarationViewModel.IsCloseCancelPlanBranch).
    private bool IsCloseCancelPlanBranch()
    {
        if (_loadedPeriod is null)
            return false;

        return VersionCloseRules.BranchFor(_dates.Today, _loadedPeriod.Value) == VersionCloseBranch.CancelPlan;
    }

    public bool IsCloseCancelPlanBranchPublic() => IsCloseCancelPlanBranch();

    // Operator-facing close explanation. The cessation day on an Immediate screen IS today — the
    // server derives the inclusive end. Do not call VersionCloseRules.CeaseFrom (that would invent
    // today-1 purely to convert it back into today).
    private string BuildCloseEffectText()
    {
        var code = string.IsNullOrWhiteSpace(RoleCode) ? "—" : RoleCode.Trim();
        return $"Mã vai trò {code} chấm dứt hiệu lực từ hôm nay ({FormatDate(_dates.Today)}).";
    }

    private async Task ExecuteCancelAsync()
    {
        ++_autoLoadGeneration; // A1: leaving mutating mode must not let a stale auto-load land afterward.
        ++_saveGeneration; // FR-1b: Cancel must invalidate an in-flight save (Hủy is a different command).
        ++_functionCatalogGeneration; // FR-2a: Cancel changes card ownership; a late catalog must not land Resolved.
        Mode = RoleCardMode.ReadOnly;
        RestoreSnapshot(_snapshot);
        IsDirty = false;
        await Task.CompletedTask;
    }

    private string? ValidateFields()
    {
        if (Mode == RoleCardMode.Closing)
            return null;

        if (!RoleCodePattern.IsMatch(RoleCode.Trim()))
            return "Mã vai trò phải 5-20 ký tự a-z, 0-9 hoặc _ (chữ thường).";
        if (!RoleNamePattern.IsMatch(RoleName.Trim()))
            return "Tên vai trò phải 5-100 ký tự (chữ, số, khoảng trắng, - | ( ) _).";
        return null;
    }

    private async Task ExecuteSaveAsync()
    {
        var generation = ++_saveGeneration;
        var username = _currentUser.Username ?? "unknown";
        RefreshAdminFlagEditable();

        var authz = await _authorization.AuthorizeAsync(username, FunctionKey);
        if (generation != _saveGeneration)
            return;
        if (authz.IsError)
        {
            StatusMessage = string.Join("; ", authz.Errors.Select(FormatSaveError));
            Severity = StatusSeverity.Error;
            return;
        }

        if (Mode == RoleCardMode.Closing)
        {
            await ExecuteSaveCloseAsync(generation);
            return;
        }

        var validationError = ValidateFields();
        if (validationError is not null)
        {
            StatusMessage = validationError;
            Severity = StatusSeverity.Error;
            return;
        }

        // FR-1a: exhaustive dispatch. A bare else treated ReadOnly (Cancel mid-await) as Edit.
        // An unexpected mode must write nothing.
        if (Mode == RoleCardMode.Adding)
            await ExecuteSaveAddAsync(generation);
        else if (Mode == RoleCardMode.Editing)
            await ExecuteSaveEditAsync(generation);
        else
            return;
    }

    private async Task ExecuteSaveAddAsync(int generation)
    {
        var roleCode = RoleCode.Trim();
        var roleName = RoleName.Trim();
        var isAdminRole = IsAdminRole;
        var reason = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        var revokes = _grantIdsToRevoke.ToList();
        var grantsToAdd = DraftGrants.Select(d => new RolePermissionGrantToAdd(
            d.FunctionId,
            d.ScopeLevel,
            string.IsNullOrWhiteSpace(d.Note) ? null : d.Note)).ToList();

        var request = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), roleCode, roleName, isAdminRole, reason, revokes, grantsToAdd);

        var result = await _declaration.SaveRoleDeclarationAsync(request);
        if (generation != _saveGeneration)
            return;

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatSaveError));
            Severity = StatusSeverity.Error;
            return;
        }

        await FinishSaveSuccessAsync(result.Value.RoleId);
    }

    private async Task ExecuteSaveEditAsync(int generation)
    {
        if (_roleId is null)
        {
            StatusMessage = "Người dùng chọn vai trò trước khi sửa.";
            Severity = StatusSeverity.Error;
            return;
        }

        if (_currentVersionId is null)
        {
            StatusMessage = "Người dùng chọn vai trò trước khi sửa.";
            Severity = StatusSeverity.Error;
            return;
        }

        var request = BuildSaveRequest(_roleId.Value, _currentVersionId.Value, _snapshot.RoleCode);
        var result = await _declaration.SaveRoleDeclarationAsync(request);
        if (generation != _saveGeneration)
            return;
        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatSaveError));
            Severity = StatusSeverity.Error;
            return;
        }

        await FinishSaveSuccessAsync(_roleId.Value);
    }

    // A3: clear draft/revoke state unconditionally right after the write succeeds — a reload failure
    // below must not leave stale drafts/revokes that a retry would re-send, creating duplicate active
    // grants (no unique constraint on role_permission over the period). A reload/history-refresh failure
    // downgrades the banner to a Warning: the write itself already succeeded (§1.6 applies the same
    // treatment on the Close/Cancel path via FinishCloseSuccessAsync).
    private async Task FinishSaveSuccessAsync(long roleId)
    {
        _grantIdsToRevoke.Clear();
        DraftGrants = [];

        Mode = RoleCardMode.ReadOnly;
        var today = _dates.Today;
        var outcome = await LoadAsync(roleId, today);
        if (outcome == CardLoadOutcome.Superseded)
            return;
        if (outcome == CardLoadOutcome.Failed || Severity == StatusSeverity.Error)
        {
            StatusMessage = "Đã lưu. Dữ liệu hiển thị chưa cập nhật.";
            Severity = StatusSeverity.Warning;
            return;
        }

        StatusMessage = "Đã lưu.";
        Severity = StatusSeverity.Success;
        await RefreshHistoryPreservingMessageAsync();
    }

    private SaveRoleDeclarationRequest BuildSaveRequest(long roleId, long versionId, string expectedRoleCode) =>
        new(
            new RoleSaveTarget.ExistingRole(roleId, versionId, expectedRoleCode),
            RoleCode.Trim(),
            RoleName.Trim(),
            IsAdminRole,
            string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            _grantIdsToRevoke.ToList(),
            DraftGrants.Select(d => new RolePermissionGrantToAdd(
                d.FunctionId,
                d.ScopeLevel,
                string.IsNullOrWhiteSpace(d.Note) ? null : d.Note)).ToList());

    // §1 — Close/Cancel wiring. WHICH branch runs is derived server-side too (IRoleDeclarationService's
    // own doc comment); this screen's IsCloseCancelPlanBranch only decides which FORM/confirmation to
    // show, it never tells the server which branch to take.
    private async Task ExecuteSaveCloseAsync(int generation)
    {
        var roleId = _roleId!.Value;
        var versionId = _currentVersionId!.Value;

        if (IsCloseCancelPlanBranch())
        {
            var confirmed = await _confirmation.ConfirmAsync(
                "Đóng vai trò sẽ hủy kỳ hiệu lực của vai trò", Array.Empty<string>());
            if (generation != _saveGeneration)
                return;
            if (!confirmed)
                return;
        }

        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        var result = await _declaration.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(roleId, versionId, note));
        if (generation != _saveGeneration)
            return;

        if (result.IsError)
        {
            StatusMessage = string.Join("; ", result.Errors.Select(FormatCloseError));
            Severity = StatusSeverity.Error;
            return;
        }

        await FinishCloseSuccessAsync(roleId, CloseSuccessMessage);
    }

    private async Task FinishCloseSuccessAsync(long roleId, string successMessage)
    {
        Mode = RoleCardMode.ReadOnly;
        var today = _dates.Today;
        var stillVisible = await _roles.GetByIdentityAsync(roleId, today);
        if (stillVisible.IsError)
        {
            // Nothing left to show today (e.g. a cancel with no adjacent predecessor to restore).
            Clear();
        }
        else
        {
            await LoadAsync(roleId, today);
        }

        StatusMessage = successMessage;
        Severity = StatusSeverity.Success;
        await RefreshHistoryPreservingMessageAsync();
    }

    // Shared by both the Save path (FinishSaveSuccessAsync) and the Close/Cancel path
    // (FinishCloseSuccessAsync): a history-refresh failure must never mask a write that already
    // succeeded — downgrade to a Warning instead of letting LoadHistoryCoreAsync's own Error banner
    // stand (mirrors OrgUnitDeclarationViewModel.RefreshTreeAndHistoryAsync's preserved-message idiom).
    private async Task RefreshHistoryPreservingMessageAsync()
    {
        var preservedMessage = StatusMessage;
        var preservedSeverity = Severity;
        await RefreshHistoryAsync();
        if (Severity == StatusSeverity.Error)
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

    // §1.5: maps Close/Cancel error codes to Vietnamese. Enumerate VersionCloseRules.Codes.All from the
    // shared component (never re-typed from memory) plus every Role-only code; keep a fallback (an
    // unmapped code must not silently vanish) but every code below must be reached before it.
    private string FormatCloseError(Error error) => error.Code switch
    {
        // VersionClose date-rule codes are unreachable on this screen: CloseRoleDeclarationRequest
        // carries no date; the service derives null (CancelPlan) or today-1 (Retire). Fall to R-SYS.
        VersionCloseRules.Codes.VersionAlreadyEnded =>
            "Vai trò đã hết hiệu lực.",
        "Role.VersionNotFound" or "VersionedRepository.VersionNotFound" =>
            "Không tìm thấy phiên bản vai trò để đóng/hủy.",
        "Role.AdminFlagChangeNotAuthorized" =>
            AdminFlagChangeNotAuthorizedMessage,
        "VersionedRepository.BaseVersionRequired" =>
            "Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.",
        "VersionedRepository.DependentSetChanged" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "VersionedRepository.DependentNotEnlisted" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        // Defensive. Only CancelPlanAsync raises this, and the service routes an in-force version
        // to CloseVersionAsync (Retire) instead. Measured 2026-08-28.
        "VersionedRepository.NotAFuturePlan" =>
            StaleCardReloadMessage,
        "VersionedRepository.LockTimeout" =>
            "Dữ liệu đang được người dùng khác khai báo.",
        // Defensive. The engine raises this iff newTo < From || newTo >= To
        // (VersionedRepository.CloseVersionCoreAsync); VersionCloseRules.Validate rejects that
        // identical predicate before the repository is called, so a close that reaches the engine is
        // already inside the window. DERIVED from the two predicates, not measured: the 2026-08-28
        // probe witnesses only the already-ended payload (VersionAlreadyEnded), which is one branch.
        "VersionedRepository.InvalidShrink" =>
            "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
        "TemporalFk.DependentsUncovered" =>
            "Vai trò không được đóng do còn người dùng phụ thuộc.",
        "Authz.ScopeInsufficient" =>
            PermissionDeniedMessage,
        "Authz.NotGranted" =>
            PermissionDeniedMessage,
        "Function.DuplicateKey" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "User.DuplicateUsername" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "RolePermission.DuplicateGrant" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "Role.CascadeGrantNotProbed" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "CompositeWrite.NotEnlisted" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "AuditLogWriter.NoAmbientConnection" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        _ when error.Code.StartsWith("Authz.", StringComparison.Ordinal) =>
            PermissionDeniedMessage,
        _ => "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
    };

    // Test-only public wrapper (Fix Round 1 §6): the VersionCloseRules.Codes.All coverage test needs to
    // call FormatCloseError without duplicating its switch. No AST.Shell.Tests InternalsVisibleTo exists
    // on AST.Shell.csproj and adding one is out of this round's Scope (VM + tests only) — `public` is the
    // narrowest option available without touching that file.
    public string FormatCloseErrorPublic(Error error) => FormatCloseError(error);

    // Maps Save/Edit error codes to Vietnamese — distinct map from FormatCloseError (different code
    // surface), but reuses the SAME AdminFlagChangeNotAuthorizedMessage constant per §1.5's "same code,
    // same UX" rule.
    private static string FormatSaveError(Error error) => error.Code switch
    {
        "Role.AdminFlagChangeNotAuthorized" => AdminFlagChangeNotAuthorizedMessage,
        "Role.CodeInUse" or "Role.CodeOwnedByAnotherIdentity" =>
            "Mã vai trò đã được sử dụng.",
        "Role.CodeIdentityAmbiguous" =>
            "Mã vai trò bị trùng lặp với mã vai trò trong lịch sử do lỗi dữ liệu.",
        "Role.CodeOwnerNotDormant" =>
            "Mã vai trò bị trùng lặp với mã vai trò sẽ được sử dụng trong tương lai.",
        "RolePermission.OverlappingGrant" =>
            "Chức năng bị trùng lặp.",
        "TemporalFk.ParentGap" =>
            "Kỳ hiệu lực của quyền vượt ngoài kỳ hiệu lực của vai trò hoặc chức năng.",
        "EffectivePeriod.NoCoverage" =>
            "Quyền cần thu hồi đã hết hiệu lực.",
        // Defensive, and unreachable at the TYPE level: SaveRoleDeclarationRequest and
        // RolePermissionGrantToAdd carry no date or period field, so no caller can express an
        // invalid range; the service derives the period itself.
        "EffectivePeriod.InvalidRange" =>
            "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.",
        "EffectivePeriod.OverlappingVersions" =>
            "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
        "Authz.ScopeInsufficient" =>
            PermissionDeniedMessage,
        "Authz.NotGranted" =>
            PermissionDeniedMessage,
        "VersionedRepository.LockTimeout" =>
            "Dữ liệu đang được người dùng khác khai báo.",
        // Defensive, and unreachable on BOTH revoke branches, for two different reasons: CancelPlan
        // calls CancelPlanAsync, which never shrinks, so the code cannot be raised there at all;
        // Retire passes today-1, while the branch itself requires From < today and the grant is
        // resolved as applicable at today (so To >= today), giving From <= today-1 < To. DERIVED
        // from the branch algebra; the 2026-08-28 probe witnesses only the CancelPlan branch.
        "VersionedRepository.InvalidShrink" =>
            "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
        "Role.CodeOwnershipChanged" or "Role.VersionOutOfDate" or "Role.ExpectedCodeMismatch"
            // Defensive: a stale save is pre-empted by Role.VersionOutOfDate from
            // ReDecideIdentityAsync (measured 2026-08-28).
            or "VersionedRepository.NotAFuturePlan" or "VersionedRepository.VersionNotFound" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "VersionedRepository.DependentSetChanged" =>
            "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.",
        "VersionedRepository.DependentNotEnlisted" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        // UNRESOLVED, deliberately. Two raise sites: DeleteVersionAsync (deleting the last active
        // version) and AutoCutExclusivelyOwnedAsync (parent shrink) — a save-revoke payload witnesses
        // neither. An external review read both as unreachable from Save; a READ does not meet this
        // row's settled bar (proven by a run), so it stays unresolved, not guessed.
        "VersionedRepository.BaseVersionRequired" =>
            "Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.",
        "RolePermission.NotOwnedByRole" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
        "RolePermission.IdentityAlreadyVersioned" =>
            "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
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
        // Role.CodeNotAscii: unreachable — ValidateFields RoleCodePattern excludes non-ASCII before
        // the service is called (brief 163 step 6). No arm.
        _ when error.Code.StartsWith("Authz.", StringComparison.Ordinal) =>
            PermissionDeniedMessage,
        _ => "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
    };

    // Test-only public wrapper — FormatSaveError completeness + fall-through without InternalsVisibleTo.
    public string FormatSaveErrorPublic(Error error) => FormatSaveError(error);

    // Card load map: GetByIdentityAsync → EffectivePeriodResolver only (NoCoverage / OverlappingVersions).
    // No Authz.* on this path. Measured brief 163 Part 2.
    private static string FormatLoadError(Error error) => error.Code switch
    {
        "EffectivePeriod.NoCoverage" =>
            "Vai trò không hiệu lực tại ngày đã chọn.",
        "EffectivePeriod.OverlappingVersions" =>
            "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
        _ => "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.",
    };

    public string FormatLoadErrorPublic(Error error) => FormatLoadError(error);

    public async Task<CardLoadOutcome> LoadAsync(long roleId, DateOnly asOf)
    {
        // Bump before ClearCore/await so two clicks (or a stale in-flight auto-load) cannot let a slower
        // GetByIdentityAsync overwrite the card (or its error banner) after a newer load already owns it
        // — same idiom as _historyLoadGeneration. A1: also invalidates any in-flight auto-load.
        // Public Clear() bumps this too (direct-clear race); LoadAsync must call ClearCore, not Clear,
        // or it would supersede itself.
        var generation = ++_cardLoadGeneration;
        ++_autoLoadGeneration;
        ++_saveGeneration;
        ++_functionCatalogGeneration;
        ClearCore();

        var result = await _roles.GetByIdentityAsync(roleId, asOf);
        if (generation != _cardLoadGeneration)
            return CardLoadOutcome.Superseded;

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
            _roleId = dto.RoleId;
            _currentVersionId = dto.Id;
            RoleCode = dto.RoleCode;
            RoleName = dto.RoleName;
            SetLoadedPeriod(new Period(dto.EffectiveFrom, dto.EffectiveTo));
            _isAdminRole = dto.IsAdminRole;
            RaisePropertyChanged(nameof(IsAdminRole));
            Note = dto.Reason ?? string.Empty;
            Status = VersionStatusResolver.Resolve(
                dto.IsActive, dto.Status, dto.EffectiveFrom, dto.EffectiveTo, _dates.Today);
            Mode = RoleCardMode.ReadOnly;

            var period = new Period(dto.EffectiveFrom, dto.EffectiveTo);
            await LoadGrantsAndJournalAsync(dto.RoleId, period, generation);
            if (generation != _cardLoadGeneration)
                return CardLoadOutcome.Superseded;

            RefreshAdminFlagEditable();
            if (GrantsReadiness == GrantsReadiness.Failed)
                return CardLoadOutcome.Failed;

            if (Severity is not (StatusSeverity.Warning or StatusSeverity.Error))
            {
                StatusMessage = null;
                Severity = StatusSeverity.None;
            }

            return CardLoadOutcome.Loaded;
        }
        finally
        {
            if (generation == _cardLoadGeneration)
            {
                _isLoading = false;
                IsDirty = false;
            }
        }
    }

    public async Task<CardLoadOutcome> LoadFromHistoryRowAsync(RoleHistoryRow row)
    {
        // Same generation discipline as LoadAsync (FR-1b/FR-2a included): a history view is a card-
        // ownership change, so in-flight save / catalog / auto-load / card-load must not land on it.
        var generation = ++_cardLoadGeneration;
        ++_autoLoadGeneration;
        ++_saveGeneration;
        ++_functionCatalogGeneration;
        ClearCore();

        _isLoading = true;
        try
        {
            _roleId = row.RoleId;
            _currentVersionId = row.VersionId;
            RoleCode = row.RoleCode;
            RoleName = row.RoleName;
            SetLoadedPeriod(new Period(row.EffectiveFrom, row.EffectiveTo));
            _isAdminRole = row.IsAdminRole;
            RaisePropertyChanged(nameof(IsAdminRole));
            RaisePropertyChanged(nameof(GrantsGridOverlayText));
            Note = row.Note;
            // Row's own Status from MapHistoryRow (IsActive/Status) — do not re-resolve as-of today.
            Status = row.Status;
            Mode = RoleCardMode.ReadOnly;

            var period = new Period(row.EffectiveFrom, row.EffectiveTo);
            await LoadGrantsAndJournalAsync(row.RoleId, period, generation);
            if (generation != _cardLoadGeneration)
                return CardLoadOutcome.Superseded;

            RefreshAdminFlagEditable();
            if (GrantsReadiness == GrantsReadiness.Failed)
                return CardLoadOutcome.Failed;

            if (Severity is not (StatusSeverity.Warning or StatusSeverity.Error))
            {
                StatusMessage = null;
                Severity = StatusSeverity.None;
            }

            return CardLoadOutcome.Loaded;
        }
        finally
        {
            if (generation == _cardLoadGeneration)
            {
                _isLoading = false;
                IsDirty = false;
            }
        }
    }

    private async Task LoadGrantsAndJournalAsync(long roleId, Period period, int generation)
    {
        GrantsReadiness = GrantsReadiness.Loading;
        IReadOnlyList<RolePermissionVersionDto> grants;
        IReadOnlyList<FunctionVersionDto> catalog;
        try
        {
            grants = await _rolePermissions.GetActiveGrantsForPeriodAsync(roleId, period);
            if (generation != _cardLoadGeneration)
                return;

            catalog = await _functions.GetInScopeAsync(
                new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown"), _dates.Today);
            if (generation != _cardLoadGeneration)
                return;
        }
        catch (Exception)
        {
            if (generation == _cardLoadGeneration)
            {
                EffectiveGrants = [];
                PermissionJournal = [];
                _functionCatalog = [];
                RefreshFunctionPicker();
                GrantsReadiness = GrantsReadiness.Failed;
                StatusMessage = "Ứng dụng không tải được danh sách quyền.";
                Severity = StatusSeverity.Error;
            }

            return;
        }

        var byFunctionId = catalog.ToDictionary(f => f.FunctionId);
        EffectiveGrants = new ObservableCollection<RoleGrantRow>(
            grants.Select(g =>
            {
                byFunctionId.TryGetValue(g.FunctionId, out var fn);
                return new RoleGrantRow(
                    g.RolePermissionId,
                    g.FunctionId,
                    fn?.FunctionKey ?? $"#{g.FunctionId}",
                    fn?.DisplayName ?? $"#{g.FunctionId}",
                    g.ScopeLevel,
                    ScopeText(g.ScopeLevel));
            }));

        _functionCatalog = catalog;
        RefreshFunctionPicker();
        GrantsReadiness = GrantsReadiness.Resolved;

        // NEW (Fix Round 1): IRolePermissionRepository.GetHistoryAsync's only filter parameter is
        // `rolePermissionId` (a single GRANT identity), not `roleId` — the contract has no way to narrow
        // this read to "every grant of this role" server-side today. Reading everything and filtering
        // client-side below is therefore the only option the current interface allows, not an oversight;
        // narrowing it would need a new repository method, out of this round's Scope.
        try
        {
            var history = await _rolePermissions.GetHistoryAsync(null);
            if (generation != _cardLoadGeneration)
                return;

            PermissionJournal = new ObservableCollection<RolePermissionJournalRow>(
                history.Where(h => h.RoleId == roleId)
                    .OrderByDescending(h => h.RecordedAt)
                    .Select(h =>
                    {
                        byFunctionId.TryGetValue(h.FunctionId, out var fn);
                        return new RolePermissionJournalRow(
                            h.RolePermissionId,
                            // §5: a CANCELLED row keeps the ORIGINAL grant's recorded_at/recorded_by
                            // (CancelVersionAsync's no-predecessor path never restamps them) — labelling that
                            // as "the moment this was carried out" would misattribute the cancel to the
                            // creator, so it renders BLANK rather than the creator's timestamp. Revoke rows
                            // are unaffected (CloseVersionAsync inserts a fresh remnant row with the real
                            // actor/moment).
                            h.Status == VersionLifecycleStatus.Cancelled
                                ? string.Empty
                                : h.RecordedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                            h.RecordedBy,
                            h.OperationKind is { } kind
                                ? VersionOperationKindPresentation.ToVietnameseText(kind)
                                : string.Empty,
                            fn?.FunctionKey ?? $"#{h.FunctionId}",
                            ScopeText(h.ScopeLevel),
                            h.Reason ?? string.Empty);
                    }));
        }
        catch (Exception)
        {
            // Journal is display-only and must not gate mutation. A failure shows a non-blocking note.
            if (generation == _cardLoadGeneration)
            {
                PermissionJournal = [];
                StatusMessage = "Ứng dụng không tải được nhật ký quyền.";
                Severity = StatusSeverity.Warning;
            }
        }
    }

    private IReadOnlyList<FunctionVersionDto> _functionCatalog = [];

    private async Task RefreshFunctionCatalogAsync()
    {
        var generation = ++_functionCatalogGeneration;
        // Catalog refresh is the picker source, not the authority on the card's grants.
        // Loading→Resolved is legitimate only on a blank Adding card (no grants to be unready).
        // Captured before the await so a Cancel that restores a Failed card cannot be overwritten
        // even if this task still thinks it owns readiness — the generation check below is the stop.
        var ownsCardReadiness = Mode == RoleCardMode.Adding && _roleId is null;
        if (ownsCardReadiness)
            GrantsReadiness = GrantsReadiness.Loading;
        try
        {
            var catalog = await _functions.GetInScopeAsync(
                new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown"), _dates.Today);
            if (generation != _functionCatalogGeneration)
                return;

            _functionCatalog = catalog;
            RefreshFunctionPicker();
            if (ownsCardReadiness)
                GrantsReadiness = GrantsReadiness.Resolved;
        }
        catch (Exception)
        {
            // B1: a discarded fire-and-forget task's exception would otherwise be lost entirely, leaving
            // the picker silently empty with no diagnostic (mirrors
            // OrgUnitDeclarationViewModel.RefreshParentCandidatesAsync's own catch+generation guard).
            if (generation == _functionCatalogGeneration)
            {
                StatusMessage = "Ứng dụng không tải được danh mục chức năng.";
                Severity = StatusSeverity.Error;
                if (ownsCardReadiness)
                    GrantsReadiness = GrantsReadiness.Failed;
            }
        }
    }

    private void RefreshFunctionPicker()
    {
        var used = new HashSet<long>(
            EffectiveGrants.Select(g => g.FunctionId)
                .Concat(DraftGrants.Select(d => d.FunctionId)));

        FunctionPickerItems = new ObservableCollection<RoleFunctionPickerItem>(
            _functionCatalog
                .Where(f => !used.Contains(f.FunctionId))
                .Select(f => new RoleFunctionPickerItem(
                    f.FunctionId,
                    f.FunctionKey,
                    f.DisplayName,
                    ModuleGroup(f.FunctionKey)))
                .OrderBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.FunctionKey, StringComparer.OrdinalIgnoreCase));

        if (SelectedFunctionToAdd is { } selected && used.Contains(selected.FunctionId))
            SelectedFunctionToAdd = null;
    }

    private static string ModuleGroup(string functionKey)
    {
        var prefix = functionKey.Split('.', 2)[0];
        return prefix is "Iam" or "Transfer" or "Platform" or "Config" ? prefix : "Khác";
    }

    // C3: mapped to the ALREADY-LOCKED Vietnamese glossary (docs/glossary.md) — never invent wording.
    private static string ScopeText(ScopeLevel level) => level switch
    {
        ScopeLevel.Self => "bản thân",
        ScopeLevel.OwnOrgUnit => "đúng đơn vị",
        ScopeLevel.OwnOrgUnitAndDescendants => "đơn vị + con",
        ScopeLevel.Global => "toàn hệ thống",
        _ => level.ToString(),
    };

    private void ExecuteAddGrant()
    {
        if (!CanMutateGrants || SelectedFunctionToAdd is null)
            return;

        var item = SelectedFunctionToAdd;
        if (_functionCatalog.All(f => f.FunctionId != item.FunctionId))
            return;

        DraftGrants.Add(new RoleGrantDraftRow(
            item.FunctionId,
            item.FunctionKey,
            item.DisplayName,
            SelectedScopeToAdd,
            ScopeText(SelectedScopeToAdd),
            null));
        SelectedFunctionToAdd = null;
        RefreshFunctionPicker();
        MarkDirty();
    }

    private void ExecuteRemoveEffectiveGrant(RoleGrantRow? row)
    {
        if (row is null || !CanMutateGrants)
            return;
        if (!EffectiveGrants.Remove(row))
            return;
        _grantIdsToRevoke.Add(row.RolePermissionId);
        RefreshFunctionPicker();
        MarkDirty();
    }

    private void ExecuteRemoveDraftGrant(RoleGrantDraftRow? row)
    {
        if (row is null || !CanMutateGrants)
            return;
        if (!DraftGrants.Remove(row))
            return;
        RefreshFunctionPicker();
        MarkDirty();
    }

    // Code-typed auto-load (§3.2.3): OrgUnit does NOT implement this (DEBT-B1); Role does.
    private async Task TryAutoLoadByCodeAsync()
    {
        // A1: bump BEFORE any early return so a stale in-flight call — invalidated by a Clear/LoadAsync/
        // Cancel that started after this call — can never win a race and overwrite a cleared or newer
        // form, or silently discard an in-progress Add.
        var generation = ++_autoLoadGeneration;

        if (_isLoading)
            return;
        if (Mode is not (RoleCardMode.Adding or RoleCardMode.ReadOnly))
            return;
        if (Mode == RoleCardMode.ReadOnly && _roleId is not null)
            return;
        if (!RoleCodePattern.IsMatch(RoleCode))
            return;

        var today = _dates.Today;
        var code = RoleCode;
        try
        {
            var scope = new DataScope(ScopeLevel.Global, null, _currentUser.Username ?? "unknown");
            var matches = await _roles.GetInScopeAsync(scope, today);
            if (generation != _autoLoadGeneration)
                return;

            var hit = matches.FirstOrDefault(r =>
                string.Equals(r.RoleCode, code, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                return;

            var status = VersionStatusResolver.Resolve(
                hit.IsActive, hit.Status, hit.EffectiveFrom, hit.EffectiveTo, today);
            if (status is not (VersionStatus.Effective or VersionStatus.Pending))
                return;

            // Re-check BOTH immediately before the load call (A1): the await above is the only yield
            // point where a newer Clear/LoadAsync/Cancel could have superseded this call.
            if (generation != _autoLoadGeneration || Mode is not (RoleCardMode.Adding or RoleCardMode.ReadOnly))
                return;

            await LoadAsync(hit.RoleId, today);
        }
        catch
        {
            // Soft fail — typing continues; no banner spam on every keystroke.
        }
    }

    public async Task LoadAllHistoryAsync() => await LoadHistoryCoreAsync(++_historyLoadGeneration);

    public async Task RefreshHistoryAsync() => await LoadHistoryCoreAsync(++_historyLoadGeneration);

    private async Task LoadHistoryCoreAsync(int generation)
    {
        try
        {
            var versions = await _roles.GetHistoryAsync(null);
            if (generation != _historyLoadGeneration)
                return;
            HistoryRows = new ObservableCollection<RoleHistoryRow>(versions.Select(MapHistoryRow));
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

    private RoleHistoryRow MapHistoryRow(RoleVersionDto dto)
    {
        var status = VersionStatusResolver.Resolve(
            dto.IsActive, dto.Status, dto.EffectiveFrom, dto.EffectiveTo, _dates.Today);
        return new RoleHistoryRow(
            dto.RoleId,
            dto.Id,
            dto.EffectiveFrom,
            dto.EffectiveTo,
            FormatDate(dto.EffectiveFrom),
            FormatDate(dto.EffectiveTo),
            dto.RecordedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            VersionStatusPresentation.DisplayText(status),
            status,
            dto.RoleCode,
            dto.RoleName,
            dto.IsAdminRole,
            dto.OperationKind is { } kind
                ? VersionOperationKindPresentation.ToVietnameseText(kind)
                : string.Empty,
            dto.RecordedBy,
            dto.Reason ?? string.Empty);
    }

    private static string FormatDate(DateOnly d) =>
        d == Period.OpenEnd ? "Không xác định" : d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    // F7: single predicate home — View CollectionView filter and Shell.Tests both call this.
    public static bool MatchesHistoryFilter(RoleHistoryRow? row, string? filter)
    {
        if (row is null)
            return false;
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var needle = filter.Trim();
        return Contains(row.RoleCode, needle)
            || Contains(row.RoleName, needle)
            || Contains(row.Operation, needle)
            || Contains(row.RecordedBy, needle)
            || Contains(row.Note, needle);

        static bool Contains(string? haystack, string n) =>
            !string.IsNullOrEmpty(haystack) && haystack.Contains(n, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear()
    {
        ++_cardLoadGeneration;
        ++_autoLoadGeneration;
        ++_saveGeneration;
        ClearCore();
    }

    private void ClearCore()
    {
        ++_functionCatalogGeneration;
        _isLoading = true;
        try
        {
            _roleId = null;
            RaisePropertyChanged(nameof(GrantsGridOverlayText));
            _currentVersionId = null;
            RoleCode = string.Empty;
            RoleName = string.Empty;
            SetLoadedPeriod(null);
            _isAdminRole = false;
            RaisePropertyChanged(nameof(IsAdminRole));
            Status = VersionStatus.None;
            Note = string.Empty;
            EffectiveGrants = [];
            DraftGrants = [];
            PermissionJournal = [];
            _grantIdsToRevoke.Clear();
            _functionCatalog = [];
            GrantsReadiness = GrantsReadiness.Unresolved;
            // F4: the in-progress picker selection must also be wiped — otherwise it survives Clear()
            // (e.g. after leaving the screen) and risks an unintended grant on the next Edit.
            SelectedFunctionToAdd = null;
            SelectedScopeToAdd = ScopeLevel.Self;
            StatusMessage = null;
            Severity = StatusSeverity.None;
            Mode = RoleCardMode.ReadOnly;
            RefreshFunctionPicker();
        }
        finally
        {
            _isLoading = false;
            IsDirty = false;
        }
    }

    private void SetLoadedPeriod(Period? period)
    {
        _loadedPeriod = period;
        EffectivePeriodText = period is null
            ? string.Empty
            : $"Hiệu lực từ {FormatDate(period.Value.From)} đến {FormatDate(period.Value.To)}";
    }

    public void ClearHistory()
    {
        ++_historyLoadGeneration;
        HistoryRows = [];
    }
}
