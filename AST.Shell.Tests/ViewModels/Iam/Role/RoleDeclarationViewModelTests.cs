using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation;
using AST.Shell.ViewModels.Iam.Role;
using ErrorOr;
using FluentAssertions;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Shell.Tests.ViewModels.Iam.Role;

public class RoleDeclarationViewModelTests
{
    private static readonly DateOnly Today = new(2026, 8, 9);

    private sealed class FixedDates(DateOnly today) : IBusinessDateProvider
    {
        public DateOnly Today { get; } = today;
    }

    private sealed class AdvancingDates(DateOnly today) : IBusinessDateProvider
    {
        public DateOnly Today { get; set; } = today;
    }

    private sealed class FakeCurrentUser(string? username) : ICurrentWindowsUser
    {
        public string? Username { get; } = username;
    }

    private sealed class FakeAuthorizationService : IAuthorizationService
    {
        public ErrorOr<DataScope> AuthorizeResult { get; set; } =
            new DataScope(ScopeLevel.Global, null, "tester");

        public TaskCompletionSource<ErrorOr<DataScope>>? GateAuthorize { get; set; }

        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => Task.FromResult(true);

        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) =>
            GateAuthorize is { } gate ? gate.Task : Task.FromResult(AuthorizeResult);
    }

    private sealed class FakeBreakGlassPolicy(bool isBreakGlass) : IBreakGlassPolicy
    {
        public bool IsBreakGlassAdmin(string username) => isBreakGlass;
    }

    private sealed class FakeConfirmationPrompt : IConfirmationPrompt
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCallCount { get; private set; }
        public string? LastMessage { get; private set; }
        public TaskCompletionSource<bool>? GateConfirm { get; set; }

        public Task<bool> ConfirmAsync(string message, IReadOnlyList<string> details)
        {
            ConfirmCallCount++;
            LastMessage = message;
            return GateConfirm is { } gate ? gate.Task : Task.FromResult(ConfirmResult);
        }
    }

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public IReadOnlyList<RoleVersionDto> InScopeResult { get; set; } = [];
        public IReadOnlyList<RoleVersionDto> HistoryResult { get; set; } = [];
        public Exception? ThrowOnGetHistory { get; set; }
        public ErrorOr<RoleVersionDto> ByIdentityResult { get; set; } = Error.NotFound();
        // B2(i): lets a test suspend GetByIdentityAsync mid-LoadAsync to race a Clear()/second load against it.
        public TaskCompletionSource<ErrorOr<RoleVersionDto>>? GateByIdentity { get; set; }

        public Task<IReadOnlyList<RoleVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf) =>
            Task.FromResult(InScopeResult);

        public Task<ErrorOr<RoleVersionDto>> GetByIdentityAsync(long roleId, DateOnly asOf) =>
            GateByIdentity is { } gate ? gate.Task : Task.FromResult(ByIdentityResult);

        public Task<IReadOnlyList<RoleVersionDto>> GetHistoryAsync(long? roleId = null) =>
            ThrowOnGetHistory is { } ex
                ? Task.FromException<IReadOnlyList<RoleVersionDto>>(ex)
                : Task.FromResult(HistoryResult);
    }

    private sealed class FakeRolePermissionRepository : IRolePermissionRepository
    {
        public IReadOnlyList<RolePermissionVersionDto> ActiveGrants { get; set; } = [];
        public IReadOnlyList<RolePermissionVersionDto> History { get; set; } = [];
        // B1(i): forces LoadGrantsAndJournalAsync's first dependency call to throw.
        public Exception? ThrowOnGetActiveGrants { get; set; }
        public TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>? GateActiveGrants { get; set; }
        // B2(ii): lets a test suspend LoadGrantsAndJournalAsync's history read to race a newer load's
        // error banner against an older load's late (unchecked) continuation.
        public TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>? GateHistory { get; set; }
        public Exception? ThrowOnGetGrantHistory { get; set; }

        public Task<IReadOnlyList<RolePermissionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf) =>
            Task.FromResult<IReadOnlyList<RolePermissionVersionDto>>([]);
        public Task<ErrorOr<RolePermissionVersionDto>> GetByIdentityAsync(long rolePermissionId, DateOnly asOf) =>
            Task.FromResult<ErrorOr<RolePermissionVersionDto>>(Error.NotFound());
        public Task<IReadOnlyList<RolePermissionVersionDto>> GetHistoryAsync(long? rolePermissionId = null) =>
            ThrowOnGetGrantHistory is { } ex
                ? Task.FromException<IReadOnlyList<RolePermissionVersionDto>>(ex)
                : GateHistory is { } gate ? gate.Task : Task.FromResult(History);
        public Task<IReadOnlyList<RolePermissionVersionDto>> GetActiveGrantsForPeriodAsync(long roleId, Period period) =>
            ThrowOnGetActiveGrants is { } ex
                ? Task.FromException<IReadOnlyList<RolePermissionVersionDto>>(ex)
                : GateActiveGrants is { } gate ? gate.Task : Task.FromResult(ActiveGrants);
        public Task<ErrorOr<RolePermissionVersionDto>> GetGrantAsync(long roleId, long functionId, DateOnly asOf) =>
            Task.FromResult<ErrorOr<RolePermissionVersionDto>>(Error.NotFound());
        public Task<ErrorOr<UpsertResult>> UpsertAsync(
            long rolePermissionId, Period period, long roleId, long functionId, ScopeLevel scopeLevel,
            VersionOperationKind operationKind, OperationDate operationDate, string recordedBy, string? reason) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFunctionRepository : IFunctionRepository
    {
        public IReadOnlyList<FunctionVersionDto> InScope { get; set; } = [];
        public Exception? ThrowOnGetInScope { get; set; }
        public TaskCompletionSource<IReadOnlyList<FunctionVersionDto>>? GateGetInScope { get; set; }

        public Task<IReadOnlyList<FunctionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf) =>
            ThrowOnGetInScope is { } ex
                ? Task.FromException<IReadOnlyList<FunctionVersionDto>>(ex)
                : GateGetInScope is { } gate ? gate.Task : Task.FromResult(InScope);
        public Task<ErrorOr<FunctionVersionDto>> GetByIdentityAsync(long functionId, DateOnly asOf) =>
            Task.FromResult<ErrorOr<FunctionVersionDto>>(Error.NotFound());
        public Task<ErrorOr<FunctionVersionDto>> GetByKeyAsync(string functionKey, DateOnly asOf) =>
            Task.FromResult<ErrorOr<FunctionVersionDto>>(Error.NotFound());
        public Task<ErrorOr<UpsertResult>> UpsertAsync(
            long functionId, Period period, string functionKey, string businessCode,
            string displayName, string menuGroup, string navTarget,
            string recordedBy, string? reason) => throw new NotSupportedException();
        public Task<ErrorOr<FunctionCreateOutcome>> CreateAsync(
            Period period, string functionKey, string businessCode,
            string displayName, string menuGroup, string navTarget,
            string recordedBy, string? reason) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAllKnownFunctionKeysAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeDeclarationService : IRoleDeclarationService
    {
        public int SaveCallCount { get; private set; }
        public SaveRoleDeclarationRequest? LastRequest { get; private set; }
        public ErrorOr<SaveRoleDeclarationResult> SaveResult { get; set; } =
            new SaveRoleDeclarationResult(101, 1, false, [], []);
        public Exception? ThrowOnSave { get; set; }
        public TaskCompletionSource<ErrorOr<SaveRoleDeclarationResult>>? GateSave { get; set; }

        public Task<ErrorOr<SaveRoleDeclarationResult>> SaveRoleDeclarationAsync(SaveRoleDeclarationRequest request)
        {
            SaveCallCount++;
            LastRequest = request;
            if (ThrowOnSave is { } ex)
                throw ex;
            return GateSave is { } gate ? gate.Task : Task.FromResult(SaveResult);
        }

        public int CloseCallCount { get; private set; }
        public CloseRoleDeclarationRequest? LastCloseRequest { get; private set; }
        public ErrorOr<UpsertResult> CloseResult { get; set; } = new UpsertResult(1, [], []);

        public Action? OnClose { get; set; }
        public TaskCompletionSource<ErrorOr<UpsertResult>>? GateClose { get; set; }

        public Task<ErrorOr<UpsertResult>> CloseRoleDeclarationAsync(CloseRoleDeclarationRequest request)
        {
            CloseCallCount++;
            LastCloseRequest = request;
            OnClose?.Invoke();
            return GateClose is { } gate ? gate.Task : Task.FromResult(CloseResult);
        }
    }

    private sealed class Harness
    {
        public required RoleDeclarationViewModel Vm { get; init; }
        public required FakeRoleRepository Roles { get; init; }
        public required FakeRolePermissionRepository Permissions { get; init; }
        public required FakeFunctionRepository Functions { get; init; }
        public required FakeDeclarationService Declaration { get; init; }
        public required FakeBreakGlassPolicy BreakGlass { get; init; }
        public required FakeAuthorizationService Authorization { get; init; }
        public required FakeConfirmationPrompt Confirmation { get; init; }
        public required IBusinessDateProvider Dates { get; init; }
    }

    private static Harness Build(bool breakGlass = false, IBusinessDateProvider? dates = null)
    {
        var roles = new FakeRoleRepository();
        var permissions = new FakeRolePermissionRepository();
        var functions = new FakeFunctionRepository
        {
            InScope =
            [
                Fn(1, "Iam.OrgUnit.Declare", "Khai báo đơn vị"),
                Fn(2, "Iam.Role.Declare", "Khai báo vai trò"),
                Fn(3, "Transfer.Create", "Tạo điều chuyển"),
                Fn(4, "Weird.Thing", "Khác sample"),
            ],
        };
        var declaration = new FakeDeclarationService();
        var breakGlassPolicy = new FakeBreakGlassPolicy(breakGlass);
        var authorization = new FakeAuthorizationService();
        var confirmation = new FakeConfirmationPrompt();
        var dateProvider = dates ?? new FixedDates(Today);
        var vm = new RoleDeclarationViewModel(
            roles, permissions, functions, declaration,
            dateProvider, new FakeCurrentUser("tester"),
            authorization, breakGlassPolicy, confirmation);
        return new Harness
        {
            Vm = vm,
            Roles = roles,
            Permissions = permissions,
            Functions = functions,
            Declaration = declaration,
            BreakGlass = breakGlassPolicy,
            Authorization = authorization,
            Confirmation = confirmation,
            Dates = dateProvider,
        };
    }

    private static FunctionVersionDto Fn(long id, string key, string name) =>
        new(id, id, new DateOnly(2000, 1, 1), Period.OpenEnd, true, key, "B", name, "G", "Nav",
            DateTime.UtcNow, "seed", null);

    private static RoleVersionDto Role(
        long roleId, string code, string name, bool active = true,
        VersionLifecycleStatus status = VersionLifecycleStatus.Normal,
        DateOnly? from = null, DateOnly? to = null, VersionOperationKind? kind = VersionOperationKind.Add,
        long versionId = 10, bool isAdminRole = false) =>
        new(versionId, roleId, from ?? Today, to ?? Period.OpenEnd, active, code, name, isAdminRole,
            DateTime.UtcNow, "tester", null, status, kind);

    private static RoleHistoryRow HistoryRow(
        long roleId = 1,
        long versionId = 10,
        DateOnly? from = null,
        DateOnly? to = null,
        string code = "clerk_role", string name = "Vai trò thư ký", string operation = "Sửa",
        string recordedBy = "tester", string note = "",
        VersionStatus status = VersionStatus.Effective,
        bool isAdminRole = false) =>
        new(roleId, versionId, from ?? Today, to ?? Period.OpenEnd,
            "01/01/2026", "Không xác định", "01/01/2026 08:00", "Hiệu lực",
            status, code, name, isAdminRole, operation, recordedBy, note);

    private static void FillValidAddForm(RoleDeclarationViewModel vm)
    {
        vm.BeginAddCommand.Execute();
        vm.RoleCode = "clerk_role";
        vm.RoleName = "Vai trò thư ký";
    }

    [Fact]
    public void ButtonMatrix_EmptyCard_OnlyAddIsEnabled()
    {
        var h = Build();
        h.Vm.CanAdd.Should().BeTrue();
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanClose.Should().BeFalse();
        h.Vm.CanSave.Should().BeFalse();
        h.Vm.CanCancel.Should().BeFalse();
        h.Vm.GrantsGridOverlayText.Should().BeNull("FR-7b: a blank card has nothing to be ready");
    }

    [Fact]
    public async Task ButtonMatrix_Effective_EnablesAddEditClose()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);

        h.Vm.Status.Should().Be(VersionStatus.Effective);
        h.Vm.CanAdd.Should().BeTrue();
        h.Vm.CanEdit.Should().BeTrue();
        h.Vm.CanClose.Should().BeTrue();
        h.Vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task MutatingMode_DisablesAddEditCloseAndEnablesCancelSave()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();

        h.Vm.Mode.Should().Be(RoleCardMode.Editing);
        h.Vm.CanAdd.Should().BeFalse();
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanClose.Should().BeFalse();
        h.Vm.CanCancel.Should().BeTrue();
        h.Vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public void RoleCode_AutoLowercases()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.RoleCode = "Clerk_Role";
        h.Vm.RoleCode.Should().Be("clerk_role");
    }

    [Fact]
    public void IsAdminFlagEditable_FalseForOrdinaryAdmin_TrueForBreakGlassOnlyInAddOrEdit()
    {
        Build(breakGlass: false).Vm.IsAdminFlagEditable.Should().BeFalse();
        var glass = Build(breakGlass: true);
        glass.Vm.IsAdminFlagEditable.Should().BeFalse("break-glass must not unlock the flag in ReadOnly");
        glass.Vm.BeginAddCommand.Execute();
        glass.Vm.IsAdminFlagEditable.Should().BeTrue();
    }

    [Fact]
    public void IsAdminRole_Setter_RejectsWriteWhenNotBreakGlass()
    {
        var h = Build(breakGlass: false);
        h.Vm.BeginAddCommand.Execute();

        h.Vm.IsAdminRole = true;

        h.Vm.IsAdminRole.Should().BeFalse("the setter must REJECT the write, not merely leave the value unread");
    }

    [Fact]
    public void IsAdminRole_Setter_AllowsWriteWhenBreakGlass()
    {
        var h = Build(breakGlass: true);
        h.Vm.BeginAddCommand.Execute();

        h.Vm.IsAdminRole = true;

        h.Vm.IsAdminRole.Should().BeTrue();
    }

    [Fact]
    public async Task CancelCommand_RestoresIdentityAndGrantDrafts()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ActiveGrants =
        [
            new RolePermissionVersionDto(1, 11, Today, Period.OpenEnd, true, 5, 1, ScopeLevel.Global,
                DateTime.UtcNow, "tester", null, VersionLifecycleStatus.Normal, VersionOperationKind.Add),
        ];
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleName = "Tên đã đổi";
        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.SelectedScopeToAdd = ScopeLevel.Self;
        h.Vm.AddGrantCommand.Execute();
        h.Vm.DraftGrants.Should().HaveCount(1);
        h.Vm.RemoveEffectiveGrantCommand.Execute(h.Vm.EffectiveGrants[0]);
        h.Vm.EffectiveGrants.Should().BeEmpty();

        await h.Vm.CancelCommand.Execute();

        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
        h.Vm.RoleName.Should().Be("Quản trị viên");
        h.Vm.EffectiveGrants.Should().HaveCount(1);
        h.Vm.DraftGrants.Should().BeEmpty();
        h.Vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_RevertsInProgressPickerSelection()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();

        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.SelectedScopeToAdd = ScopeLevel.Global;

        await h.Vm.CancelCommand.Execute();

        h.Vm.SelectedFunctionToAdd.Should().BeNull();
        h.Vm.SelectedScopeToAdd.Should().Be(ScopeLevel.Self);
    }

    [Fact]
    public void Clear_ResetsPickerSelection()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First();
        h.Vm.SelectedScopeToAdd = ScopeLevel.Global;

        h.Vm.Clear();

        h.Vm.SelectedFunctionToAdd.Should().BeNull();
        h.Vm.SelectedScopeToAdd.Should().Be(ScopeLevel.Self);
    }

    [Fact]
    public async Task Save_Add_CallsDeclarationOnce_WithNewRoleTarget()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Roles.ByIdentityResult = Role(101, "clerk_role", "Vai trò thư ký");
        h.Declaration.SaveResult = new SaveRoleDeclarationResult(101, 1, false, [], []);

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(1);
        h.Declaration.LastRequest!.Target.Should().BeOfType<RoleSaveTarget.NewRole>();
        h.Declaration.LastRequest.RoleCode.Should().Be("clerk_role");
        h.Declaration.LastRequest.Reason.Should().BeNull();
        h.Vm.Severity.Should().Be(StatusSeverity.Success);
        h.Vm.StatusMessage.Should().Be("Đã lưu.");
    }

    [Fact]
    public async Task Save_Add_EmptyNote_IsAllowed()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Vm.Note.Should().BeEmpty();
        h.Roles.ByIdentityResult = Role(101, "clerk_role", "Vai trò thư ký");

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(1);
        h.Vm.Severity.Should().Be(StatusSeverity.Success);
    }

    [Fact]
    public async Task Save_Add_OnError_ShowsErrorAndStaysInAdding()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Declaration.SaveResult = Error.Validation("Role.CodeInUse", "code in use");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.Mode.Should().Be(RoleCardMode.Adding);
    }

    [Fact]
    public async Task Save_Add_ThrowingDeclarationService_PropagatesException()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Declaration.ThrowOnSave = new InvalidOperationException("boom");

        var act = async () => await h.Vm.SaveCommand.Execute();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Save_AuthorizationDenied_DoesNotCallDeclarationService()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Authorization.AuthorizeResult = Error.Forbidden("Authz.ScopeInsufficient", "no scope");

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(0);
        h.Vm.Severity.Should().Be(StatusSeverity.Error);
    }

    [Fact]
    public async Task Save_InvalidRoleCode_ShowsValidationMessage_DoesNotCallService()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.RoleCode = "ab";
        h.Vm.RoleName = "Tên hợp lệ đủ dài ký tự";

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Contain("Mã vai trò");
        h.Declaration.SaveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Save_InvalidRoleName_ShowsValidationMessage_DoesNotCallService()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.RoleCode = "clerk_role";
        h.Vm.RoleName = "no";

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Contain("Tên vai trò");
        h.Declaration.SaveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Save_Edit_RevokeAndAdd_InOneCall()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ActiveGrants =
        [
            new RolePermissionVersionDto(1, 11, Today, Period.OpenEnd, true, 5, 1, ScopeLevel.Global,
                DateTime.UtcNow, "tester", null, VersionLifecycleStatus.Normal, VersionOperationKind.Add),
        ];
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RemoveEffectiveGrantCommand.Execute(h.Vm.EffectiveGrants[0]);
        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.SelectedScopeToAdd = ScopeLevel.OwnOrgUnit;
        h.Vm.AddGrantCommand.Execute();
        h.Vm.RoleCode = "clerk_role";
        h.Declaration.SaveResult = new SaveRoleDeclarationResult(5, 2, false, [12], [11]);

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(1);
        h.Declaration.LastRequest!.GrantIdentityIdsToRevoke.Should().Equal(11);
        h.Declaration.LastRequest.GrantsToAdd.Should().HaveCount(1);
        h.Declaration.LastRequest.GrantsToAdd[0].FunctionId.Should().Be(2);
        h.Declaration.LastRequest.GrantsToAdd[0].ScopeLevel.Should().Be(ScopeLevel.OwnOrgUnit);
        h.Declaration.LastRequest.Target.Should().BeOfType<RoleSaveTarget.ExistingRole>();
        var existingTarget = (RoleSaveTarget.ExistingRole)h.Declaration.LastRequest.Target;
        existingTarget.RoleId.Should().Be(5);
        existingTarget.ExpectedRoleVersionId.Should().Be(10);
        existingTarget.ExpectedRoleCode.Should().Be("admin_role", "loaded code at BeginEdit, not the edited textbox");
        h.Declaration.LastRequest.RoleCode.Should().Be("clerk_role");
    }

    [Fact]
    public async Task Save_Edit_OnError_ShowsErrorAndStaysInEditing()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleName = "Tên mới";
        h.Declaration.SaveResult = Error.Validation("Role.CodeInUse", "in use");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.Mode.Should().Be(RoleCardMode.Editing);
    }

    [Fact]
    public async Task Save_Edit_AdminFlagChangeDenied_ShowsMappedMessage()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Declaration.SaveResult = Error.Forbidden("Role.AdminFlagChangeNotAuthorized", "raw engine text");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be(RoleDeclarationViewModel.AdminFlagChangeNotAuthorizedMessage);
    }

    [Fact]
    public async Task Save_Edit_ReloadFails_ClearsDraftsAndRevokes_DowngradesToWarning()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ActiveGrants =
        [
            new RolePermissionVersionDto(1, 11, Today, Period.OpenEnd, true, 5, 1, ScopeLevel.Global,
                DateTime.UtcNow, "tester", null, VersionLifecycleStatus.Normal, VersionOperationKind.Add),
        ];
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RemoveEffectiveGrantCommand.Execute(h.Vm.EffectiveGrants[0]);
        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.AddGrantCommand.Execute();

        // The write succeeds, but the post-save reload fails (identity vanished/DB hiccup).
        h.Roles.ByIdentityResult = Error.NotFound("Role.VersionNotFound", "reload failed");

        await h.Vm.SaveCommand.Execute();

        h.Vm.DraftGrants.Should().BeEmpty("A3: drafts must clear unconditionally on save success, even if the reload after it fails");
        h.Vm.Severity.Should().Be(StatusSeverity.Warning, "the write succeeded — only the reload failed");

        // B6/LOW-1: the transient reload failure clears — the operator reloads the card and retries Save.
        // The retry's request must not re-send anything from the FIRST save (that would create duplicate
        // active grants — no unique constraint on role_permission over the period).
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Vai trò thư ký");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(2);
        h.Declaration.LastRequest!.GrantIdentityIdsToRevoke.Should().BeEmpty(
            "nothing from the first save's revoke list may be re-sent on retry");
        h.Declaration.LastRequest.GrantsToAdd.Should().BeEmpty(
            "nothing from the first save's draft grants may be re-sent on retry — no duplicate active grants");
    }

    [Fact]
    public async Task FunctionPicker_HidesFunctionsAlreadyOnForm()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ActiveGrants =
        [
            new RolePermissionVersionDto(1, 11, Today, Period.OpenEnd, true, 5, 1, ScopeLevel.Global,
                DateTime.UtcNow, "tester", null, VersionLifecycleStatus.Normal, VersionOperationKind.Add),
        ];
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.FunctionPickerItems.Select(i => i.FunctionId).Should().NotContain(1);

        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.AddGrantCommand.Execute();
        h.Vm.FunctionPickerItems.Select(i => i.FunctionId).Should().NotContain(2);
        h.Vm.FunctionPickerItems.Should().Contain(i => i.Group == "Khác");
    }

    [Fact]
    public void ScopeText_MapsToVietnameseGlossary()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.SelectedFunctionToAdd = h.Vm.FunctionPickerItems.First(i => i.FunctionId == 2);
        h.Vm.SelectedScopeToAdd = ScopeLevel.OwnOrgUnitAndDescendants;

        h.Vm.AddGrantCommand.Execute();

        h.Vm.DraftGrants.Single().ScopeText.Should().Be("đơn vị + con");
    }

    [Fact]
    public void BeginAdd_FunctionCatalogLoadFails_ShowsErrorBanner()
    {
        var h = Build();
        h.Functions.ThrowOnGetInScope = new InvalidOperationException("db down");

        h.Vm.BeginAddCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Ứng dụng không tải được danh mục chức năng.");
    }

    [Fact]
    public async Task LoadAsync_Error_ShowsErrorBanner()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Error.NotFound("Role.VersionNotFound", "not found");

        await h.Vm.LoadAsync(999, Today);

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadAsync_ClearsPreviousStateBeforeLoadingNew()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.RoleCode.Should().Be("admin_role");

        h.Roles.ByIdentityResult = Error.NotFound("Role.VersionNotFound", "not found");
        await h.Vm.LoadAsync(999, Today);

        h.Vm.RoleCode.Should().BeEmpty("B4: LoadAsync must Clear() the previous role BEFORE attempting the new load");
        h.Vm.Severity.Should().Be(StatusSeverity.Error);
    }

    // ---- B1 (HIGH-2): fail-CLOSED on a grants/catalog dependency failure ----

    [Fact]
    public async Task LoadAsync_ActiveGrantsRepositoryThrows_ShowsErrorBanner_AndBlocksEdit()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");

        var act = async () => await h.Vm.LoadAsync(5, Today);

        await act.Should().NotThrowAsync(
            "a dependency failure while loading grants must surface as an Error banner, not crash LoadAsync");
        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.EffectiveGrants.Should().BeEmpty();
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse(
            "a role card that failed to load its grants must not be editable — fail-open on a permission screen");
    }

    [Fact]
    public async Task LoadAsync_FunctionCatalogRepositoryThrows_ShowsErrorBanner_AndBlocksEdit()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Functions.ThrowOnGetInScope = new InvalidOperationException("db down");

        var act = async () => await h.Vm.LoadAsync(5, Today);

        await act.Should().NotThrowAsync(
            "a dependency failure while loading the function catalog must surface as an Error banner, not crash LoadAsync");
        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.EffectiveGrants.Should().BeEmpty();
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse();
    }

    [Fact]
    public void CodeTypedAutoLoad_GrantsRepositoryThrows_DoesNotPresentAsLoadedRoleWithZeroGrants()
    {
        var h = Build();
        h.Roles.InScopeResult = [Role(8, "clerk_role", "Vai trò thư ký")];
        h.Roles.ByIdentityResult = Role(8, "clerk_role", "Vai trò thư ký");
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");

        h.Vm.BeginAddCommand.Execute();
        // Every fake resolves synchronously, so the auto-load fire-and-forget chain (including the
        // swallowing catch in TryAutoLoadByCodeAsync) has already completed by the time this setter
        // returns — no fallback delay needed (mirrors CodeTypedAutoLoad_LoadsEffectiveMatch's own A2 note).
        h.Vm.RoleCode = "clerk_role";

        h.Vm.Severity.Should().Be(StatusSeverity.Error,
            "a dependency failure reached through the code-typed auto-load path must not leave the card " +
            "silently presenting as a loaded role with zero grants");
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse();
    }

    // ---- B8/B9/B10: the fail-CLOSED gate must be independent of Severity ----
    //
    // CanEdit/CanSave depend only on Mode/Status today. FinishSaveSuccessAsync (:683-694) deliberately
    // downgrades a post-write reload's Error banner to Warning (locked precedent), and
    // FinishCloseSuccessAsync (:780-796) unconditionally overwrites the banner with Success. A gate that
    // reads Severity therefore CANNOT distinguish "reload partially failed" from "reload was fine" in
    // either path. These three tests pin the eventual fix to an explicit fully-loaded state instead.

    [Fact]
    public async Task Save_Edit_PostSaveReload_GrantsFail_LockedWarningBannerPreserved_ButCardNotEditable()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleName = "Tên mới";

        // The write itself succeeds, and the post-save reload's IDENTITY fetch also succeeds — only the
        // grants read inside that same reload fails.
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");

        var act = async () => await h.Vm.SaveCommand.Execute();

        await act.Should().NotThrowAsync(
            "a grants-read failure during the post-save reload must degrade to the locked Warning banner, not crash Save");
        h.Vm.StatusMessage.Should().Be("Đã lưu. Dữ liệu hiển thị chưa cập nhật.");
        h.Vm.Severity.Should().Be(StatusSeverity.Warning);
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse(
            "a partially-reloaded card (grants failed) must not be editable regardless of banner severity — " +
            "a Severity == Warning check alone would wrongly allow this");
    }

    [Fact]
    public async Task Close_Retire_PostCloseReload_GrantsFail_SuccessBannerStands_ButCardNotEditable()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();

        // Close succeeds; FinishCloseSuccessAsync's post-close identity re-fetch also succeeds (the role
        // stays visible today) — only the reload's grants read fails.
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");

        var act = async () => await h.Vm.SaveCommand.Execute();

        await act.Should().NotThrowAsync("a grants-read failure during the post-close reload must not crash Save");
        h.Vm.Severity.Should().Be(StatusSeverity.Success,
            "FinishCloseSuccessAsync unconditionally overwrites the banner with Success after a close — this is " +
            "exactly why a Severity-based gate is impossible: there is no Error/Warning value left to check here");
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse(
            "the card must still be blocked from mutation even though the banner reads Success");
    }

    [Fact]
    public async Task LoadAsync_AfterAPartialLoadFailure_ASubsequentSuccessfulLoad_ReEnablesMutation()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");
        try
        {
            await h.Vm.LoadAsync(5, Today);
        }
        catch
        {
            // Today's code has no catch around the grants read (B1) — swallow here so the test can still
            // exercise the RECOVERY half of the fix regardless of whether B1's crash is fixed yet.
        }

        h.Permissions.ThrowOnGetActiveGrants = null;
        await h.Vm.LoadAsync(5, Today);

        h.Vm.BeginEditCommand.CanExecute().Should().BeTrue(
            "a subsequent successful load must re-enable mutation — the fail-closed gate must not become a " +
            "one-way latch that permanently bricks the screen after a single transient failure");
    }

    // ---- B2 (MED-1): load generations ----

    [Fact]
    public async Task Clear_DuringInFlightLoad_KeepsCardCleared_AfterTheLoadsGateReleases()
    {
        var h = Build();
        var gate = new TaskCompletionSource<ErrorOr<RoleVersionDto>>();
        h.Roles.GateByIdentity = gate;

        var loadTask = h.Vm.LoadAsync(5, Today);
        h.Vm.Clear();
        gate.SetResult(Role(5, "admin_role", "Quản trị viên"));
        await loadTask;

        h.Vm.RoleCode.Should().BeEmpty(
            "Clear() must invalidate any in-flight load's generation so a slower GetByIdentityAsync cannot resurrect the cleared card");
        h.Vm.Status.Should().Be(VersionStatus.None);
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse();
    }

    [Fact]
    public async Task NewerLoadsErrorBanner_SurvivesAnOlderLoadsLateUncheckedContinuation()
    {
        var h = Build();
        var gate = new TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>();
        h.Permissions.GateHistory = gate;
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");

        // Older load: proceeds synchronously through grants + catalog, then suspends on the history read.
        var olderLoad = h.Vm.LoadAsync(5, Today);

        // Newer load: not gated, resolves as a failure and sets the Error banner.
        h.Permissions.GateHistory = null;
        h.Roles.ByIdentityResult = Error.NotFound("Role.VersionNotFound", "not found");
        await h.Vm.LoadAsync(999, Today);
        h.Vm.Severity.Should().Be(StatusSeverity.Error);

        // Release the OLDER load's history read. LoadGrantsAndJournalAsync correctly no-ops on the stale
        // generation, but LoadAsync's own continuation right after it does NOT re-check the generation.
        gate.SetResult([]);
        await olderLoad;

        h.Vm.Severity.Should().Be(StatusSeverity.Error,
            "an older load's late, unchecked continuation must not clear a newer load's error banner");
    }

    // ---- B3 (MED-2): AddGrant must validate SelectedFunctionToAdd against the current catalog ----

    [Fact]
    public void AddGrant_SelectedFunctionNotInCurrentCatalog_DoesNotAddDraftRow()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        var bogus = new RoleFunctionPickerItem(9999, "Ghost.Function", "Ma", "Khác");
        h.Vm.SelectedFunctionToAdd = bogus;

        h.Vm.AddGrantCommand.Execute();

        h.Vm.DraftGrants.Should().BeEmpty(
            "SelectedFunctionToAdd must be validated against FunctionPickerItems before becoming a draft grant");
    }

    [Fact]
    public async Task CodeTypedAutoLoad_LoadsEffectiveMatch_IgnoresCancelled()
    {
        var h = Build();
        h.Roles.InScopeResult =
        [
            Role(9, "clerk_role", "Đã hủy vai trò", active: false, status: VersionLifecycleStatus.Cancelled),
        ];
        h.Vm.BeginAddCommand.Execute();
        h.Vm.RoleCode = "clerk_role";
        // Cancelled match must not auto-load.
        h.Vm.Status.Should().Be(VersionStatus.None);
        h.Vm.Mode.Should().Be(RoleCardMode.Adding);

        h.Roles.InScopeResult = [Role(8, "clerk_role", "Vai trò thư ký")];
        h.Roles.ByIdentityResult = Role(8, "clerk_role", "Vai trò thư ký");
        // Re-trigger auto-load by rewriting code after seeding the Effective match. No fallback
        // LoadAsync here (A2) — every fake resolves synchronously (Task.FromResult), so if the real
        // TryAutoLoadByCodeAsync feature is disabled/broken, this genuinely stays Adding and fails below.
        h.Vm.RoleCode = "clerk_x";
        h.Vm.RoleCode = "clerk_role";

        h.Vm.Status.Should().Be(VersionStatus.Effective);
        h.Vm.RoleName.Should().Be("Vai trò thư ký");
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
    }

    [Fact]
    public async Task History_LoadAll_MapsRows()
    {
        var h = Build();
        h.Roles.HistoryResult = [Role(5, "admin_role", "Quản trị viên")];
        await h.Vm.LoadAllHistoryAsync();
        h.Vm.HistoryRows.Should().HaveCount(1);
        h.Vm.HistoryRows[0].RoleCode.Should().Be("admin_role");
        h.Vm.HistoryFilterText = "admin";
        h.Vm.HistoryFilterText.Should().Be("admin");
    }

    [Fact]
    public async Task History_LoadAll_OnException_ShowsErrorBanner()
    {
        var h = Build();
        h.Roles.ThrowOnGetHistory = new InvalidOperationException("db down");

        await h.Vm.LoadAllHistoryAsync();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Ứng dụng không tải được dữ liệu lịch sử.");
    }

    [Fact]
    public async Task Clear_DoesNotResetHistoryRows()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Roles.HistoryResult = [Role(1, "x_role", "Tên đủ dài")];
        await h.Vm.LoadAllHistoryAsync();
        h.Vm.HistoryRows.Should().NotBeEmpty();
        h.Vm.Clear();
        h.Vm.HistoryRows.Should().NotBeEmpty();
        h.Vm.RoleCode.Should().BeEmpty();
    }

    // ---- MatchesHistoryFilter (F7) ----

    [Fact]
    public void MatchesHistoryFilter_NullOrEmptyFilter_MatchesEverything()
    {
        var row = HistoryRow();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, null).Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "   ").Should().BeTrue();
    }

    [Fact]
    public void MatchesHistoryFilter_NullRow_NeverMatches()
    {
        RoleDeclarationViewModel.MatchesHistoryFilter(null, "anything").Should().BeFalse();
        RoleDeclarationViewModel.MatchesHistoryFilter(null, null).Should().BeFalse();
    }

    [Fact]
    public void MatchesHistoryFilter_MatchesByCodeNameOperationRecordedByOrNote_CaseInsensitive()
    {
        var row = HistoryRow(code: "clerk_role", name: "Vai trò thư ký", operation: "Sửa",
            recordedBy: "tester", note: "ghi chú abc");

        RoleDeclarationViewModel.MatchesHistoryFilter(row, "CLERK").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "thư ký").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "sửa").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "TESTER").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "ghi chú").Should().BeTrue();
        RoleDeclarationViewModel.MatchesHistoryFilter(row, "no-match-xyz").Should().BeFalse();
    }

    // ---- Close / Cancel wiring (Fix Round 1 §1) ----

    [Fact]
    public async Task IsCloseCancelPlanBranch_FalseOnPastFrom_TrueOnTodayAndFutureFrom()
    {
        var h = Build();

        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeFalse("a version that started before today is Retire");

        h.Roles.ByIdentityResult = Role(6, "future_role", "Vai trò tương lai", from: Today.AddDays(3));
        await h.Vm.LoadAsync(6, Today.AddDays(3));
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeTrue("From after today is CancelPlan");

        // D1 boundary (VersionCloseRules.BranchFor uses `>=`) — a version whose EffectiveFrom is TODAY
        // is still the CancelPlan branch, not Retire.
        h.Roles.ByIdentityResult = Role(7, "today_role", "Vai trò hôm nay", from: Today);
        await h.Vm.LoadAsync(7, Today);
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeTrue(
            "D1: a version whose EffectiveFrom == today has not completed a single effective day and is still the cancel-plan branch");
    }

    [Fact]
    public async Task Close_Retire_Effective_SendsRequest_ReportsNeutralSuccess()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();

        await h.Vm.SaveCommand.Execute();

        h.Declaration.CloseCallCount.Should().Be(1);
        h.Declaration.LastCloseRequest!.RoleId.Should().Be(5);
        h.Declaration.LastCloseRequest.VersionId.Should().Be(10);
        h.Vm.StatusMessage.Should().Be("Đã cập nhật hiệu lực vai trò.");
        h.Vm.Severity.Should().Be(StatusSeverity.Success);
        h.Confirmation.ConfirmCallCount.Should().Be(0, "retire never confirms — only cancel-plan does");
    }

    [Fact]
    public async Task Close_CancelPlan_Pending_ConfirmsAndReportsNeutralSuccess()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(6, "future_role", "Vai trò tương lai", from: Today.AddDays(3));
        await h.Vm.LoadAsync(6, Today.AddDays(3));
        h.Vm.Status.Should().Be(VersionStatus.Pending);
        h.Vm.BeginCloseCommand.Execute();

        await h.Vm.SaveCommand.Execute();

        h.Confirmation.ConfirmCallCount.Should().Be(1);
        h.Confirmation.LastMessage.Should().Be("Đóng vai trò sẽ hủy kỳ hiệu lực của vai trò");
        h.Declaration.CloseCallCount.Should().Be(1);
        h.Declaration.LastCloseRequest!.RoleId.Should().Be(6);
        h.Vm.StatusMessage.Should().Be("Đã cập nhật hiệu lực vai trò.");
        h.Vm.Severity.Should().Be(StatusSeverity.Success);
    }

    [Fact]
    public async Task Close_CancelPlan_ConfirmDeclined_AbortsWithoutCallingService()
    {
        var h = Build();
        h.Confirmation.ConfirmResult = false;
        h.Roles.ByIdentityResult = Role(6, "future_role", "Vai trò tương lai", from: Today.AddDays(3));
        await h.Vm.LoadAsync(6, Today.AddDays(3));
        h.Vm.BeginCloseCommand.Execute();

        await h.Vm.SaveCommand.Execute();

        h.Declaration.CloseCallCount.Should().Be(0);
        h.Vm.Mode.Should().Be(RoleCardMode.Closing, "declining the confirmation must leave the form exactly as it was");
    }

    [Fact]
    public async Task Close_Retire_ServiceRejectsDependentsUncovered_ShowsMappedMessage()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        h.Declaration.CloseResult = Error.Failure("TemporalFk.DependentsUncovered", "raw engine text");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Vai trò không được đóng do còn người dùng phụ thuộc.");
    }

    [Fact]
    public async Task Close_ServiceRejectsAdminFlagChangeNotAuthorized_ShowsSameMessageAsSavePath()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        h.Declaration.CloseResult = Error.Forbidden("Role.AdminFlagChangeNotAuthorized", "raw engine text");

        await h.Vm.SaveCommand.Execute();

        h.Vm.StatusMessage.Should().Be(RoleDeclarationViewModel.AdminFlagChangeNotAuthorizedMessage);
    }

    [Fact]
    public void FormatCloseErrorPublic_DateCodesFallThrough_OnlyVersionAlreadyEndedIsMapped()
    {
        var h = Build();
        const string rSys = "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.";
        // Five date-rule codes are unreachable: CloseRoleDeclarationRequest carries no date;
        // service derives null (CancelPlan) or today-1 (Retire). VersionAlreadyEnded is the only
        // VersionCloseRules code that can surface on this screen.
        var expected = new Dictionary<string, string>
        {
            [VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan] = rSys, // unreachable: CancelPlan always passes null date
            [VersionCloseRules.Codes.VersionAlreadyEnded] = "Vai trò đã hết hiệu lực.",
            [VersionCloseRules.Codes.CloseDateRequired] = rSys, // unreachable: Retire always passes today-1
            [VersionCloseRules.Codes.CloseDateInPast] = rSys, // unreachable: derived date is exactly the floor
            [VersionCloseRules.Codes.CloseDateEqualsVersionEnd] = rSys, // unreachable: VersionAlreadyEnded reports first when To < today
            [VersionCloseRules.Codes.CloseDateOutsideVersionPeriod] = rSys, // unreachable: derived today-1 + branch cutover
        };

        foreach (var code in VersionCloseRules.Codes.All)
        {
            const string seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
            var error = Error.Validation(code, seed);
            var message = h.Vm.FormatCloseErrorPublic(error);
            message.Should().Be(expected[code], $"code '{code}' must map to the exact Immediate-screen string");
            message.Should().NotBe(seed);
        }
    }

    [Fact]
    public void FormatCloseErrorPublic_MapsRoleOnlyCodes()
    {
        var h = Build();
        h.Vm.FormatCloseErrorPublic(Error.NotFound("Role.VersionNotFound", "raw"))
            .Should().Be("Không tìm thấy phiên bản vai trò để đóng/hủy.");
        h.Vm.FormatCloseErrorPublic(Error.Forbidden("Role.AdminFlagChangeNotAuthorized", "raw"))
            .Should().Be(RoleDeclarationViewModel.AdminFlagChangeNotAuthorizedMessage);
        h.Vm.FormatCloseErrorPublic(Error.Forbidden("Authz.ScopeInsufficient", "raw"))
            .Should().Be("Người dùng không được cấp quyền.");
        h.Vm.FormatCloseErrorPublic(Error.Failure("VersionedRepository.DependentSetChanged", "raw"))
            .Should().Be("Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.");
        h.Vm.FormatCloseErrorPublic(Error.Failure("VersionedRepository.DependentNotEnlisted", "raw"))
            .Should().Be("Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.");
        h.Vm.FormatCloseErrorPublic(Error.Validation("VersionedRepository.BaseVersionRequired", "raw"))
            .Should().Be("Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.");
    }


    // Brief 160 → 161: BaseVersionRequired on close is reachable via AutoCutExclusivelyOwnedAsync;
    // the map must show the settled grant/role period sentence, not a stale-reload hint.
    [Fact]
    public async Task Close_BaseVersionRequired_StatusMessageIsSettledGrantPeriodSentence()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();

        const string engineDescription =
            "Không thể auto-cut 'role_permission_version' identity=9: " +
            "ngày cắt 23/07/2026 không nằm trong phiên bản active [20/07/2026, 31/12/9999).";
        h.Declaration.CloseResult = Error.Validation(
            "VersionedRepository.BaseVersionRequired", engineDescription);

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be(
            "Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.");
        h.Vm.StatusMessage.Should().NotContain("role_permission_version");
        h.Vm.StatusMessage.Should().NotBe(engineDescription);
    }

    // ---- Permission journal cancelled-row blank cell (§5) ----

    [Fact]
    public async Task LoadAsync_PermissionJournal_CancelledRow_HasBlankWhenText_DiscriminatingAgainstNonCancelled()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        var cancelledTime = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var revokeTime = new DateTime(2026, 3, 15, 14, 45, 0, DateTimeKind.Utc);
        var activeTime = new DateTime(2026, 6, 1, 9, 30, 0, DateTimeKind.Utc);
        h.Permissions.History =
        [
            // Cancelled Add — the original grant's recorded_at is never restamped by CancelVersionAsync's
            // no-predecessor path, so labelling it "the moment this was carried out" would misattribute
            // the cancel to the creator. Must render BLANK.
            new RolePermissionVersionDto(1, 11, Today, Period.OpenEnd, false, 5, 1, ScopeLevel.Global,
                cancelledTime, "creator", null, VersionLifecycleStatus.Cancelled, VersionOperationKind.Add),
            // B5/MED-5: a NON-cancelled Close remnant — CloseVersionAsync inserts a fresh remnant row with
            // the real actor/moment, so this one must NOT be blanked (the old test never covered this row
            // kind, so it would stay green even if a future change blanked revoke timestamps too).
            new RolePermissionVersionDto(3, 13, Today, Today.AddDays(30), false, 5, 3, ScopeLevel.OwnOrgUnit,
                revokeTime, "closer", null, VersionLifecycleStatus.Normal, VersionOperationKind.Close),
            new RolePermissionVersionDto(2, 12, Today, Period.OpenEnd, true, 5, 2, ScopeLevel.Self,
                activeTime, "tester", null, VersionLifecycleStatus.Normal, VersionOperationKind.Add),
        ];

        await h.Vm.LoadAsync(5, Today);

        var cancelledRow = h.Vm.PermissionJournal.Single(r => r.RolePermissionId == 11);
        var revokeRow = h.Vm.PermissionJournal.Single(r => r.RolePermissionId == 13);
        var activeRow = h.Vm.PermissionJournal.Single(r => r.RolePermissionId == 12);
        cancelledRow.WhenText.Should().BeEmpty();
        revokeRow.WhenText.Should().Be(revokeTime.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            "only a CANCELLED row is blanked — a non-cancelled Close/revoke remnant keeps its real timestamp");
        activeRow.WhenText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EffectivePeriodText_LoadedOpenEndedVsClosed_BlankInAdding()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.EffectivePeriodText.Should().BeEmpty();

        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today, to: Period.OpenEnd);
        await h.Vm.LoadAsync(5, Today);
        h.Vm.EffectivePeriodText.Should().Be("Hiệu lực từ 09/08/2026 đến Không xác định");

        var closedFrom = new DateOnly(2026, 7, 1);
        var closedTo = new DateOnly(2026, 8, 1);
        h.Roles.ByIdentityResult = Role(6, "closed_role", "Vai trò đã đóng", from: closedFrom, to: closedTo);
        await h.Vm.LoadAsync(6, closedFrom);
        h.Vm.EffectivePeriodText.Should().Be("Hiệu lực từ 01/07/2026 đến 01/08/2026");
    }

    [Fact]
    public async Task Cancel_AfterAdd_RestoresLoadedPeriodAndDisplayTextTogether()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        var loadedText = h.Vm.EffectivePeriodText;
        loadedText.Should().NotBeNullOrEmpty();

        h.Vm.BeginAddCommand.Execute();
        h.Vm.EffectivePeriodText.Should().BeEmpty();
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeFalse("Adding has no loaded period");

        await h.Vm.CancelCommand.Execute();
        h.Vm.EffectivePeriodText.Should().Be(loadedText);
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeFalse("restored past-From version is still Retire");
    }

    [Fact]
    public async Task GrantsReadinessMatrix_FullCrossProduct_AndCanExecuteChangedBothWays()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.Status.Should().Be(VersionStatus.Effective);
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);

        AssertReadinessCell(h, RoleCardMode.ReadOnly, GrantsReadiness.Unresolved, canEdit: false, canSave: false, canClose: true, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.ReadOnly, GrantsReadiness.Loading, canEdit: false, canSave: false, canClose: true, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.ReadOnly, GrantsReadiness.Resolved, canEdit: true, canSave: false, canClose: true, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.ReadOnly, GrantsReadiness.Failed, canEdit: false, canSave: false, canClose: true, canMutate: false);

        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Resolved);
        h.Vm.BeginEditCommand.Execute();
        AssertReadinessCell(h, RoleCardMode.Editing, GrantsReadiness.Unresolved, canEdit: false, canSave: false, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Editing, GrantsReadiness.Loading, canEdit: false, canSave: false, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Editing, GrantsReadiness.Resolved, canEdit: false, canSave: true, canClose: false, canMutate: true);
        AssertReadinessCell(h, RoleCardMode.Editing, GrantsReadiness.Failed, canEdit: false, canSave: false, canClose: false, canMutate: false);

        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Resolved);
        await h.Vm.CancelCommand.Execute();
        h.Vm.BeginCloseCommand.Execute();
        AssertReadinessCell(h, RoleCardMode.Closing, GrantsReadiness.Unresolved, canEdit: false, canSave: true, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Closing, GrantsReadiness.Loading, canEdit: false, canSave: true, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Closing, GrantsReadiness.Resolved, canEdit: false, canSave: true, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Closing, GrantsReadiness.Failed, canEdit: false, canSave: true, canClose: false, canMutate: false);

        await h.Vm.CancelCommand.Execute();
        h.Vm.BeginAddCommand.Execute();
        AssertReadinessCell(h, RoleCardMode.Adding, GrantsReadiness.Unresolved, canEdit: false, canSave: false, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Adding, GrantsReadiness.Loading, canEdit: false, canSave: false, canClose: false, canMutate: false);
        AssertReadinessCell(h, RoleCardMode.Adding, GrantsReadiness.Resolved, canEdit: false, canSave: true, canClose: false, canMutate: true);
        AssertReadinessCell(h, RoleCardMode.Adding, GrantsReadiness.Failed, canEdit: false, canSave: false, canClose: false, canMutate: false);

        await h.Vm.CancelCommand.Execute();
        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Resolved);
        h.Vm.BeginEditCommand.Execute();
        var saveFired = 0;
        var mutateFired = 0;
        var editFired = 0;
        h.Vm.SaveCommand.CanExecuteChanged += (_, _) => saveFired++;
        h.Vm.AddGrantCommand.CanExecuteChanged += (_, _) => mutateFired++;
        h.Vm.BeginEditCommand.CanExecuteChanged += (_, _) => editFired++;

        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Failed);
        saveFired.Should().BeGreaterThan(0, "Resolved→Failed must requery SaveCommand");
        mutateFired.Should().BeGreaterThan(0, "Resolved→Failed must requery AddGrantCommand");
        var saveAfterFail = saveFired;
        var mutateAfterFail = mutateFired;

        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Resolved);
        saveFired.Should().BeGreaterThan(saveAfterFail, "Failed→Resolved must requery SaveCommand");
        mutateFired.Should().BeGreaterThan(mutateAfterFail, "Failed→Resolved must requery AddGrantCommand");
        editFired.Should().BeGreaterThan(0);

        var addGrantFired = 0;
        var removeEffectiveFired = 0;
        var removeDraftFired = 0;
        h.Vm.AddGrantCommand.CanExecuteChanged += (_, _) => addGrantFired++;
        h.Vm.RemoveEffectiveGrantCommand.CanExecuteChanged += (_, _) => removeEffectiveFired++;
        h.Vm.RemoveDraftGrantCommand.CanExecuteChanged += (_, _) => removeDraftFired++;

        await h.Vm.CancelCommand.Execute();
        addGrantFired.Should().BeGreaterThan(0, "Editing→ReadOnly must requery AddGrantCommand");
        removeEffectiveFired.Should().BeGreaterThan(0, "Editing→ReadOnly must requery RemoveEffectiveGrantCommand");
        removeDraftFired.Should().BeGreaterThan(0, "Editing→ReadOnly must requery RemoveDraftGrantCommand");
        var addAfterCancel = addGrantFired;
        var removeEffectiveAfterCancel = removeEffectiveFired;
        var removeDraftAfterCancel = removeDraftFired;

        h.Vm.BeginEditCommand.Execute();
        addGrantFired.Should().BeGreaterThan(addAfterCancel, "ReadOnly→Editing must requery AddGrantCommand");
        removeEffectiveFired.Should().BeGreaterThan(removeEffectiveAfterCancel, "ReadOnly→Editing must requery RemoveEffectiveGrantCommand");
        removeDraftFired.Should().BeGreaterThan(removeDraftAfterCancel, "ReadOnly→Editing must requery RemoveDraftGrantCommand");
    }

    private static void AssertReadinessCell(
        Harness h, RoleCardMode mode, GrantsReadiness readiness,
        bool canEdit, bool canSave, bool canClose, bool canMutate)
    {
        h.Vm.Mode.Should().Be(mode);
        h.Vm.OverrideGrantsReadinessForTest(readiness);
        var cell = $"{mode}×{readiness}";
        h.Vm.CanEdit.Should().Be(canEdit, $"{cell} CanEdit");
        h.Vm.CanSave.Should().Be(canSave, $"{cell} CanSave");
        h.Vm.CanClose.Should().Be(canClose, $"{cell} CanClose");
        h.Vm.CanMutateGrants.Should().Be(canMutate, $"{cell} CanMutateGrants");
        h.Vm.SaveCommand.CanExecute().Should().Be(canSave, $"{cell} SaveCommand");
        h.Vm.AddGrantCommand.CanExecute().Should().Be(canMutate, $"{cell} AddGrantCommand");
    }

    [Fact]
    public async Task GrantsReadiness_LoadPath_OverlayAndCanCloseWhileLoading()
    {
        var h = Build();
        h.Vm.GrantsGridOverlayText.Should().BeNull("FR-7b: empty card, _roleId is null");

        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        var gate = new TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>();
        h.Permissions.GateActiveGrants = gate;
        var loading = h.Vm.LoadAsync(5, Today);
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Loading);
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanMutateGrants.Should().BeFalse();
        h.Vm.CanClose.Should().BeTrue("CanClose must not wait on grants");
        h.Vm.GrantsGridOverlayText.Should().Contain("Đang tải");
        gate.SetResult([]);
        await loading;
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Resolved);
        h.Vm.CanEdit.Should().BeTrue();
        h.Vm.GrantsGridOverlayText.Should().BeNull();

        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Failed);
        h.Vm.BeginEditCommand.CanExecute().Should().BeFalse();
        h.Vm.CanClose.Should().BeTrue();
        h.Vm.GrantsGridOverlayText.Should().Be("Ứng dụng không tải được danh sách quyền.");
    }

    [Fact]
    public async Task LoadAsync_JournalOnlyFailure_LeavesMutationEnabled()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        h.Permissions.ThrowOnGetGrantHistory = new InvalidOperationException("journal down");

        await h.Vm.LoadAsync(5, Today);

        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Resolved);
        h.Vm.BeginEditCommand.CanExecute().Should().BeTrue();
        h.Vm.CanMutateGrants.Should().BeFalse();
        h.Vm.Severity.Should().Be(StatusSeverity.Warning);
        h.Vm.StatusMessage.Should().Be("Ứng dụng không tải được nhật ký quyền.");
    }

    [Fact]
    public async Task IsAdminFlagEditable_FalseInReadOnlyAndClosing_EvenForBreakGlass()
    {
        var h = Build(breakGlass: true);
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.IsAdminFlagEditable.Should().BeFalse();
        h.Vm.IsAdminRole = true;
        h.Vm.IsAdminRole.Should().BeFalse();

        h.Vm.BeginCloseCommand.Execute();
        h.Vm.IsAdminFlagEditable.Should().BeFalse();
        h.Vm.IsAdminRole = true;
        h.Vm.IsAdminRole.Should().BeFalse();
    }

    [Fact]
    public async Task Close_SuccessMessage_IsNeutral_WhenBusinessDateAdvancesBetweenBranchAndService()
    {
        var dates = new AdvancingDates(Today);
        var h = Build(dates: dates);
        h.Roles.ByIdentityResult = Role(7, "today_role", "Vai trò hôm nay", from: Today);
        await h.Vm.LoadAsync(7, Today);
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeTrue();
        h.Vm.BeginCloseCommand.Execute();
        h.Declaration.OnClose = () => dates.Today = dates.Today.AddDays(1);

        await h.Vm.SaveCommand.Execute();

        h.Confirmation.ConfirmCallCount.Should().Be(1);
        h.Vm.StatusMessage.Should().Be("Đã cập nhật hiệu lực vai trò.");
    }

    [Fact]
    public async Task Save_StaleAuthorizationSuccess_DoesNotCallServiceAfterClear()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        var gate = new TaskCompletionSource<ErrorOr<DataScope>>();
        h.Authorization.GateAuthorize = gate;

        var save = h.Vm.SaveCommand.Execute();
        h.Vm.Clear();
        gate.SetResult(new DataScope(ScopeLevel.Global, null, "tester"));
        await save;

        h.Declaration.SaveCallCount.Should().Be(0);
        h.Vm.Severity.Should().Be(StatusSeverity.None,
            "a superseded in-flight AuthorizeAsync(true) must not resume and paint a validation error on the cleared card");
        h.Vm.RoleCode.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_Add_CancelDuringAuthorization_WritesNothing()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        FillValidAddForm(h.Vm);
        var gate = new TaskCompletionSource<ErrorOr<DataScope>>();
        h.Authorization.GateAuthorize = gate;

        var save = h.Vm.SaveCommand.Execute();
        await h.Vm.CancelCommand.Execute();
        gate.SetResult(new DataScope(ScopeLevel.Global, null, "tester"));
        await save;

        h.Declaration.SaveCallCount.Should().Be(0);
        h.Vm.RoleCode.Should().Be("admin_role");
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
    }

    [Fact]
    public async Task Save_Edit_CancelDuringAuthorization_WritesNothing()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleName = "Tên vai trò đã sửa đủ dài";
        var gate = new TaskCompletionSource<ErrorOr<DataScope>>();
        h.Authorization.GateAuthorize = gate;

        var save = h.Vm.SaveCommand.Execute();
        await h.Vm.CancelCommand.Execute();
        gate.SetResult(new DataScope(ScopeLevel.Global, null, "tester"));
        await save;

        h.Declaration.SaveCallCount.Should().Be(0);
        h.Vm.RoleName.Should().Be("Quản trị viên");
    }

    [Fact]
    public async Task Save_Closing_CancelDuringAuthorization_DoesNotWriteEdit()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        var gate = new TaskCompletionSource<ErrorOr<DataScope>>();
        h.Authorization.GateAuthorize = gate;

        var save = h.Vm.SaveCommand.Execute();
        await h.Vm.CancelCommand.Execute();
        gate.SetResult(new DataScope(ScopeLevel.Global, null, "tester"));
        await save;

        h.Declaration.SaveCallCount.Should().Be(0);
        h.Declaration.CloseCallCount.Should().Be(0);
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
    }

    [Fact]
    public async Task Save_Add_ClearDuringAuthorization_WritesNothing()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        var gate = new TaskCompletionSource<ErrorOr<DataScope>>();
        h.Authorization.GateAuthorize = gate;

        var save = h.Vm.SaveCommand.Execute();
        h.Vm.Clear();
        gate.SetResult(new DataScope(ScopeLevel.Global, null, "tester"));
        await save;

        h.Declaration.SaveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Save_Add_FormMutatedDuringSave_SubmitsTheSubmittedFields()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Roles.ByIdentityResult = Role(101, "clerk_role", "Vai trò thư ký");
        var gate = new TaskCompletionSource<ErrorOr<SaveRoleDeclarationResult>>();
        h.Declaration.GateSave = gate;

        var save = h.Vm.SaveCommand.Execute();
        h.Vm.RoleName = "Tên đã bị đổi trong lúc save";
        gate.SetResult(new SaveRoleDeclarationResult(101, 1, false, [], []));
        await save;

        h.Declaration.LastRequest.Should().NotBeNull();
        h.Declaration.LastRequest!.RoleName.Should().Be("Vai trò thư ký");
        h.Declaration.LastRequest.RoleCode.Should().Be("clerk_role");
    }

    [Fact]
    public async Task Grants_Failed_ThenAddThenCancel_StaysNonEditable()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Failed);

        h.Permissions.ThrowOnGetActiveGrants = null;
        h.Vm.BeginAddCommand.Execute();
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Resolved, "blank Adding catalog owns readiness");

        await h.Vm.CancelCommand.Execute();
        AssertFailedCardLocked(h);
    }

    [Fact]
    public async Task Grants_Failed_ThenAddThenCancel_LateCatalogLanding_StaysNonEditable()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");
        await h.Vm.LoadAsync(5, Today);
        h.Permissions.ThrowOnGetActiveGrants = null;

        var gate = new TaskCompletionSource<IReadOnlyList<FunctionVersionDto>>();
        h.Functions.GateGetInScope = gate;
        h.Vm.BeginAddCommand.Execute();
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Loading);

        await h.Vm.CancelCommand.Execute();
        gate.SetResult(h.Functions.InScope);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        AssertFailedCardLocked(h);
    }

    [Fact]
    public async Task Grants_FailedCardLoad_LateCatalogFromPreviousEdit_DoesNotClearFailure()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);

        var gate = new TaskCompletionSource<IReadOnlyList<FunctionVersionDto>>();
        h.Functions.GateGetInScope = gate;
        h.Vm.BeginEditCommand.Execute();
        await h.Vm.CancelCommand.Execute();

        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Failed);

        gate.SetResult(h.Functions.InScope);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        AssertFailedCardLocked(h);
    }

    private static void AssertFailedCardLocked(Harness h)
    {
        h.Vm.GrantsReadiness.Should().Be(GrantsReadiness.Failed);
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanSave.Should().BeFalse();
        h.Vm.CanMutateGrants.Should().BeFalse();
        h.Vm.GrantsGridOverlayText.Should().NotBeNull();
    }

    [Fact]
    public async Task ViewHistoryRow_CancelledRow_ShowsThatExactVersion_NotThePredecessor()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(
            5, "pred_role", "Phiên bản tiền nhiệm",
            from: new DateOnly(2020, 1, 1), versionId: 1);
        var rowA = HistoryRow(
            roleId: 5, versionId: 2, from: Today, to: Period.OpenEnd,
            code: "canc_role", name: "Phiên bản đã hủy",
            status: VersionStatus.Cancelled);

        var outcome = await h.Vm.LoadFromHistoryRowAsync(rowA);

        outcome.Should().Be(CardLoadOutcome.Loaded);
        h.Vm.RoleCode.Should().Be("canc_role");
        h.Vm.RoleName.Should().Be("Phiên bản đã hủy");
        h.Vm.Status.Should().Be(VersionStatus.Cancelled);
        h.Vm.EffectivePeriodText.Should().Be("Hiệu lực từ 09/08/2026 đến Không xác định");
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanClose.Should().BeFalse();
    }

    [Fact]
    public async Task ViewHistoryRow_SupersededRow_ShowsThatVersionReadOnly()
    {
        var h = Build();
        var from = new DateOnly(2020, 1, 1);
        var to = new DateOnly(2025, 12, 31);
        var row = HistoryRow(
            roleId: 5, versionId: 7, from: from, to: to,
            code: "old_role", name: "Phiên bản đã thay",
            status: VersionStatus.Expired);

        var outcome = await h.Vm.LoadFromHistoryRowAsync(row);

        outcome.Should().Be(CardLoadOutcome.Loaded);
        h.Vm.RoleName.Should().Be("Phiên bản đã thay");
        h.Vm.Status.Should().Be(VersionStatus.Expired);
        h.Vm.EffectivePeriodText.Should().Be("Hiệu lực từ 01/01/2020 đến 31/12/2025");
        h.Vm.CanEdit.Should().BeFalse();
        h.Vm.CanClose.Should().BeFalse();
    }

    [Fact]
    public async Task ViewHistoryRow_Superseded_ByANewerLoad_ReturnsSuperseded_AndWritesNoBanner()
    {
        var h = Build();
        var row = HistoryRow(roleId: 5, versionId: 2, code: "hist_role", name: "Từ lịch sử");
        var gate = new TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>();
        h.Permissions.GateActiveGrants = gate;

        var history = h.Vm.LoadFromHistoryRowAsync(row);
        h.Roles.ByIdentityResult = Role(9, "newer_role", "Tải mới hơn");
        var newer = h.Vm.LoadAsync(9, Today);
        gate.SetResult([]);

        (await history).Should().Be(CardLoadOutcome.Superseded);
        (await newer).Should().Be(CardLoadOutcome.Loaded);
        h.Vm.RoleCode.Should().Be("newer_role");
        h.Vm.StatusMessage.Should().BeNull();
        h.Vm.Severity.Should().Be(StatusSeverity.None);
    }

    [Fact]
    public async Task ViewHistoryRow_Superseded_DoesNotMarkRowConsumed()
    {
        var h = Build();
        var row = HistoryRow(roleId: 5, versionId: 2, code: "hist_role", name: "Từ lịch sử");
        var gate = new TaskCompletionSource<IReadOnlyList<RolePermissionVersionDto>>();
        h.Permissions.GateActiveGrants = gate;

        var history = h.Vm.LoadFromHistoryRowAsync(row);
        h.Roles.ByIdentityResult = Role(9, "newer_role", "Tải mới hơn");
        var newer = h.Vm.LoadAsync(9, Today);
        gate.SetResult([]);

        var outcome = await history;
        await newer;
        outcome.Should().Be(CardLoadOutcome.Superseded,
            "LocalChrome.MarkHistoryViewConsumed is view-side; VM pin is outcome != Loaded");
    }

    [Fact]
    public async Task ViewHistoryRow_LoadedWithPreexistingWarningBanner_MarksRowConsumed()
    {
        var h = Build();
        h.Permissions.ThrowOnGetGrantHistory = new InvalidOperationException("journal down");
        var row = HistoryRow(roleId: 5, versionId: 2, code: "warn_role", name: "Có cảnh báo nhật ký");

        var outcome = await h.Vm.LoadFromHistoryRowAsync(row);

        outcome.Should().Be(CardLoadOutcome.Loaded);
        h.Vm.Severity.Should().Be(StatusSeverity.Warning);
        h.Vm.RoleName.Should().Be("Có cảnh báo nhật ký");
    }

    [Fact]
    public async Task SaveClose_CancelDuringConfirmation_DoesNotWriteClose()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "today_role", "Vai trò hôm nay", from: Today);
        await h.Vm.LoadAsync(5, Today);
        h.Vm.IsCloseCancelPlanBranchPublic().Should().BeTrue();
        h.Vm.BeginCloseCommand.Execute();
        var gate = new TaskCompletionSource<bool>();
        h.Confirmation.GateConfirm = gate;

        var save = h.Vm.SaveCommand.Execute();
        await h.Vm.CancelCommand.Execute();
        gate.SetResult(true);
        await save;

        h.Declaration.CloseCallCount.Should().Be(0);
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
        h.Vm.RoleCode.Should().Be("today_role");
    }

    [Fact]
    public async Task SaveClose_CancelDuringCloseWrite_DoesNotPaintBanner()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        var gate = new TaskCompletionSource<ErrorOr<UpsertResult>>();
        h.Declaration.GateClose = gate;

        var save = h.Vm.SaveCommand.Execute();
        await h.Vm.CancelCommand.Execute();
        gate.SetResult(new UpsertResult(1, [], []));
        await save;

        h.Declaration.CloseCallCount.Should().Be(1);
        h.Vm.Severity.Should().NotBe(StatusSeverity.Success);
        h.Vm.StatusMessage.Should().NotBe("Đã cập nhật hiệu lực vai trò.");
        h.Vm.RoleName.Should().Be("Quản trị viên");
        h.Vm.Mode.Should().Be(RoleCardMode.ReadOnly);
    }

    // Brief 161 — FormatCloseError / FormatSaveError completeness. Deliberately manual lists: a new
    // raise-site code is NOT caught until someone adds it HERE; the list therefore cannot catch an
    // unmapped code that still lands on the catch-all.
    private const string OperatorFallThroughMessage =
        "Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.";

    private const string DependentSetChangedMessage =
        "Dữ liệu đã được thay đổi, người dùng tải lại chức năng để cập nhật.";

    [Fact]
    public void FormatCloseErrorPublic_CoversEverySettledCode_ReturnsExactSentence()
    {
        var h = Build();
        var seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        var expected = new Dictionary<string, string>
        {
            ["VersionedRepository.BaseVersionRequired"] =
                "Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.",
            ["VersionedRepository.DependentSetChanged"] = DependentSetChangedMessage,
            ["VersionedRepository.DependentNotEnlisted"] = OperatorFallThroughMessage,
            ["VersionedRepository.NotAFuturePlan"] = DependentSetChangedMessage,
            ["VersionedRepository.LockTimeout"] = "Dữ liệu đang được người dùng khác khai báo.",
            ["VersionedRepository.InvalidShrink"] =
                "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
            ["Authz.NotGranted"] = "Người dùng không được cấp quyền.",
            ["Authz.ScopeInsufficient"] =
                "Người dùng không được cấp quyền.",
            ["Role.AdminFlagChangeNotAuthorized"] =
                "Người dùng không được cấp quyền.", // literal — not AdminFlagChangeNotAuthorizedMessage (FR3-2)
            [VersionCloseRules.Codes.VersionAlreadyEnded] = "Vai trò đã hết hiệu lực.",
            ["TemporalFk.DependentsUncovered"] =
                "Vai trò không được đóng do còn người dùng phụ thuộc.",
            ["Function.DuplicateKey"] = OperatorFallThroughMessage,
            ["User.DuplicateUsername"] = OperatorFallThroughMessage,
            ["RolePermission.DuplicateGrant"] = OperatorFallThroughMessage,
            ["Role.CascadeGrantNotProbed"] = OperatorFallThroughMessage,
            ["CompositeWrite.NotEnlisted"] = OperatorFallThroughMessage,
            ["AuditLogWriter.NoAmbientConnection"] = OperatorFallThroughMessage,
        };

        foreach (var (code, sentence) in expected)
        {
            var message = h.Vm.FormatCloseErrorPublic(Error.Validation(code, seed));
            message.Should().Be(sentence, $"code '{code}' must map to its settled operator sentence");
        }
    }

    [Fact]
    public void FormatCloseErrorPublic_UnmappedCode_ReturnsFallThrough_DoesNotThrow()
    {
        var h = Build();
        var act = () => h.Vm.FormatCloseErrorPublic(
            Error.Failure("Totally.UnknownCode", "English developer sentence"));
        act.Should().NotThrow();
        act().Should().Be(OperatorFallThroughMessage);
    }

    [Fact]
    public void FormatCloseErrorPublic_PermissionFamily_DedicatedArmsDistinctFromPrefix_Control()
    {
        const string permission = "Người dùng không được cấp quyền.";
        const string seedDedicated = "SEED-DEDICATED-ARM-MUST-NOT-LEAK";
        const string seedPrefix = "SEED-PREFIX-ARM-MUST-NOT-LEAK";
        var h = Build();

        h.Vm.FormatCloseErrorPublic(Error.Forbidden("Authz.NotGranted", seedDedicated))
            .Should().Be(permission).And.NotContain(seedDedicated);
        h.Vm.FormatCloseErrorPublic(Error.Forbidden("Authz.OnlyViaPrefixControl", seedPrefix))
            .Should().Be(permission).And.NotContain(seedPrefix);

        // Scope to FormatCloseError only — whole-file scan would match FormatSaveError's twin arms.
        var closeRegion = SliceRoleMapSource(
            "private string FormatCloseError(Error error)",
            "public string FormatCloseErrorPublic");
        AssertAuthzDedicatedArmsInRegion(closeRegion, nameof(FormatCloseErrorPublic_PermissionFamily_DedicatedArmsDistinctFromPrefix_Control));
    }

    [Fact]
    public void FormatSaveErrorPublic_PermissionFamily_DedicatedArmsDistinctFromPrefix_Control()
    {
        const string permission = "Người dùng không được cấp quyền.";
        const string seedDedicated = "SEED-DEDICATED-ARM-MUST-NOT-LEAK";
        const string seedPrefix = "SEED-PREFIX-ARM-MUST-NOT-LEAK";
        var h = Build();

        h.Vm.FormatSaveErrorPublic(Error.Forbidden("Authz.NotGranted", seedDedicated))
            .Should().Be(permission).And.NotContain(seedDedicated);
        h.Vm.FormatSaveErrorPublic(Error.Forbidden("Authz.OnlyViaPrefixControl", seedPrefix))
            .Should().Be(permission).And.NotContain(seedPrefix);

        var saveRegion = SliceRoleMapSource(
            "private static string FormatSaveError(Error error)",
            "public string FormatSaveErrorPublic");
        AssertAuthzDedicatedArmsInRegion(saveRegion, nameof(FormatSaveErrorPublic_PermissionFamily_DedicatedArmsDistinctFromPrefix_Control));
    }

    private static string SliceRoleMapSource(string startMarker, string endMarker)
    {
        var src = System.IO.File.ReadAllText(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "AST.Shell", "ViewModels", "Iam", "Role", "RoleDeclarationViewModel.cs")));
        var start = src.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"missing start marker '{startMarker}'");
        var end = src.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"missing end marker '{endMarker}'");
        return src[start..end];
    }

    private static void AssertAuthzDedicatedArmsInRegion(string region, string because)
    {
        System.Text.RegularExpressions.Regex.Matches(region, "(?m)^\\s*\"Authz\\.NotGranted\"\\s*=>")
            .Count.Should().Be(1, $"{because}: Format region must keep exactly one NotGranted arm");
        System.Text.RegularExpressions.Regex.Matches(region, "(?m)^\\s*\"Authz\\.ScopeInsufficient\"\\s*=>")
            .Count.Should().Be(1, $"{because}: Format region must keep exactly one ScopeInsufficient arm");
        region.Should().Contain("StartsWith(\"Authz.\"", because);
        // Catch-all-identical sentence — output asserts cannot discriminate deletion (FR4-1).
        System.Text.RegularExpressions.Regex.Matches(
                region, "(?m)^\\s*\"VersionedRepository\\.DependentNotEnlisted\"\\s*=>")
            .Count.Should().Be(1, $"{because}: Format region must keep exactly one DependentNotEnlisted arm");
    }

    [Fact]
    public void FormatCloseErrorPublic_NeverSurfacesDescription()
    {
        var h = Build();
        const string seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        foreach (var code in new[]
                 {
                     VersionCloseRules.Codes.VersionAlreadyEnded,
                     "Authz.ScopeInsufficient",
                     "Function.DuplicateKey",
                     "Totally.UnknownCode",
                 })
        {
            h.Vm.FormatCloseErrorPublic(Error.Validation(code, seed))
                .Should().NotContain(seed, $"code '{code}'");
        }
    }

    [Fact]
    public void FormatSaveErrorPublic_CoversEverySettledCode_ReturnsExactSentence()
    {
        var h = Build();
        var seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        var expected = new Dictionary<string, string>
        {
            ["Role.AdminFlagChangeNotAuthorized"] =
                "Người dùng không được cấp quyền.", // literal — not AdminFlagChangeNotAuthorizedMessage (FR3-2)
            ["Role.CodeInUse"] = "Mã vai trò đã được sử dụng.",
            ["Role.CodeOwnedByAnotherIdentity"] = "Mã vai trò đã được sử dụng.",
            ["Role.CodeIdentityAmbiguous"] =
                "Mã vai trò bị trùng lặp với mã vai trò trong lịch sử do lỗi dữ liệu.",
            ["Role.CodeOwnerNotDormant"] =
                "Mã vai trò bị trùng lặp với mã vai trò sẽ được sử dụng trong tương lai.",
            ["RolePermission.OverlappingGrant"] = "Chức năng bị trùng lặp.",
            ["TemporalFk.ParentGap"] =
                "Kỳ hiệu lực của quyền vượt ngoài kỳ hiệu lực của vai trò hoặc chức năng.",
            ["EffectivePeriod.NoCoverage"] = "Quyền cần thu hồi đã hết hiệu lực.",
            ["EffectivePeriod.InvalidRange"] =
                "Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.",
            ["EffectivePeriod.OverlappingVersions"] =
                "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
            ["Authz.ScopeInsufficient"] =
                "Người dùng không được cấp quyền.",
            ["Authz.NotGranted"] = "Người dùng không được cấp quyền.",
            ["VersionedRepository.LockTimeout"] = "Dữ liệu đang được người dùng khác khai báo.",
            ["VersionedRepository.InvalidShrink"] =
                "Ngày kết thúc hiệu lực không nằm trong kỳ hiệu lực đã khai báo.",
            ["Role.CodeOwnershipChanged"] = DependentSetChangedMessage,
            ["Role.VersionOutOfDate"] = DependentSetChangedMessage,
            ["Role.ExpectedCodeMismatch"] = DependentSetChangedMessage,
            ["VersionedRepository.NotAFuturePlan"] = DependentSetChangedMessage,
            ["VersionedRepository.VersionNotFound"] = DependentSetChangedMessage,
            ["VersionedRepository.DependentSetChanged"] = DependentSetChangedMessage,
            ["VersionedRepository.DependentNotEnlisted"] = OperatorFallThroughMessage,
            ["VersionedRepository.BaseVersionRequired"] =
                "Kỳ hiệu lực của quyền không phù hợp với kỳ hiệu lực của vai trò.",
            ["RolePermission.NotOwnedByRole"] = OperatorFallThroughMessage,
            ["RolePermission.IdentityAlreadyVersioned"] = OperatorFallThroughMessage,
            ["Function.DuplicateKey"] = OperatorFallThroughMessage,
            ["User.DuplicateUsername"] = OperatorFallThroughMessage,
            ["RolePermission.DuplicateGrant"] = OperatorFallThroughMessage,
            ["CompositeWrite.NotEnlisted"] = OperatorFallThroughMessage,
            ["AuditLogWriter.NoAmbientConnection"] = OperatorFallThroughMessage,
        };

        foreach (var (code, sentence) in expected)
        {
            var message = h.Vm.FormatSaveErrorPublic(Error.Validation(code, seed));
            message.Should().Be(sentence, $"code '{code}' must map to its settled operator sentence");
        }
    }

    [Fact]
    public void FormatSaveErrorPublic_UnmappedCode_ReturnsFallThrough_DoesNotThrow()
    {
        var h = Build();
        var act = () => h.Vm.FormatSaveErrorPublic(
            Error.Failure("Totally.UnknownCode", "English developer sentence"));
        act.Should().NotThrow();
        act().Should().Be(OperatorFallThroughMessage);
    }

    [Fact]
    public void FormatSaveErrorPublic_NeverSurfacesDescription()
    {
        var h = Build();
        const string seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        foreach (var code in new[] { "Role.CodeInUse", "Authz.ScopeInsufficient", "Totally.UnknownCode" })
        {
            h.Vm.FormatSaveErrorPublic(Error.Validation(code, seed))
                .Should().NotContain(seed, $"code '{code}'");
        }
    }

    [Fact]
    public void FormatLoadErrorPublic_CoversEverySettledCode_ReturnsExactSentence()
    {
        var h = Build();
        const string seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        var expected = new Dictionary<string, string>
        {
            ["EffectivePeriod.NoCoverage"] = "Vai trò không hiệu lực tại ngày đã chọn.",
            ["EffectivePeriod.OverlappingVersions"] =
                "Kỳ hiệu lực bị trùng lặp một phần hoặc toàn phần.",
        };

        foreach (var (code, sentence) in expected)
        {
            h.Vm.FormatLoadErrorPublic(Error.Validation(code, seed))
                .Should().Be(sentence, $"code '{code}'");
        }
    }

    [Fact]
    public void FormatLoadErrorPublic_UnmappedCode_ReturnsFallThrough_DoesNotThrow()
    {
        var h = Build();
        var act = () => h.Vm.FormatLoadErrorPublic(
            Error.Failure("Load.Unknown", "english only"));
        act.Should().NotThrow();
        act().Should().Be(OperatorFallThroughMessage);
    }

    [Fact]
    public void FormatLoadErrorPublic_NeverSurfacesDescription()
    {
        var h = Build();
        const string seed = "SEED-DESCRIPTION-MUST-NOT-LEAK";
        h.Vm.FormatLoadErrorPublic(Error.NotFound("EffectivePeriod.NoCoverage", seed))
            .Should().NotContain(seed);
        h.Vm.FormatLoadErrorPublic(Error.Failure("Load.Unknown", seed))
            .Should().NotContain(seed);
    }

    [Fact]
    public async Task Save_Edit_DependentSetChanged_StatusMessageIsSettledSentence_OutsideWitness()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Declaration.SaveResult = Error.Conflict(
            "VersionedRepository.DependentSetChanged",
            "Có thay đổi khác vừa được ghi trong lúc thao tác này đang chuẩn bị. Vui lòng thử lại.");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be(DependentSetChangedMessage);
    }

    [Fact]
    public async Task Close_VersionAlreadyEnded_StatusMessageIsSettledSentence_OutsideWitness()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        h.Declaration.CloseResult = Error.Validation(
            VersionCloseRules.Codes.VersionAlreadyEnded, "SEED-DESCRIPTION-MUST-NOT-LEAK");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Vai trò đã hết hiệu lực.");
    }

    [Fact]
    public async Task Save_Add_CodeInUse_StatusMessageIsSettledSentence_OutsideWitness()
    {
        var h = Build();
        FillValidAddForm(h.Vm);
        h.Declaration.SaveResult = Error.Validation("Role.CodeInUse", "SEED-DESCRIPTION-MUST-NOT-LEAK");

        await h.Vm.SaveCommand.Execute();

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Mã vai trò đã được sử dụng.");
        h.Vm.StatusMessage.Should().NotContain("SEED-DESCRIPTION-MUST-NOT-LEAK");
    }

    [Fact]
    public async Task LoadAsync_NoCoverage_StatusMessageIsSettledSentence_OutsideWitness()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Error.NotFound(
            "EffectivePeriod.NoCoverage", "Tham số 'RoleVersionRow' chưa có giá trị hiệu lực");

        await h.Vm.LoadAsync(5, Today);

        h.Vm.Severity.Should().Be(StatusSeverity.Error);
        h.Vm.StatusMessage.Should().Be("Vai trò không hiệu lực tại ngày đã chọn.");
        h.Vm.StatusMessage.Should().NotContain("RoleVersionRow");
    }

    // Brief 163 step 6: Role.CodeNotAscii is unreachable — client RoleCodePattern excludes non-ASCII
    // before SaveRoleDeclarationAsync. Evidence shape: declaration never invoked (not merely that a
    // guard fires).
    [Fact]
    public async Task Save_NonAsciiRoleCode_NeverInvokesDeclarationService_CodeNotAsciiUnreachable()
    {
        var h = Build();
        h.Vm.BeginAddCommand.Execute();
        h.Vm.RoleCode = "váitrò"; // non-ASCII letters — would be Role.CodeNotAscii if it reached the service
        h.Vm.RoleName = "Tên hợp lệ đủ dài ký tự";

        await h.Vm.SaveCommand.Execute();

        h.Declaration.SaveCallCount.Should().Be(0,
            "Role.CodeNotAscii must not reach FormatSaveError — ValidateFields blocks before the service call");
        h.Vm.Severity.Should().Be(StatusSeverity.Error);
    }

    [Fact]
    public async Task Save_Edit_MissingRoleId_ShowsP2S13()
    {
        var h = Build();
        // BeginEditCommand.Execute bypasses CanExecute — Mode=Editing with no loaded role.
        h.Vm.OverrideGrantsReadinessForTest(GrantsReadiness.Resolved);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleCode = "clerk_role";
        h.Vm.RoleName = "Tên hợp lệ đủ dài ký tự";
        await h.Vm.SaveCommand.Execute();
        h.Vm.StatusMessage.Should().Be("Người dùng chọn vai trò trước khi sửa.");
        // The _currentVersionId-null arm uses the identical P2-S13 literal (same source string);
        // no public API can set role id without version id, so that arm is covered by source identity.
    }

    [Fact]
    public async Task Save_Edit_ReloadFail_And_Close_HistoryFail_BothUseP2S2()
    {
        var h = Build();
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên");
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginEditCommand.Execute();
        h.Vm.RoleName = "Tên mới đủ dài";
        h.Permissions.ThrowOnGetActiveGrants = new InvalidOperationException("db down");

        await h.Vm.SaveCommand.Execute();

        h.Vm.StatusMessage.Should().Be("Đã lưu. Dữ liệu hiển thị chưa cập nhật.",
            "FinishSaveSuccessAsync reload-fail site");

        h.Permissions.ThrowOnGetActiveGrants = null;
        h.Roles.ByIdentityResult = Role(5, "admin_role", "Quản trị viên", from: Today.AddDays(-10));
        await h.Vm.LoadAsync(5, Today);
        h.Vm.BeginCloseCommand.Execute();
        h.Roles.ThrowOnGetHistory = new InvalidOperationException("history down");

        await h.Vm.SaveCommand.Execute();

        h.Vm.StatusMessage.Should().Be("Đã lưu. Dữ liệu hiển thị chưa cập nhật.",
            "RefreshHistoryPreservingMessageAsync after close — same P2-S2 sentence");
        h.Vm.Severity.Should().Be(StatusSeverity.Warning);
    }
}
