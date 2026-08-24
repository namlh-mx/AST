using System.ComponentModel;
using System.Globalization;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Core.Presentation;
using AST.Core.Time;
using AST.Shell.Presentation;
using AST.Shell.ViewModels.Iam;
using ErrorOr;
using FluentAssertions;

namespace AST.Shell.Tests.ViewModels;

public class OrgUnitDeclarationViewModelTests
{
    private static readonly DateOnly Today = new(2026, 7, 24);

    private sealed class FixedDates(DateOnly today) : IBusinessDateProvider
    {
        public DateOnly Today { get; } = today;
    }

    // Hand-written fake (project convention — no mocking framework). Only GetByIdentityAsync is needed by
    // this task; other members throw to make an accidental extra call fail loudly rather than silently no-op.
    private sealed class FakeOrgUnitRepository : IOrgUnitRepository
    {
        public ErrorOr<OrgUnitVersionDto> ByIdentityResult { get; set; } = Error.NotFound();
        public ErrorOr<OrgUnitVersionDto>? ByIdentityResultAfterClose { get; set; }
        private int _byIdentityCallCount;
        // Item 6 (2026-08-10 fix round): exposed so a test can assert LoadFromHistoryRow never calls the
        // repository, not just that it produces no error banner.
        public int ByIdentityCallCount => _byIdentityCallCount;
        // Optional per-call gate (same style as BeforeInScopeReturn) so a test can hold one identity
        // while a later LoadAsync supersedes it.
        public Func<long, DateOnly, Task>? BeforeByIdentityReturn { get; set; }
        public Dictionary<long, ErrorOr<OrgUnitVersionDto>> ByIdentityByOrgUnitId { get; } = new();

        public Func<int, ErrorOr<OrgUnitVersionDto>>? ByIdentityResultFactory { get; set; }

        public async Task<ErrorOr<OrgUnitVersionDto>> GetByIdentityAsync(long orgUnitId, DateOnly asOf)
        {
            if (BeforeByIdentityReturn is { } before)
                await before(orgUnitId, asOf);

            if (ByIdentityByOrgUnitId.TryGetValue(orgUnitId, out var byId))
                return byId;

            _byIdentityCallCount++;
            if (ByIdentityResultFactory is { } factory)
                return factory(_byIdentityCallCount);

            var result = _byIdentityCallCount > 1 && ByIdentityResultAfterClose is { } after ? after : ByIdentityResult;
            return result;
        }

        public IReadOnlyList<OrgUnitVersionDto> InScopeResult { get; set; } = [];
        public DataScope? LastInScopeScope { get; private set; }
        public DateOnly? LastInScopeAsOf { get; private set; }
        public long CreateIdentityResult { get; set; }
        public int CreateIdentityCallCount { get; private set; }
        public ErrorOr<UpsertResult> UpsertResult { get; set; } = new UpsertResult(1, [], []);
        public long? LastUpsertOrgUnitId { get; private set; }
        public string? LastUpsertOrgCode { get; private set; }
        public long? LastUpsertParentId { get; private set; }
        public OrgUnitSupplementalDto? LastUpsertSupplemental { get; private set; }
        public VersionOperationKind? LastUpsertOperationKind { get; private set; }

        public Exception? InScopeException { get; set; }
        public int InScopeCallCount { get; private set; }
        // Optional gate so a test can hold the first GetInScopeAsync until a later LoadTreeAsync supersedes it.
        public Func<Task>? BeforeInScopeReturn { get; set; }
        // When false, Upsert/Close/CancelPlan do not auto-seed InScopeResult (FR1: just-saved id may be
        // absent from GetInScopeAsync at the current tree as-of).
        public bool AutoAddWrittenIdentityToInScope { get; set; } = true;

        public async Task<IReadOnlyList<OrgUnitVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf)
        {
            InScopeCallCount++;
            LastInScopeScope = scope;
            LastInScopeAsOf = asOf;
            if (InScopeException is { } ex)
                throw ex;
            if (BeforeInScopeReturn is { } before)
                await before();
            return InScopeResult;
        }

        // NOT an IOrgUnitRepository member any more (2026-08-17): production mints inside the composite
        // transaction, so the interface cannot produce a zero-version header. Kept on the fake because
        // FakeOrgUnitDeclarationService calls it to model what the real service does. There is deliberately
        // no DeleteEmptyIdentityAsync counterpart: the rollback is what removes a header now, and a fake
        // that still offered a compensation would model a behaviour that must no longer exist.
        public Task<long> CreateIdentityAsync()
        {
            CreateIdentityCallCount++;
            return Task.FromResult(CreateIdentityResult);
        }

        public Task<ErrorOr<UpsertResult>> UpsertAsync(long orgUnitId, EffectivePeriod period, string orgCode, string orgNameFullVn, string orgNameShortVn, long? parentId, VersionOperationKind operationKind, string recordedBy, string? reason, OrgUnitSupplementalDto? supplemental = null)
        {
            LastUpsertOrgUnitId = orgUnitId;
            LastUpsertOrgCode = orgCode;
            LastUpsertParentId = parentId;
            LastUpsertSupplemental = supplemental;
            LastUpsertOperationKind = operationKind;
            // Post-save tree refresh usually needs the just-saved identity present in GetInScopeAsync.
            if (AutoAddWrittenIdentityToInScope
                && !UpsertResult.IsError
                && InScopeResult.All(u => u.OrgUnitId != orgUnitId))
            {
                InScopeResult = InScopeResult.Append(
                    Dto(orgUnitId, parentId, period.From, period.To, orgCode: orgCode,
                        orgNameFullVn: orgNameFullVn, orgNameShortVn: orgNameShortVn)).ToList();
            }
            return Task.FromResult(UpsertResult);
        }
        public ErrorOr<UpsertResult> CloseResult { get; set; } = new UpsertResult(0, [], []);
        public ErrorOr<UpsertResult> CancelPlanResult { get; set; } = new UpsertResult(0, [], []);
        public long? LastCloseOrgUnitId { get; private set; }
        public long? LastCloseVersionId { get; private set; }
        public DateOnly? LastCloseNewTo { get; private set; }
        public long? LastCancelOrgUnitId { get; private set; }
        public long? LastCancelVersionId { get; private set; }
        public int CancelPlanCallCount { get; private set; }

        public Task<ErrorOr<UpsertResult>> CloseVersionAsync(long orgUnitId, long versionId, DateOnly newTo, OperationDate operationDate, string recordedBy, string? reason)
        {
            _ = operationDate;
            LastCloseOrgUnitId = orgUnitId;
            LastCloseVersionId = versionId;
            LastCloseNewTo = newTo;
            if (AutoAddWrittenIdentityToInScope
                && !CloseResult.IsError
                && InScopeResult.All(u => u.OrgUnitId != orgUnitId))
            {
                InScopeResult = InScopeResult.Append(
                    Dto(orgUnitId, parentId: null, Today, newTo)).ToList();
            }
            return Task.FromResult(CloseResult);
        }

        public Task<ErrorOr<UpsertResult>> DeleteVersionAsync(long orgUnitId, long versionId) => throw new NotSupportedException();

        public Task<ErrorOr<UpsertResult>> CancelPlanAsync(long orgUnitId, long versionId, DateOnly operationDate, string recordedBy, string reason)
        {
            CancelPlanCallCount++;
            LastCancelOrgUnitId = orgUnitId;
            LastCancelVersionId = versionId;
            if (AutoAddWrittenIdentityToInScope
                && !CancelPlanResult.IsError
                && InScopeResult.All(u => u.OrgUnitId != orgUnitId))
            {
                InScopeResult = InScopeResult.Append(
                    Dto(orgUnitId, parentId: null, Today, EffectivePeriod.OpenEnd)).ToList();
            }
            return Task.FromResult(CancelPlanResult);
        }

        public IReadOnlyList<OrgUnitVersionDto> PreviewResult { get; set; } = [];

        public Task<IReadOnlyList<OrgUnitVersionDto>> PreviewUpsertAsync(long orgUnitId, EffectivePeriod period) => Task.FromResult(PreviewResult);

        public IReadOnlyList<OrgUnitVersionDto> HistoryResult { get; set; } = [];
        public long? LastHistoryOrgUnitId { get; private set; }
        public DataScope? LastHistoryScope { get; private set; }
        public int HistoryCallCount { get; private set; }
        public Exception? HistoryException { get; set; }

        // Async gate + per-call result selector, for tests that need two GetHistoryInScopeAsync calls
        // to complete out of order (staleness/generation-guard races). Mirrors BeforeByIdentityReturn's shape.
        public Func<long?, Task>? BeforeHistoryReturn { get; set; }
        public Func<long?, IReadOnlyList<OrgUnitVersionDto>>? HistoryResultFactory { get; set; }

        public async Task<IReadOnlyList<OrgUnitVersionDto>> GetHistoryInScopeAsync(DataScope scope, long? orgUnitId = null)
        {
            LastHistoryScope = scope;
            HistoryCallCount++;
            LastHistoryOrgUnitId = orgUnitId;
            if (BeforeHistoryReturn is { } hook)
                await hook(orgUnitId);
            if (HistoryException is { } ex)
                throw ex;
            return HistoryResultFactory is { } factory ? factory(orgUnitId) : HistoryResult;
        }

        public IReadOnlyList<OrgUnitPickerItem> EligibleParentsResult { get; set; } = [];
        public Exception? EligibleParentsException { get; set; }
        public TaskCompletionSource<IReadOnlyList<OrgUnitPickerItem>>? EligibleParentsTcs { get; set; }

        public Task<IReadOnlyList<OrgUnitPickerItem>> GetEligibleParentsAsync(DataScope scope, EffectivePeriod childPeriod)
        {
            if (EligibleParentsException is { } ex)
                return Task.FromException<IReadOnlyList<OrgUnitPickerItem>>(ex);
            if (EligibleParentsTcs is { } tcs)
                return tcs.Task;
            return Task.FromResult(EligibleParentsResult);
        }

        // Scope-checked-write test double (2026-08-05 security fix, part 2). Defaults to true so every
        // pre-existing test (built with Global scope, which is always "in scope") keeps passing without
        // opting in; scope-denial tests override this to false.
        public bool WithinScopeResult { get; set; } = true;
        public int WithinScopeCallCount { get; private set; }
        public DataScope? LastWithinScopeScope { get; private set; }
        public long? LastWithinScopeOrgUnitId { get; private set; }

        public Task<bool> IsWithinScopeAsync(DataScope scope, long orgUnitId)
        {
            WithinScopeCallCount++;
            LastWithinScopeScope = scope;
            LastWithinScopeOrgUnitId = orgUnitId;
            return Task.FromResult(WithinScopeResult);
        }
    }

    private sealed class FakeCurrentUser(string? username) : ICurrentWindowsUser
    {
        public string? Username { get; } = username;
    }

    private static OrgUnitVersionDto Dto(
        long orgUnitId, long? parentId, DateOnly from, DateOnly to, bool isActive = true, bool cancelled = false, long id = 1,
        OrgUnitSupplementalDto? supplemental = null, string orgCode = "ABC", string orgNameFullVn = "Đon vị đầy đủ",
        string orgNameShortVn = "Đon vị", VersionOperationKind? operationKind = null, string? parentOrgCodeAsOf = null,
        string? parentOrgNameFullVnAsOf = null) =>
        new(
            Id: id, OrgUnitId: orgUnitId, EffectiveFrom: from, EffectiveTo: to, IsActive: isActive,
            OrgCode: orgCode, OrgNameFullVn: orgNameFullVn, OrgNameShortVn: orgNameShortVn,
            ParentId: parentId, RecordedAt: DateTime.UtcNow, RecordedBy: "tester", Reason: "seed",
            Supplemental: supplemental ?? new OrgUnitSupplementalDto(), Cancelled: cancelled,
            OperationKind: operationKind, ParentOrgCodeAsOf: parentOrgCodeAsOf, ParentOrgNameFullVnAsOf: parentOrgNameFullVnAsOf);

    private sealed class FakeAuthorizationService : IAuthorizationService
    {
        public ErrorOr<DataScope> AuthorizeResult { get; set; } = new DataScope(ScopeLevel.Global, null, "tester");

        // Lets a test return a DIFFERENT result per call (e.g. the write-scope check succeeds but a
        // later post-save refresh's ResolveScopeAsync call soft-fails) -- 1-based call index.
        // Falls back to AuthorizeResult when unset, so every existing fixed-result test is unaffected.
        public Func<int, ErrorOr<DataScope>>? AuthorizeResultFactory { get; set; }
        public int AuthorizeCallCount { get; private set; }

        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey)
        {
            AuthorizeCallCount++;
            return Task.FromResult(AuthorizeResultFactory?.Invoke(AuthorizeCallCount) ?? AuthorizeResult);
        }

        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => throw new NotSupportedException();
    }

    private sealed class FakeConfirmationPrompt(bool confirm) : IConfirmationPrompt
    {
        public bool WasCalled { get; private set; }
        public string? LastMessage { get; private set; }
        public IReadOnlyList<string>? LastDetails { get; private set; }

        public Task<bool> ConfirmAsync(string message, IReadOnlyList<string> details)
        {
            WasCalled = true;
            LastMessage = message;
            LastDetails = details;
            return Task.FromResult(confirm);
        }
    }

    private sealed class FakeOrgUnitDeclarationService : IOrgUnitDeclarationService
    {
        public ErrorOr<UpsertResult> CloseResult { get; set; } = new UpsertResult(0, [], []);
        public CloseOrgUnitDeclarationRequest? LastRequest { get; private set; }
        public int CloseCallCount { get; private set; }

        public Task<ErrorOr<UpsertResult>> CloseOrgUnitDeclarationAsync(CloseOrgUnitDeclarationRequest request)
        {
            CloseCallCount++;
            LastRequest = request;
            return Task.FromResult(CloseResult);
        }

        // Add (backlog 0.4b, 2026-08-17). The REAL service mints the identity and then writes the first
        // version through the repository, so this fake does the same against the repository fake -- the Add
        // tests keep asserting the values that actually reached the write, and LastAddRequest is what proves
        // the VM DELEGATED rather than writing the version itself (it can no longer reach either call).
        //
        // The service's own guards -- Global scope, root-period overlap -- are deliberately NOT
        // re-implemented here: a fake enforcing its own rule would only ever prove itself. A test that needs
        // a denial injects it through AddError; the real guards are covered on real MySQL by
        // AST.Modules.IAM.Tests/Integration/OrgUnitDeclarationServiceTests.
        public FakeOrgUnitRepository? Repository { get; set; }
        public Error? AddError { get; set; }
        public AddOrgUnitDeclarationRequest? LastAddRequest { get; private set; }
        public int AddCallCount { get; private set; }

        public async Task<ErrorOr<AddOrgUnitDeclarationResult>> AddOrgUnitDeclarationAsync(
            AddOrgUnitDeclarationRequest request)
        {
            AddCallCount++;
            LastAddRequest = request;

            if (AddError is { } denied)
            {
                return denied;
            }

            if (Repository is null)
            {
                return new AddOrgUnitDeclarationResult(0, new UpsertResult(0, [], []));
            }

            var newId = await Repository.CreateIdentityAsync();
            var write = await Repository.UpsertAsync(
                newId, request.Period, request.OrgCode, request.OrgNameFullVn, request.OrgNameShortVn,
                request.ParentId, VersionOperationKind.Add, "tester", request.Reason, request.Supplemental);

            return write.IsError ? write.Errors : new AddOrgUnitDeclarationResult(newId, write.Value);
        }

        // Edit (backlog 0.7, 2026-08-21). Delegates to the repository fake for the same reason Add does --
        // the existing Edit tests assert the values that reached the write, and they must keep working while
        // the CALLER changes. What the fake does NOT re-implement is the immutability guard itself: it writes
        // the request's ExpectedParentId straight through, so nothing here can make an over-permissive VM
        // look correct. The guard is proven on real MySQL in
        // AST.Modules.IAM.Tests/Integration/OrgUnitDeclarationServiceTests.
        public Error? EditError { get; set; }
        public EditOrgUnitDeclarationRequest? LastEditRequest { get; private set; }
        public int EditCallCount { get; private set; }

        public async Task<ErrorOr<UpsertResult>> EditOrgUnitDeclarationAsync(EditOrgUnitDeclarationRequest request)
        {
            EditCallCount++;
            LastEditRequest = request;

            if (EditError is { } denied)
            {
                return denied;
            }

            if (Repository is null)
            {
                return new UpsertResult(0, [], []);
            }

            return await Repository.UpsertAsync(
                request.OrgUnitId, request.Period, request.OrgCode, request.OrgNameFullVn,
                request.OrgNameShortVn, request.ExpectedParentId, VersionOperationKind.Edit, "tester",
                request.Reason, request.Supplemental);
        }
    }

    private sealed class FakeBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);
        public bool IsBreakGlassAdmin(string username) => _admins.Contains(username);
    }

    // Every builder points the declaration fake at the SAME repository fake, so an Add routed through the
    // service still lands on the repository the test asserts against (see FakeOrgUnitDeclarationService).
    private static FakeOrgUnitDeclarationService BindDeclaration(
        FakeOrgUnitDeclarationService? declaration, FakeOrgUnitRepository repo)
    {
        var service = declaration ?? new FakeOrgUnitDeclarationService();
        service.Repository = repo;
        return service;
    }

    private static (OrgUnitDeclarationViewModel Vm, FakeOrgUnitRepository Repo) Build(
        FakeOrgUnitDeclarationService? declaration = null, IBreakGlassPolicy? breakGlass = null)
    {
        var repo = new FakeOrgUnitRepository();
        var vm = new OrgUnitDeclarationViewModel(
            repo, BindDeclaration(declaration, repo), new FixedDates(Today),
            new FakeCurrentUser("tester"), new FakeAuthorizationService(), new FakeConfirmationPrompt(confirm: true),
            breakGlass ?? new FakeBreakGlassPolicy());
        return (vm, repo);
    }

    private static (OrgUnitDeclarationViewModel Vm, FakeOrgUnitRepository Repo, FakeAuthorizationService Auth) BuildForSave(
        FakeOrgUnitDeclarationService? declaration = null, IBreakGlassPolicy? breakGlass = null)
    {
        var repo = new FakeOrgUnitRepository();
        var auth = new FakeAuthorizationService();
        var vm = new OrgUnitDeclarationViewModel(
            repo, BindDeclaration(declaration, repo), new FixedDates(Today),
            new FakeCurrentUser("tester"), auth, new FakeConfirmationPrompt(confirm: true),
            breakGlass ?? new FakeBreakGlassPolicy());
        return (vm, repo, auth);
    }

    private static (OrgUnitDeclarationViewModel Vm, FakeOrgUnitRepository Repo, FakeConfirmationPrompt Confirm) BuildForEdit(
        bool confirmH2 = true, FakeOrgUnitDeclarationService? declaration = null,
        FakeAuthorizationService? authorization = null, IBreakGlassPolicy? breakGlass = null)
    {
        var repo = new FakeOrgUnitRepository();
        var confirm = new FakeConfirmationPrompt(confirmH2);
        var vm = new OrgUnitDeclarationViewModel(
            repo, BindDeclaration(declaration, repo), new FixedDates(Today),
            new FakeCurrentUser("tester"), authorization ?? new FakeAuthorizationService(), confirm,
            breakGlass ?? new FakeBreakGlassPolicy());
        return (vm, repo, confirm);
    }

    private static void FillValidAddForm(OrgUnitDeclarationViewModel vm)
    {
        vm.OrgCode = "ABCD";
        vm.OrgNameFullVn = "Đơn vị đầy đủ";
        vm.OrgNameShortVn = "Đơn vị";
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;
        vm.Reason = "khai báo mới";
    }

    [Fact]
    public async Task LoadAsync_StaleResultDoesNotOverwriteANewerLoad()
    {
        var (vm, repo) = Build();
        var holdUnit1 = new TaskCompletionSource();
        repo.ByIdentityByOrgUnitId[1] = Error.Failure("OrgUnit.Stale", "slow unit 1 failed");
        repo.ByIdentityByOrgUnitId[2] = Dto(2, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd,
            orgCode: "U2", orgNameFullVn: "Unit Two Full", orgNameShortVn: "Unit Two");
        repo.BeforeByIdentityReturn = async (id, _) =>
        {
            if (id == 1)
                await holdUnit1.Task;
        };

        var slow = vm.LoadAsync(1, Today);
        var fast = vm.LoadAsync(2, Today);
        await fast;

        Assert.Equal("U2", vm.OrgCode);
        Assert.Equal(StatusSeverity.None, vm.Severity);

        holdUnit1.SetResult();
        await slow;

        Assert.Equal("U2", vm.OrgCode);
        Assert.Equal(StatusSeverity.None, vm.Severity);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage));
    }

    [Fact]
    public async Task LoadAsync_PopulatesFieldsFromTheResolvedVersion()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);

        await vm.LoadAsync(1, Today);

        Assert.Equal("ABC", vm.OrgCode);
        Assert.Equal("Đon vị đầy đủ", vm.OrgNameFullVn);
        Assert.Equal("Đon vị", vm.OrgNameShortVn);
        Assert.Equal(Today.AddDays(-10), vm.EffectiveFrom);
        Assert.True(vm.IsUndetermined);
        Assert.False(vm.IsRoot);
    }

    [Fact]
    public async Task LoadAsync_NullParentId_MarksIsRoot()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd);

        await vm.LoadAsync(1, Today);

        Assert.True(vm.IsRoot);
    }

    [Fact]
    public async Task LoadAsync_ComputesStatusViaVersionStatusResolver()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd); // future -> Pending

        await vm.LoadAsync(1, Today);

        Assert.Equal(VersionStatus.Pending, vm.Status);
    }

    [Fact]
    public async Task LoadAsync_DoesNotMarkTheFormDirty()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);

        await vm.LoadAsync(1, Today);

        Assert.False(vm.IsDirty);
        Assert.False(vm.HasUnsavedInput);
    }

    [Fact]
    public async Task LoadAsync_PopulatesSupplementalFromTheResolvedVersion()
    {
        var (vm, repo) = Build();
        var supplemental = new OrgUnitSupplementalDto(
            BusinessNumber: "0101234567",
            AddrLineVn: "123 Đường ABC",
            AdminDivisionLevel: 3,
            Phone: "0909123456",
            Email: "contact@example.com");
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, supplemental: supplemental);

        await vm.LoadAsync(1, Today);

        Assert.Equal(supplemental, vm.Supplemental);
    }

    [Fact]
    public async Task LoadAsync_WithNonDefaultSupplemental_DoesNotMarkTheFormDirty()
    {
        var (vm, repo) = Build();
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, supplemental: supplemental);

        await vm.LoadAsync(1, Today);

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task LoadAsync_NotFound_SetsErrorStatusBanner()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Error.NotFound("OrgUnit.NotFound", "not found");

        await vm.LoadAsync(1, Today);

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }

    // Defect B (2026-08-10): EffectivePeriodResolver.NoCoverage's own message names the C# entity type
    // ("Tham số 'OrgUnitVersionEntity' chưa có giá trị hiệu lực...") -- unusable for an operator. The
    // screen must map the CODE to a plain sentence with no class name, same pattern as FormatCloseError.
    [Fact]
    public async Task LoadAsync_NoCoverage_MapsToAnOperatorSentenceWithNoClassName()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Error.NotFound(
            "EffectivePeriod.NoCoverage",
            "Tham số 'OrgUnitVersionEntity' chưa có giá trị hiệu lực tại ngày 10/08/2026");

        await vm.LoadAsync(1, Today);

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be("Đơn vị này không có phiên bản hiệu lực tại ngày đã chọn.");
        vm.StatusMessage.Should().NotContain("OrgUnitVersionEntity");
        vm.StatusMessage.Should().NotContain("Tham số");
    }

    // Defect A (2026-08-10): the History "Xem" flow must be a ROW-IDENTIFIED read that works for a
    // LAPSED identity -- one whose GetByIdentityAsync/EffectivePeriodResolver route can never resolve
    // (no version covers ANY date, including today), which is exactly the bug this reproduces.
    //
    // Item 6 (2026-08-10 fix round): renamed + asserts ByIdentityCallCount is 0 -- the earlier name
    // claimed "without calling the repository" but only checked for the absence of an error banner,
    // which does not distinguish "never called" from "called and happened to succeed." The row's
    // Status stays Expired (non-actionable) to match the View-level routing split from item 2: an
    // actionable (Effective/Pending) row goes through the fresh LoadAsync path instead, so this test's
    // row shape is exactly the one that ever reaches LoadFromHistoryRow in the real flow.
    [Fact]
    public void LoadFromHistoryRow_LapsedIdentity_NeverCallsTheRepositoryAndPopulatesCardFromTheRowAlone()
    {
        var (vm, repo) = Build();
        // Simulate a lapsed identity: the date-resolved route always fails, exactly like the real
        // GetByIdentityAsync/EffectivePeriodResolver.NoCoverage bug this fix works around.
        repo.ByIdentityResult = Error.NotFound("EffectivePeriod.NoCoverage", "no coverage at any date");
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        var row = new OrgUnitHistoryRow(
            Id: 42, OrgUnitId: 7, EffectiveFrom: Today.AddDays(-100), EffectiveTo: Today.AddDays(-1),
            FromText: "x", ToText: "y", RecordedAtText: "z", StatusText: "Hết hiệu lực",
            Status: VersionStatus.Expired, OrgCode: "LAPS", NameFull: "Đơn vị đã đóng",
            NameShort: "Đơn vị đóng", ParentId: 5, ParentLabel: "P — Cha", Operation: "Đóng",
            RecordedBy: "tester", Reason: "seed", Supplemental: supplemental);

        vm.LoadFromHistoryRow(row);

        repo.ByIdentityCallCount.Should().Be(0);
        vm.Severity.Should().Be(StatusSeverity.None);
        string.IsNullOrEmpty(vm.StatusMessage).Should().BeTrue();
        vm.OrgCode.Should().Be("LAPS");
        vm.OrgNameFullVn.Should().Be("Đơn vị đã đóng");
        vm.OrgNameShortVn.Should().Be("Đơn vị đóng");
        vm.EffectiveFrom.Should().Be(Today.AddDays(-100));
        vm.EffectiveTo.Should().Be(Today.AddDays(-1));
        vm.IsUndetermined.Should().BeFalse();
        vm.ParentId.Should().Be(5);
        vm.IsRoot.Should().BeFalse();
        vm.Status.Should().Be(VersionStatus.Expired);
        vm.Supplemental.Should().Be(supplemental);
        vm.IsDirty.Should().BeFalse();

        // Read-only enforcement: a lapsed (non-current) row can never enable Edit/Close.
        vm.Mode.Should().Be(OrgUnitCardMode.ReadOnly);
        vm.CanEdit.Should().BeFalse();
        vm.CanClose.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_SwitchingToARecordThatFailsToResolve_ClearsThePreviouslyLoadedFields()
    {
        // Reproduces a real UX bug: viewing record A then clicking to view record B (whose
        // period fails to resolve, e.g. missing effective period) must NOT leave A's data on
        // screen underneath B's error banner -- the card must clear regardless of whether the
        // new record loads successfully.
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        Assert.Equal("ABC", vm.OrgCode); // sanity: record A really did load

        repo.ByIdentityResult = Error.NotFound("OrgUnit.NoEffectivePeriod", "no effective period as of today");
        await vm.LoadAsync(2, Today);

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(string.Empty, vm.OrgCode);
        Assert.Equal(string.Empty, vm.OrgNameFullVn);
        Assert.Equal(string.Empty, vm.OrgNameShortVn);
        Assert.Null(vm.EffectiveFrom);
        Assert.Null(vm.EffectiveTo);
        Assert.False(vm.IsRoot);
        Assert.Equal(VersionStatus.None, vm.Status);
    }

    [Fact]
    public void SettingAField_AfterLoad_MarksTheFormDirty()
    {
        var (vm, _) = Build();

        vm.OrgCode = "XYZ";

        Assert.True(vm.IsDirty);
        Assert.True(vm.HasUnsavedInput);
    }

    [Fact]
    public async Task Clear_ResetsFieldsAndDirtyAndStatus()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        vm.OrgCode = "CHANGED";

        vm.Clear();

        Assert.Equal(string.Empty, vm.OrgCode);
        Assert.Equal(VersionStatus.None, vm.Status);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task ClearHistory_ResetsHistoryRows()
    {
        // ClearHistory is the reset for leaving Screen A entirely (OnLeaving) -- brief 049
        // decoupled History from tree/as-of/Show All, so this is now the ONLY View call site;
        // deliberately SEPARATE from Clear() (see the regression guard below).
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.Single(vm.HistoryRows);

        vm.ClearHistory();

        Assert.Empty(vm.HistoryRows);
    }

    [Fact]
    public async Task HistoryFilterText_IsPlainString_UnaffectedByHistoryReload()
    {
        // Brief 045 FR1: filtering is View-owned (ICollectionView). The VM only holds the filter
        // string; Reload/ClearHistory must not wipe or mutate it (View Rebuild attaches Filter to the
        // new collection and Refresh re-reads this property).
        var (vm, repo) = Build();
        Assert.Equal(string.Empty, vm.HistoryFilterText);

        vm.HistoryFilterText = "abc";
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.Equal("abc", vm.HistoryFilterText);

        vm.ClearHistory();
        Assert.Equal("abc", vm.HistoryFilterText);

        vm.HistoryFilterText = "  ";
        Assert.Equal("  ", vm.HistoryFilterText);
    }

    [Fact]
    public async Task Clear_DoesNotResetHistoryRows()
    {
        // Regression guard (fix round 1, batch brief 044): Clear() is also called by LoadAsync,
        // including the History "Xem" path (OnHistoryViewClick -> LoadAsync -> Clear()), which
        // deliberately does NOT reload history afterward -- browsing past versions must keep the
        // History grid showing the SAME unit's rows. Folding a HistoryRows reset into Clear()
        // emptied the grid on every "Xem" click; this guards against reintroducing that.
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.Single(vm.HistoryRows);

        vm.Clear();

        Assert.Single(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadAsync_KeepsHistoryRows()
    {
        // Raised in review (fix round 1): the regression path
        // was LoadAsync -> Clear(), so guard the public entry point too, not just Clear() directly --
        // a future change inside LoadAsync that touches HistoryRows before/after calling Clear()
        // would otherwise slip past Clear_DoesNotResetHistoryRows unnoticed.
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.Single(vm.HistoryRows);
        repo.ByIdentityResult = Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd);

        await vm.LoadAsync(1, Today);

        Assert.Single(vm.HistoryRows);
    }

    [Fact]
    public async Task Clear_ResetsSupplementalToDefault()
    {
        var (vm, repo) = Build();
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3, Phone: "0909123456");
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, supplemental: supplemental);
        await vm.LoadAsync(1, Today);

        vm.Clear();

        Assert.Equal(new OrgUnitSupplementalDto(), vm.Supplemental);
    }

    [Fact]
    public void MarkSupplementalDirty_OnACleanVm_SetsIsDirtyTrue()
    {
        var (vm, _) = Build();
        Assert.False(vm.IsDirty);

        vm.MarkSupplementalDirty();

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void SettingSupplemental_ToADifferentValue_MarksTheFormDirty()
    {
        var (vm, _) = Build();
        Assert.False(vm.IsDirty);
        Assert.Equal(new OrgUnitSupplementalDto(), vm.Supplemental);

        vm.Supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);

        Assert.True(vm.IsDirty);
    }

    // Card context -> (isActive, cancelled, effectiveFrom, parentId) crafted so VersionStatusResolver naturally
    // produces the row's §2.7.3 status via the real LoadAsync path — no reflection, no bypassing the resolver.
    public static IEnumerable<object?[]> ButtonMatrixCases()
    {
        // name, isActive, cancelled, effectiveFrom (offset days from Today as string, or null), parentId, canAdd, canEdit, canClose
        // object?[] + IEnumerable<object?[]>: bare null under NRT+TreatWarningsAsErrors (brief's [] form is CS8625).
        yield return new object?[] { "Bi huy", false, true, null, 5L, true, false, false };
        yield return new object?[] { "Het hieu luc", false, false, null, 5L, true, false, false };
        yield return new object?[] { "Hieu luc non-root", true, false, "-1", 5L, true, true, true };
        yield return new object?[] { "Hieu luc root", true, false, "-1", null, true, true, false };
        yield return new object?[] { "Cho hieu luc", true, false, "1", 5L, true, true, true };
    }

    [Theory]
    [MemberData(nameof(ButtonMatrixCases))]
    public async Task ButtonMatrix_MatchesSpec2710(
        string _, bool isActive, bool cancelled, string? fromOffsetDays, long? parentId, bool canAdd, bool canEdit, bool canClose)
    {
        var (vm, repo) = Build();
        var from = fromOffsetDays is null ? Today.AddDays(-30) : Today.AddDays(int.Parse(fromOffsetDays));
        repo.ByIdentityResult = Dto(1, parentId, from, EffectivePeriod.OpenEnd, isActive, cancelled);

        await vm.LoadAsync(1, Today);

        Assert.Equal(canAdd, vm.CanAdd);
        Assert.Equal(canEdit, vm.CanEdit);
        Assert.Equal(canClose, vm.CanClose);
    }

    [Fact]
    public void ButtonMatrix_EmptyCard_OnlyAddIsEnabled()
    {
        var (vm, _) = Build(); // fresh VM, never loaded -> Status == None, IsRoot == false

        Assert.True(vm.CanAdd);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanClose);
    }

    [Fact]
    public void MutatingMode_DisablesAddEditCloseAndEnablesCancel()
    {
        var (vm, _) = Build();

        vm.BeginAddCommand.Execute();

        Assert.Equal(OrgUnitCardMode.Adding, vm.Mode);
        Assert.False(vm.CanAdd);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanClose);
        Assert.True(vm.CanCancel);
    }

    [Fact]
    public async Task BeginEditCommand_KeepsTheLoadedFieldValues()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);

        vm.BeginEditCommand.Execute();

        Assert.Equal(OrgUnitCardMode.Editing, vm.Mode);
        Assert.Equal("ABC", vm.OrgCode);
    }

    [Fact]
    public async Task BeginCloseCommand_Alone_DoesNotMarkTheFormDirty()
    {
        // Regression: ExecuteBeginClose clears open-end via public IsUndetermined/EffectiveTo setters,
        // which call MarkDirty — clicking Đóng alone must not trip leave-confirm before any real edit.
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        Assert.False(vm.IsDirty);

        vm.BeginCloseCommand.Execute();

        Assert.False(vm.IsDirty);
        Assert.Equal(OrgUnitCardMode.Closing, vm.Mode);
    }

    [Fact]
    public async Task CancelCommand_RestoresFieldsAsOfTheLastLoadAndReturnsToReadOnly()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CHANGED";

        await vm.CancelCommand.Execute();

        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
        Assert.Equal("ABC", vm.OrgCode);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task CancelCommand_AfterBeginAddOverALoadedRecord_RestoresStatusAndIsRootToo()
    {
        // Regression: ExecuteBeginAdd snapshots then Clear()s the form (wiping Status/IsRoot to None/false).
        // Cancel must restore those too, not just the editable fields -- otherwise the card shows record A's
        // fields again but with the WRONG button-matrix state (e.g. CanEdit/CanClose false for an Effective,
        // non-root record that was editable one click earlier).
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd); // Effective, non-root
        await vm.LoadAsync(1, Today);
        Assert.Equal(VersionStatus.Effective, vm.Status);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanClose);

        vm.BeginAddCommand.Execute();
        await vm.CancelCommand.Execute();

        Assert.Equal(VersionStatus.Effective, vm.Status);
        Assert.False(vm.IsRoot);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanClose);
    }

    [Fact]
    public async Task CancelCommand_AfterBeginAddOverALoadedRecord_ReenablesSupplementalAfterIdentityRestored()
    {
        // Regression: ExecuteCancel sets Mode=ReadOnly (raising CanOpenSupplemental) BEFORE RestoreSnapshot
        // repopulates _orgUnitId. At raise time _orgUnitId is still null from Clear(), so a binding that
        // caches on PropertyChanged sees false; RestoreSnapshot must re-raise after identity is restored.
        // (A bare Assert on the live getter cannot catch this — the getter re-evaluates after restore.)
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        Assert.True(vm.CanOpenSupplemental);

        bool? lastNotifiedCanOpenSupplemental = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrgUnitDeclarationViewModel.CanOpenSupplemental))
                lastNotifiedCanOpenSupplemental = vm.CanOpenSupplemental;
        };

        vm.BeginAddCommand.Execute();
        await vm.CancelCommand.Execute();

        Assert.True(vm.CanOpenSupplemental);
        Assert.True(lastNotifiedCanOpenSupplemental);
    }

    [Fact]
    public async Task CancelCommand_AfterBeginEditWithSupplementalDialogCommit_RestoresSupplementalAsOfTheLastLoad()
    {
        // Regression: FieldSnapshot does not capture Supplemental at all (brief-036 added the Supplemental
        // property but left the snapshot untouched), so Cancel does not revert a supplemental-dialog "Lưu"
        // that committed a new value while Editing -- mirrors the Phase 4a gap where Status/IsRoot were
        // originally left out of the snapshot (see CancelCommand_AfterBeginAddOverALoadedRecord_RestoresStatusAndIsRootToo).
        var (vm, repo) = Build();
        var loadedSupplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, supplemental: loadedSupplemental);
        await vm.LoadAsync(1, Today);
        Assert.Equal(loadedSupplemental, vm.Supplemental);

        vm.BeginEditCommand.Execute();
        // Simulate the supplemental dialog committing a NEW value via its own "Lưu" while Editing.
        vm.Supplemental = new OrgUnitSupplementalDto(BusinessNumber: "9999999999", AdminDivisionLevel: 1, Email: "changed@example.com");

        await vm.CancelCommand.Execute();

        Assert.Equal(loadedSupplemental, vm.Supplemental);
    }

    [Fact]
    public async Task CancelCommand_AfterBeginAddOverALoadedRecord_RestoresSupplementalToo()
    {
        // Regression: ExecuteBeginAdd snapshots then Clear()s the form (wiping Supplemental to new()) --
        // Cancel must restore the PREVIOUSLY-loaded record's Supplemental too, not leave it at Clear()'s
        // wiped default (mirrors CancelCommand_AfterBeginAddOverALoadedRecord_RestoresStatusAndIsRootToo).
        var (vm, repo) = Build();
        var loadedSupplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, supplemental: loadedSupplemental);
        await vm.LoadAsync(1, Today);
        Assert.Equal(loadedSupplemental, vm.Supplemental);

        vm.BeginAddCommand.Execute();
        Assert.Equal(new OrgUnitSupplementalDto(), vm.Supplemental); // Clear() wiped it to default

        await vm.CancelCommand.Execute();

        Assert.Equal(loadedSupplemental, vm.Supplemental);
    }

    [Fact]
    public async Task CanOpenSupplemental_True_InClosingMode()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);

        vm.BeginCloseCommand.Execute();

        Assert.Equal(OrgUnitCardMode.Closing, vm.Mode);
        Assert.True(vm.CanOpenSupplemental);
    }

    [Fact]
    public async Task CanOpenSupplemental_True_InReadOnly_WithLoadedIdentity()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);

        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
        Assert.True(vm.CanOpenSupplemental);
    }

    [Fact]
    public void CanOpenSupplemental_False_InReadOnly_WithNoIdentity()
    {
        var (vm, _) = Build();

        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
        Assert.False(vm.CanOpenSupplemental);
    }

    [Fact]
    public void CanOpenSupplemental_False_InAddingMode_WithIncompleteIdentity()
    {
        var (vm, _) = Build();

        vm.BeginAddCommand.Execute();

        Assert.Equal(OrgUnitCardMode.Adding, vm.Mode);
        Assert.False(vm.CanOpenSupplemental);
    }

    [Fact]
    public void BeginAddCommand_ClearsTheFormForANewIdentity()
    {
        var (vm, _) = Build();

        vm.BeginAddCommand.Execute();

        Assert.Equal(OrgUnitCardMode.Adding, vm.Mode);
        Assert.Equal(string.Empty, vm.OrgCode);
    }

    [Fact]
    public void SettingReason_MarksTheFormDirty()
    {
        var (vm, _) = Build();

        vm.Reason = "vì lý do X";

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Clear_ResetsReason()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        vm.Reason = "draft reason";

        vm.Clear();

        Assert.Equal(string.Empty, vm.Reason);
    }

    [Fact]
    public async Task CancelCommand_RestoresReason_EvenThoughItWasNeverPartOfTheLoadedRecord()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        vm.BeginEditCommand.Execute();
        vm.Reason = "typed while editing";

        await vm.CancelCommand.Execute();

        Assert.Equal(string.Empty, vm.Reason);
    }

    [Fact]
    public void BeginAdd_FromEmptyCard_NoTreeContext_ParentUnlockedAndCandidatesEmptyUntilEpEntered()
    {
        var (vm, _) = Build();

        vm.BeginAddCommand.Execute();

        Assert.False(vm.IsParentLocked);
        Assert.Empty(vm.ParentCandidates);
        Assert.Null(vm.ParentId);
    }

    [Fact]
    public async Task BeginAdd_AfterLoadingANodeThatCoversTheDefaultEmptyForm_ParentPreFilledAndLocked()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd); // loaded node = identity 1
        await vm.LoadAsync(1, Today);

        vm.BeginAddCommand.Execute();

        // Form EP is not fully entered yet (EffectiveFrom is null after Clear()) -- optimistic pre-fill from
        // the tree context, per N3/N7's "pre-filled; disabled" row.
        Assert.True(vm.IsParentLocked);
        Assert.Equal(1, vm.ParentId);
    }

    [Fact]
    public async Task DuringAdd_EnteringAnEpTheLockedParentDoesNotCover_UnlocksAndPopulatesCandidatesFromThePicker()
    {
        var (vm, repo) = Build();
        // Tree context: identity 1 is only effective 2020-01-01 .. 2020-12-31 (closed).
        repo.ByIdentityResult = Dto(1, parentId: null, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        await vm.LoadAsync(1, Today);
        vm.BeginAddCommand.Execute();
        var expectedCandidates = new List<OrgUnitPickerItem> { new(9, "PAR — Cha") };
        repo.EligibleParentsResult = expectedCandidates;

        // Form EP starts in 2021 -- identity 1's coverage (ends 2020-12-31) does NOT cover it.
        vm.EffectiveFrom = new DateOnly(2021, 1, 1);
        vm.IsUndetermined = true;

        Assert.False(vm.IsParentLocked);
        Assert.Null(vm.ParentId);
        Assert.Equal(expectedCandidates, vm.ParentCandidates);
    }

    [Fact]
    public async Task DuringAdd_EnteringAnEpTheLockedParentDoesCover_StaysLocked()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: null, new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        vm.BeginAddCommand.Execute();

        vm.EffectiveFrom = new DateOnly(2021, 1, 1);
        vm.IsUndetermined = true;

        Assert.True(vm.IsParentLocked);
        Assert.Equal(1, vm.ParentId);
    }

    [Fact]
    public void DuringAdd_ParentCandidatesQueryThrows_SurfacesAnErrorInstadOfSilentlyStayingEmpty()
    {
        var (vm, repo) = Build();
        repo.EligibleParentsException = new InvalidOperationException("connection lost");

        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }

    [Fact]
    public async Task Save_Add_BlankNote_IsAccepted()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = 9;
        vm.Reason = "";

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Equal(1, repo.CreateIdentityCallCount);
    }

    [Theory]
    [InlineData("AB")]      // too short
    [InlineData("abcd")]    // lowercase (spec: ALL CAPS)
    [InlineData("AB CD")]   // space
    [InlineData("ABCDEFGHI")] // too long
    public async Task Save_Add_InvalidOrgCode_BlocksWithClearError(string badCode)
    {
        var (vm, _, _) = BuildForSave();
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.OrgCode = badCode;

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    // Defect C (2026-08-10): requester-specified exact wording, replacing "Ngày Đến phải sau ngày Từ."
    // Item 4 (2026-08-10 fix round): requester picked the SENTENCE fix over the rule fix -- the guard
    // itself stays `EffectiveTo < EffectiveFrom` (a one-day period, To == From, is legal), only the
    // wording changed to stop promising a stricter rule than what is enforced.
    [Fact]
    public async Task Save_Add_EffectiveToBeforeEffectiveFrom_BlocksWithTheRewordedMessage()
    {
        var (vm, _, _) = BuildForSave();
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.IsUndetermined = false;
        vm.EffectiveFrom = Today;
        vm.EffectiveTo = Today.AddDays(-1);

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be("Ngày kết thúc hiệu lực không được trước ngày bắt đầu hiệu lực.");
    }

    // P7 for Add now runs INSIDE the service, so this screen no longer calls AuthorizeAsync on this branch
    // (same posture as the close branch). Fail-closed is still the screen's contract: a denial from the
    // service writes nothing and shows a Vietnamese sentence, never the English Description.
    [Fact]
    public async Task Save_Add_NotAuthorized_BlocksFailClosed_DoesNotWrite()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            AddError = Error.Forbidden("Authz.NotGranted", string.Empty),
        };
        var (vm, repo, auth) = BuildForSave(declaration);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be("Bạn không có quyền thực hiện thao tác này.");
        repo.CreateIdentityCallCount.Should().Be(0);
        auth.AuthorizeCallCount.Should().Be(0, "the VM must not re-authorize what the service already gates");
    }

    [Fact]
    public async Task Save_Add_Root_WhenNoRootExists_Succeeds()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd) with { OrgUnitId = 42 };
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        // ParentId left null = attempting a root.

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
    }

    // Root uniqueness moved into IOrgUnitDeclarationService with the Global gate, and its RULE changed at the
    // same time (requester ruling 2026-08-17): it is about an OVERLAPPING PERIOD, not "a root exists". The
    // screen's remaining job is the wording — which had to change too, because "Đã có đơn vị gốc" would now
    // tell the operator something is permanently impossible when only these dates are.
    // Requester F5, 2026-08-17: editing a unit's effective period surfaced the RAW ENGLISH
    // "Org code ... is already in use by another org unit for an overlapping effective period." The Add path
    // had just been given Vietnamese wording for that same code, so one screen showed one code two ways.
    // Both write codes are checked on the EDIT path here; the NotContain assertions are the discriminator —
    // asserting only the Vietnamese sentence would still pass if the map were bypassed and both strings were
    // concatenated.
    [Theory]
    [InlineData("OrgUnit.CodeInUse", "Org code 'ABCD' is already in use by another org unit for an overlapping effective period.",
        "Mã đơn vị này đã được dùng cho một đơn vị khác trong khoảng thời gian trùng nhau.")]
    [InlineData("TemporalFk.ParentGap", "parent gap",
        "Đơn vị cha không có hiệu lực trong suốt kỳ hiệu lực của đơn vị này — chọn đơn vị cha khác hoặc sửa kỳ hiệu lực.")]
    public async Task Save_Edit_WriteError_SurfacesVietnamese_NotTheEngineDescription(
        string code, string englishDescription, string expectedVietnamese)
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX"; // the loaded Dto's default "ABC" is 3 chars, below the §2.2 4-8 minimum
        vm.OrgNameFullVn = "Tên mới";
        vm.Reason = "sửa kỳ hiệu lực";
        // Widen the period past the parent's — the shape the requester hit at F5.
        vm.EffectiveFrom = Today.AddDays(-20);
        vm.IsUndetermined = true;
        repo.UpsertResult = Error.Validation(code, englishDescription);

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be(expectedVietnamese);
        vm.StatusMessage.Should().NotContain("Org code");
        vm.StatusMessage.Should().NotContain("parent gap");
    }

    // Reused by the two tests that assert this wording, so a reworded message fails in ONE place.
    private const string RootPeriodOverlapsMessage =
        "Đã có đơn vị gốc hiệu lực trong khoảng thời gian này — hãy chọn đơn vị cha, hoặc chọn kỳ hiệu lực khác.";

    [Fact]
    public async Task Save_Add_ServiceReportsRootPeriodOverlap_ShowsThePeriodWording_AndWritesNothing()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            AddError = Error.Validation(
                "OrgUnit.RootPeriodOverlaps", "Another root org unit is already effective during […]."),
        };

        var (vm, repo, _) = BuildForSave(declaration);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        // ParentId left null = a root attempt.

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be(RootPeriodOverlapsMessage);
        repo.CreateIdentityCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Save_Add_NonRoot_WithParentSelected_ReachesTheWriteWithTheFormsValues()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = 9;

        await vm.SaveCommand.Execute();

        Assert.Equal(1, repo.CreateIdentityCallCount);
        Assert.NotNull(repo.LastUpsertOrgUnitId);
        Assert.Equal(42, repo.LastUpsertOrgUnitId);
        Assert.Equal("ABCD", repo.LastUpsertOrgCode);
        Assert.Equal(9, repo.LastUpsertParentId);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Add_NonRoot_WithParentSelected_ReachesTheWriteWithTheSupplemental()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = 9;
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        vm.Supplemental = supplemental;

        await vm.SaveCommand.Execute();

        Assert.Equal(supplemental, repo.LastUpsertSupplemental);
    }

    // Was Save_Add_RepositoryError_SurfacesTheErrorDescription, which asserted the raw English Description
    // reached the operator. TemporalFk.ParentGap is now mapped (2026-08-17): a write error must arrive as a
    // Vietnamese sentence, and "parent gap" must NOT — that assertion is the discriminator.
    [Fact]
    public async Task Save_Add_ParentGap_SurfacesVietnamese_NotTheEngineDescription()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = Error.Failure("TemporalFk.ParentGap", "parent gap");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = 9;

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().NotContain("parent gap");
        vm.StatusMessage.Should().Be(
            "Đơn vị cha không có hiệu lực trong suốt kỳ hiệu lực của đơn vị này — chọn đơn vị cha khác hoặc sửa kỳ hiệu lực.");
    }

    // Replaces Save_Add_RepositoryError_DeletesTheOrphanedIdentity (deleted 2026-08-17, backlog 0.4b): the
    // VM no longer mints, so there is no orphan for it to compensate -- the service's transaction rolls both
    // rows back instead, proven on real MySQL by AST.Modules.IAM.Tests. What the VM still owes is that it
    // DELEGATES the whole Add, carrying the form's values and asserting no authority of its own.
    [Fact]
    public async Task Save_Add_DelegatesTheWholeRequestToTheDeclarationService()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForSave(declaration);
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = 9;
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "0101234567", AdminDivisionLevel: 3);
        vm.Supplemental = supplemental;

        await vm.SaveCommand.Execute();

        declaration.AddCallCount.Should().Be(1);
        var request = declaration.LastAddRequest.Should().BeOfType<AddOrgUnitDeclarationRequest>().Subject;
        request.OrgCode.Should().Be("ABCD");
        request.OrgNameFullVn.Should().Be("Đơn vị đầy đủ");
        request.ParentId.Should().Be(9);
        request.Period.From.Should().Be(Today);
        request.Period.To.Should().Be(EffectivePeriod.OpenEnd);
        request.Supplemental.Should().Be(supplemental);
    }

    // THE DISCRIMINATOR: the request type carries no actor and no scope, so this screen cannot assert either
    // -- the service derives both. A compile-time property, pinned here so widening the DTO fails loudly.
    [Fact]
    public void AddOrgUnitDeclarationRequest_CarriesNoActorAndNoScope()
    {
        var names = typeof(AddOrgUnitDeclarationRequest).GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain(n => n.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("Scope", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("RecordedBy", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("OperationKind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_Add_ReloadFails_DoesNotOverwriteTheErrorWithSuccess()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 42;
        repo.UpsertResult = new UpsertResult(1, [], []);
        repo.ByIdentityResult = Error.NotFound(); // the post-save reload itself fails
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.NotEqual("Đã lưu.", vm.StatusMessage);
    }

    [Fact]
    public void CanSave_TrueOnlyWhileMutating()
    {
        var (vm, _, _) = BuildForSave();

        Assert.False(vm.CanSave);

        vm.BeginAddCommand.Execute();

        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Save_Edit_NoOverlap_SavesDirectly_WithoutAskingToConfirm()
    {
        var (vm, repo, confirm) = BuildForEdit();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        // A real PreviewUpsertAsync always includes the version being edited itself (it always overlaps an
        // in-place edit of itself) -- the VM must exclude it by _currentVersionId, not treat it as an H2
        // collision. This is the case that used to falsely trigger a confirm on every ordinary edit.
        repo.PreviewResult = [Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77)];
        repo.UpsertResult = new UpsertResult(2, [], []);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX"; // loaded Dto's default "ABC" is only 3 chars, below the §2.2 4-8 minimum
        vm.OrgNameFullVn = "Tên mới";
        vm.Reason = "sửa tên";

        await vm.SaveCommand.Execute();

        Assert.False(confirm.WasCalled);
        Assert.Equal(1, repo.LastUpsertOrgUnitId); // same identity, no CreateIdentityAsync call
        Assert.Equal(0, repo.CreateIdentityCallCount);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Edit_NoOverlap_PassesSupplementalToUpsert()
    {
        var (vm, repo, confirm) = BuildForEdit();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        repo.PreviewResult = [Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77)];
        repo.UpsertResult = new UpsertResult(2, [], []);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.OrgNameFullVn = "Tên mới";
        vm.Reason = "sửa tên";
        var supplemental = new OrgUnitSupplementalDto(BusinessNumber: "9999999999", Email: "edit@example.com");
        vm.Supplemental = supplemental;

        await vm.SaveCommand.Execute();

        Assert.False(confirm.WasCalled);
        Assert.Equal(supplemental, repo.LastUpsertSupplemental);
    }

    [Fact]
    public async Task Save_Edit_Overlap_AsksToConfirm_ProceedsWhenConfirmed()
    {
        var (vm, repo, confirm) = BuildForEdit(confirmH2: true);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        // id:2 = a genuinely DIFFERENT version row (a future sibling plan), distinct from the loaded version's
        // own id:77 -- this is the actual H2 case: an OTHER version is affected, not self.
        repo.PreviewResult = [Dto(1, parentId: 5, Today.AddDays(30), EffectivePeriod.OpenEnd, id: 2)];
        repo.UpsertResult = new UpsertResult(2, [], []);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.Reason = "kế hoạch nhiều giai đoạn";

        await vm.SaveCommand.Execute();

        Assert.True(confirm.WasCalled);
        Assert.NotNull(confirm.LastDetails);
        Assert.Single(confirm.LastDetails!);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Edit_Overlap_DeclinedConfirm_DoesNotWrite()
    {
        var (vm, repo, confirm) = BuildForEdit(confirmH2: false);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        repo.PreviewResult = [Dto(1, parentId: 5, Today.AddDays(30), EffectivePeriod.OpenEnd, id: 2)];
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.Reason = "kế hoạch nhiều giai đoạn";

        await vm.SaveCommand.Execute();

        Assert.True(confirm.WasCalled);
        Assert.Null(repo.LastUpsertOrgUnitId);
        // Declining leaves the operator in Editing mode to adjust rather than silently dropping the attempt.
        Assert.Equal(OrgUnitCardMode.Editing, vm.Mode);
    }

    [Fact]
    public async Task Save_Edit_ReloadFails_DoesNotOverwriteTheErrorWithSuccess()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        repo.PreviewResult = [];
        repo.UpsertResult = new UpsertResult(2, [], []);
        repo.ByIdentityResultAfterClose = Error.NotFound(); // the post-save reload itself fails
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.Reason = "sửa";

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.NotEqual("Đã lưu.", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Edit_NeverChangesParentId()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        await vm.LoadAsync(1, Today);
        repo.PreviewResult = [];
        repo.UpsertResult = new UpsertResult(2, [], []);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.Reason = "sửa";

        await vm.SaveCommand.Execute();

        Assert.Equal(5, repo.LastUpsertParentId);
    }

    [Fact]
    public async Task Save_Close_EffectiveVersion_CallsDeclarationService_WithTypedEffectiveThrough()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        declaration.CloseResult = new UpsertResult(0, [], []);
        repo.ByIdentityResultAfterClose = Dto(1, parentId: 5, Today.AddDays(-10), Today.AddDays(5), id: 77);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(5);
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        Assert.Equal(1, declaration.CloseCallCount);
        Assert.NotNull(declaration.LastRequest);
        Assert.Equal(1, declaration.LastRequest!.OrgUnitId);
        Assert.Equal(77, declaration.LastRequest.VersionId);
        Assert.Equal(Today.AddDays(5), declaration.LastRequest.EffectiveThrough);
        Assert.Null(repo.LastCloseOrgUnitId);
        Assert.Equal(0, repo.CancelPlanCallCount);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Close_MapsVersionCloseCloseDateInPast_ToExistingVnMessage()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Validation(VersionCloseRules.Codes.CloseDateInPast, "past"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(-1);
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        Assert.Equal(1, declaration.CloseCallCount);
        // D2: the VN string now states the concrete floor date
        // (today - 1), not the relative word "hôm qua" — pins the requester-directed wording change.
        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be($"Ngày kết thúc phải từ ngày {Today.AddDays(-1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.");
        Assert.Null(repo.LastCloseOrgUnitId);
    }

    [Fact]
    public async Task Save_Close_PendingVersion_CallsDeclarationService_WithNullEffectiveThrough()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88); // future -> Pending
        await vm.LoadAsync(1, Today);
        declaration.CloseResult = new UpsertResult(0, [], []);
        repo.ByIdentityResultAfterClose = Dto(1, parentId: 5, Today.AddDays(-30), Today.AddDays(-1), id: 55, isActive: false);
        vm.BeginCloseCommand.Execute();
        vm.Reason = "hủy kế hoạch";

        await vm.SaveCommand.Execute();

        Assert.Equal(1, declaration.CloseCallCount);
        Assert.NotNull(declaration.LastRequest);
        Assert.Equal(88, declaration.LastRequest!.VersionId);
        Assert.Null(declaration.LastRequest.EffectiveThrough);
        Assert.Equal(0, repo.CancelPlanCallCount);
        Assert.Null(repo.LastCloseOrgUnitId);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Close_WhenNoLongerVisibleToday_ClearsTheCardAfterward()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        declaration.CloseResult = new UpsertResult(0, [], []);
        repo.ByIdentityResultAfterClose = Error.NotFound(); // closed effective as of TODAY too -> nothing left
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "đóng ngay hôm nay";

        await vm.SaveCommand.Execute();

        Assert.Equal(string.Empty, vm.OrgCode);
        Assert.Equal(VersionStatus.None, vm.Status);
    }

    // Phase 4d Task 3a: LoadTreeAsync/LoadAllHistoryAsync history capability
    // (View wiring is a separate follow-up task) -- these tests exercise the VM surface directly.

    [Fact]
    public async Task LoadTreeAsync_MapsEachUnitToANodeLabelledOrgCodeDashOrgNameShortVn()
    {
        var (vm, repo) = Build();
        repo.InScopeResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "HO0001", orgNameShortVn: "Tổng công ty")];

        await vm.LoadTreeAsync(Today);

        var root = Assert.Single(vm.TreeRoots);
        Assert.Equal(1, root.Id);
        Assert.Equal("HO0001 — Tổng công ty", root.Label);
    }

    [Fact]
    public async Task LoadTreeAsync_NestsAChildUnderItsParentByParentId()
    {
        var (vm, repo) = Build();
        repo.InScopeResult =
        [
            Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "HO0001", orgNameShortVn: "Tổng công ty"),
            Dto(2, parentId: 1, Today, EffectivePeriod.OpenEnd, orgCode: "CN001", orgNameShortVn: "Chi nhánh 1"),
        ];

        await vm.LoadTreeAsync(Today);

        var root = Assert.Single(vm.TreeRoots);
        var child = Assert.Single(root.Children);
        Assert.Equal(2, child.Id);
        Assert.Equal("CN001 — Chi nhánh 1", child.Label);
    }

    [Fact]
    public async Task LoadTreeAsync_UnitWithNoResolvableParentBecomesARoot()
    {
        // A unit whose ParentId does not resolve within the scoped result set (out-of-scope parent) must not
        // be dropped -- it becomes its own root rather than silently disappearing from the tree.
        var (vm, repo) = Build();
        repo.InScopeResult = [Dto(5, parentId: 999, Today, EffectivePeriod.OpenEnd, orgCode: "PGD002", orgNameShortVn: "Phòng giao dịch 2")];

        await vm.LoadTreeAsync(Today);

        Assert.Single(vm.TreeRoots);
    }

    [Fact]
    public async Task LoadTreeAsync_PassesTheGivenAsOfDateToTheRepository()
    {
        var (vm, repo) = Build();
        var asOf = Today.AddDays(-30);

        await vm.LoadTreeAsync(asOf);

        Assert.Equal(asOf, repo.LastInScopeAsOf);
    }

    [Fact]
    public async Task LoadTreeAsync_ReplacesThePreviousTreeRootsContents()
    {
        var (vm, repo) = Build();
        repo.InScopeResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "AAAA", orgNameShortVn: "First")];
        await vm.LoadTreeAsync(Today);
        Assert.Single(vm.TreeRoots);

        repo.InScopeResult = [];
        await vm.LoadTreeAsync(Today);

        Assert.Empty(vm.TreeRoots);
    }

    private static async Task SeedInScopeTreeAsync(OrgUnitDeclarationViewModel vm, FakeOrgUnitRepository repo, params OrgUnitVersionDto[] units)
    {
        repo.InScopeResult = units;
        await vm.LoadTreeAsync(Today);
    }

    // DFS that FAILS on revisit (cycle) rather than silently skipping — FR2 regression requirement.
    private static int CountTreeNodesExactlyOnce(IEnumerable<OrgUnitTreeNode> roots)
    {
        var visited = new HashSet<long>();
        void Walk(OrgUnitTreeNode n)
        {
            Assert.True(visited.Add(n.Id), $"Tree node {n.Id} revisited — residual cycle in Children.");
            foreach (var c in n.Children)
                Walk(c);
        }

        foreach (var r in roots)
            Walk(r);
        return visited.Count;
    }

    [Fact]
    public async Task LoadAllHistoryAsync_MapsOperationKindViaVersionOperationKindPresentation()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, operationKind: VersionOperationKind.Edit)];

        await vm.LoadAllHistoryAsync();

        var row = Assert.Single(vm.HistoryRows);
        Assert.Equal("Sửa", row.Operation);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_MapsParentLabelFromTheAsOfParentFields()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult =
        [
            Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd,
                parentOrgCodeAsOf: "HO0001", parentOrgNameFullVnAsOf: "Tổng công ty"),
        ];

        await vm.LoadAllHistoryAsync();

        var row = Assert.Single(vm.HistoryRows);
        Assert.Equal("HO0001 — Tổng công ty", row.ParentLabel);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_BothParentAsOfFieldsNull_ParentLabelIsEmpty()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd)];

        await vm.LoadAllHistoryAsync();

        var row = Assert.Single(vm.HistoryRows);
        Assert.Equal(string.Empty, row.ParentLabel);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_MapsIdentityAndAuditFieldsFromTheDto()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, orgCode: "CN001", orgNameFullVn: "Chi nhánh Thành phố", orgNameShortVn: "Chi nhánh 1")];

        await vm.LoadAllHistoryAsync();

        var row = Assert.Single(vm.HistoryRows);
        Assert.Equal(1, row.OrgUnitId);
        Assert.Equal("CN001", row.OrgCode);
        Assert.Equal("Chi nhánh Thành phố", row.NameFull);
        Assert.Equal("Chi nhánh 1", row.NameShort);
        Assert.Equal("tester", row.RecordedBy);
        Assert.Equal("seed", row.Reason);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_ReplacesThePreviousHistoryRowsContents()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.Single(vm.HistoryRows);

        repo.HistoryResult = [];
        await vm.LoadAllHistoryAsync();

        Assert.Empty(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadTreeAsync_RepositoryThrows_SetsErrorStatusWithoutEscaping()
    {
        var (vm, repo) = Build();
        repo.InScopeException = new InvalidOperationException("db down");

        await vm.LoadTreeAsync(Today);

        Assert.Equal("Không tải được cây đơn vị.", vm.StatusMessage);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    [Fact]
    public async Task LoadTreeAsync_DuplicateOrgUnitId_DoesNotThrowAndKeepsASingleNode()
    {
        var (vm, repo) = Build();
        repo.InScopeResult =
        [
            Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "HO0001", orgNameShortVn: "First"),
            Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "HO0001", orgNameShortVn: "Dup"),
        ];

        await vm.LoadTreeAsync(Today);

        Assert.Equal(1, CountTreeNodesExactlyOnce(vm.TreeRoots));
    }

    [Fact]
    public async Task LoadTreeAsync_ParentCycle_CutsCyclicEdgeSoForestHasNoRevisits()
    {
        var (vm, repo) = Build();
        repo.InScopeResult =
        [
            Dto(1, parentId: 2, Today, EffectivePeriod.OpenEnd, orgCode: "A", orgNameShortVn: "A"),
            Dto(2, parentId: 1, Today, EffectivePeriod.OpenEnd, orgCode: "B", orgNameShortVn: "B"),
        ];

        await vm.LoadTreeAsync(Today);

        Assert.Equal(2, CountTreeNodesExactlyOnce(vm.TreeRoots));
    }

    [Fact]
    public async Task LoadTreeAsync_AllNodesAreExpandedAfterLoad()
    {
        var (vm, repo) = Build();
        repo.InScopeResult =
        [
            Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "HO0001", orgNameShortVn: "Tổng công ty"),
            Dto(2, parentId: 1, Today, EffectivePeriod.OpenEnd, orgCode: "CN001", orgNameShortVn: "Chi nhánh 1"),
            Dto(3, parentId: 2, Today, EffectivePeriod.OpenEnd, orgCode: "PGD001", orgNameShortVn: "PGD 1"),
        ];

        await vm.LoadTreeAsync(Today);

        Assert.Equal(3, CountTreeNodesExactlyOnce(vm.TreeRoots));
        AssertAllNodesExpanded(vm.TreeRoots);
    }

    [Fact]
    public async Task LoadTreeAsync_ObsoleteGeneration_DoesNotMutateTreeRoots()
    {
        var (vm, repo) = Build();
        repo.InScopeResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "AAAA", orgNameShortVn: "First")];
        await vm.LoadTreeAsync(Today);

        var holdFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdCount = 0;
        repo.BeforeInScopeReturn = async () =>
        {
            if (Interlocked.Increment(ref holdCount) == 1)
            {
                firstEntered.TrySetResult();
                await holdFirst.Task;
            }
        };

        repo.InScopeResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "AAAA", orgNameShortVn: "Stale")];
        var staleLoad = vm.LoadTreeAsync(Today);
        await firstEntered.Task;

        repo.InScopeResult = [Dto(9, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "BBBB", orgNameShortVn: "Winner")];
        await vm.LoadTreeAsync(Today);
        var winnerRoots = vm.TreeRoots;
        Assert.Equal(9, Assert.Single(winnerRoots).Id);

        holdFirst.TrySetResult();
        await staleLoad;

        // Stale completion must not reassign TreeRoots after a newer generation won.
        Assert.Same(winnerRoots, vm.TreeRoots);
    }

    private static void AssertAllNodesExpanded(IEnumerable<OrgUnitTreeNode> roots)
    {
        foreach (var node in roots)
        {
            Assert.True(node.IsExpanded);
            AssertAllNodesExpanded(node.Children);
        }
    }

    [Fact]
    public async Task LoadAllHistoryAsync_CallsTheRepositoryAndPopulatesTheRows()
    {
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd, operationKind: VersionOperationKind.Add)];

        await vm.LoadAllHistoryAsync();

        Assert.Equal(1, repo.HistoryCallCount);
        Assert.Single(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_PassesNullOrgUnitIdToTheRepository()
    {
        // Brief 048: "Hiện tất cả" must load the real cross-unit history (GetHistoryInScopeAsync
        // already returns every unit's history when orgUnitId is null), not merely clear the grid.
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd)];

        await vm.LoadAllHistoryAsync();

        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.NotNull(repo.LastHistoryScope);
        Assert.Equal(ScopeLevel.Global, repo.LastHistoryScope!.Level);
        Assert.Single(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_RepositoryThrows_SetsErrorStatusWithoutEscaping()
    {
        var (vm, repo) = Build();
        repo.HistoryException = new InvalidOperationException("db down");

        await vm.LoadAllHistoryAsync();

        Assert.Equal("Không tải được lịch sử.", vm.StatusMessage);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
    }

    [Fact]
    public async Task RefreshHistoryAsync_AfterLoadAllHistoryAsync_ReloadsAllHistoryNotAnOrgUnit()
    {
        // Requester decision (2026-08-06): "Làm mới" refreshes whatever the History grid is
        // currently showing, independent of tree/node selection -- after "Hiện tất cả", refresh
        // must re-run the all-units query, not silently no-op or fall back to a stale node id.
        var (vm, repo) = Build();
        repo.HistoryResult = [Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        repo.HistoryResult = [
            Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd),
            Dto(2, parentId: null, Today.AddDays(-5), EffectivePeriod.OpenEnd),
        ];

        await vm.RefreshHistoryAsync();

        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.Equal(2, vm.HistoryRows.Count);
    }

    [Fact]
    public async Task MapHistoryRow_NullOperationKind_MapsEmptyOperation()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, operationKind: null)];

        await vm.LoadAllHistoryAsync();

        Assert.Equal(string.Empty, Assert.Single(vm.HistoryRows).Operation);
    }

    [Fact]
    public async Task MapHistoryRow_OpenEnd_FormatsToKhongXacDinh()
    {
        var (vm, repo) = Build();
        await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd));
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];

        await vm.LoadAllHistoryAsync();

        Assert.Equal("Không xác định", Assert.Single(vm.HistoryRows).ToText);
    }

    // Culture "/" in a custom date format is the date-separator placeholder, not a literal slash —
    // without CultureInfo.InvariantCulture it becomes CurrentCulture.DateTimeFormat.DateSeparator
    // (often "-"), so history shows dd-MM-yyyy. Force a dash separator for the duration of this test.
    [Fact]
    public async Task MapHistoryRow_DateTexts_UseLiteralSlash_EvenWhenCultureDateSeparatorIsDash()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("vi-VN").Clone();
            culture.DateTimeFormat.DateSeparator = "-";
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var (vm, repo) = Build();
            await SeedInScopeTreeAsync(vm, repo, Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd));
            var from = new DateOnly(2026, 7, 23);
            repo.HistoryResult = [Dto(1, parentId: null, from, EffectivePeriod.OpenEnd)];

            await vm.LoadAllHistoryAsync();

            var row = Assert.Single(vm.HistoryRows);
            Assert.Equal("23/07/2026", row.FromText);
            Assert.Equal("Không xác định", row.ToText);
            // RecordedAt uses UtcNow inside Dto — assert the date half uses literal '/' not '-'.
            var dateHalf = row.RecordedAtText.Split(' ')[0];
            Assert.Matches(@"^\d{2}/\d{2}/\d{4}$", dateHalf);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_Success_RefreshesTreeAndHistory()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.True(repo.InScopeCallCount >= 1);
        // Brief 049: History is decoupled from tree/unit selection -- post-save refresh reloads
        // the FULL history dataset (null id), not just the just-written unit.
        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.True(repo.HistoryCallCount >= 1);
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_Success_RefreshesTreeAndHistory()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        repo.InScopeResult = [Dto(3, parentId: 1, Today, EffectivePeriod.OpenEnd, orgCode: "CN001")];
        await vm.LoadTreeAsync(Today);
        vm.BeginEditCommand.Execute();
        vm.OrgNameShortVn = "Chi nhanh";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.True(repo.HistoryCallCount >= 1);
    }

    // Backlog 0.8: `CanClose` used to carry a bare `&& !IsRoot`, which disables the ONE button that reaches
    // the service -- and the service derives close-vs-cancel server-side, so it blocked the cancel path too.
    // With the service-side carve-out in place, a bare `!IsRoot` here would leave the rescuer's remedy
    // UNREACHABLE and the requester's third ruling unimplemented. That is why this VM half is required, not
    // cosmetic.
    [Fact]
    public async Task CanClose_ForARootUnit_IsTrueOnlyForABreakGlassActor()
    {
        var ordinary = Build(breakGlass: new FakeBreakGlassPolicy());
        ordinary.Repo.ByIdentityResult = Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 88);
        await ordinary.Vm.LoadAsync(1, Today);
        ordinary.Vm.IsRoot.Should().BeTrue("the fixture must actually load a ROOT unit for this test to mean anything");
        ordinary.Vm.CanClose.Should().BeFalse();

        var rescuer = Build(breakGlass: new FakeBreakGlassPolicy("tester"));
        rescuer.Repo.ByIdentityResult = Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 88);
        await rescuer.Vm.LoadAsync(1, Today);
        rescuer.Vm.CanClose.Should().BeTrue("break-glass performs a root's close under the unit's normal rules");
    }

    [Fact]
    public async Task CanClose_ForANonRootUnit_IsTrueWithoutBreakGlass()
    {
        // THE DISCRIMINATOR: proves the new condition keys on root-ness, not on break-glass membership --
        // an implementation that simply required break-glass for everything would pass the test above.
        var (vm, repo) = Build(breakGlass: new FakeBreakGlassPolicy());
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 88);
        await vm.LoadAsync(1, Today);

        vm.IsRoot.Should().BeFalse();
        vm.CanClose.Should().BeTrue();
    }

    // Backlog 0.7: the screen delegates the Edit write instead of performing it. What this pins that the
    // existing Edit tests cannot: the ECHO the VM sends -- the version and parent it actually READ -- which
    // is what the service verifies under the lock. A VM that sent its own editable ParentId would still
    // write the right row here and be wrong for the one reason this slice exists.
    [Fact]
    public async Task ExecuteSaveEditAsync_DelegatesToTheDeclarationService_EchoingTheLoadedVersionAndParent()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgNameShortVn = "Chi nhanh";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        declaration.EditCallCount.Should().Be(1);
        declaration.LastEditRequest.Should().NotBeNull();
        declaration.LastEditRequest!.OrgUnitId.Should().Be(3);
        declaration.LastEditRequest.ExpectedVersionId.Should().Be(30);
        declaration.LastEditRequest.ExpectedParentId.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_Success_RefreshesTreeAndHistory()
    {
        var (vm, repo, _) = BuildForSave();
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        repo.InScopeResult = [Dto(4, parentId: 1, Today, EffectivePeriod.OpenEnd, orgCode: "CN004")];
        await vm.LoadTreeAsync(Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.True(repo.HistoryCallCount >= 1);
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_Success_RefreshHistoryAsync_AfterwardStillReloadsFullHistory()
    {
        // Brief 049/053: RefreshHistoryAsync always reloads the full dataset (History is fully
        // decoupled from tree/unit); a Refresh-button click right after a save must still pass
        // null orgUnitId to the repository.
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        await vm.SaveCommand.Execute();

        await vm.RefreshHistoryAsync();

        Assert.Null(repo.LastHistoryOrgUnitId);
    }

    [Fact]
    public async Task ExecuteCancel_AfterEdit_DoesNotCallTreeOrHistoryReload()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(5, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 50, orgCode: "CN005");
        await vm.LoadAsync(5, Today);
        repo.InScopeResult = [Dto(5, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "CN005")];
        await vm.LoadTreeAsync(Today);
        var inScopeCallsBefore = repo.InScopeCallCount;
        var historyCallsBefore = repo.HistoryCallCount;
        vm.BeginEditCommand.Execute();
        vm.OrgNameShortVn = "temp name";

        await vm.CancelCommand.Execute();

        Assert.Equal(inScopeCallsBefore, repo.InScopeCallCount);
        Assert.Equal(historyCallsBefore, repo.HistoryCallCount);
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_PostSaveHistoryReloadThrows_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        // RefreshTreeAndHistoryAsync's finally block used to unconditionally restore the preserved
        // "Đã lưu."/Success banner even when its own try block had just thrown -- silently hiding a
        // stale-tree/stale-history state behind an apparently-successful save. AST.Shell has no Serilog
        // reference (Scope forbids adding one), so the loud-failure signal has to be the status banner.
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        repo.HistoryException = new InvalidOperationException("boom");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Warning, vm.Severity);
        Assert.NotEqual("Đã lưu.", vm.StatusMessage);
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_PostSaveScopeResolutionSoftFails_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        // RefreshTreeAndHistoryAsync only sets refreshFailed on a THROWN exception. A non-throwing
        // ResolveScopeAsync failure (ErrorOr.IsError, e.g. an as-of/scope mismatch dropping the caller's
        // grant between the save's own write-scope check and the post-save refresh) skips the tree reload
        // but falls through to the `finally` block's success branch, so a stale tree is silently hidden
        // behind "Đã lưu." -- the still-open counterpart of the thrown-exception case fixed above
        // (2026-08-05 FR1).
        var (vm, repo, auth) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        // Since 2026-08-17 the Add branch does NOT call ResolveScopeAsync before the write — P7 runs inside
        // IOrgUnitDeclarationService — so RefreshTreeAndHistoryAsync's own call is now the FIRST one this
        // flow makes, not the second. The scenario is unchanged: it is the POST-save resolution that
        // soft-fails.
        auth.AuthorizeResultFactory = _ => Error.Forbidden("Authz.NotGranted", "not granted");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Warning, vm.Severity);
        Assert.NotEqual("Đã lưu.", vm.StatusMessage);
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_WhenSavedIdAbsentFromInScope_PreservesSuccessBannerAndStillLoadsHistory()
    {
        var (vm, repo, _) = BuildForSave();
        repo.AutoAddWrittenIdentityToInScope = false;
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        repo.HistoryResult = [Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD", operationKind: VersionOperationKind.Add)];
        repo.InScopeResult = [];
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vm.Severity);
        Assert.Equal("Đã lưu.", vm.StatusMessage);
        Assert.Null(repo.LastHistoryOrgUnitId);
        Assert.True(repo.HistoryCallCount >= 1);
        Assert.Single(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadTreeAsync_AuthorizeReturnsError_SetsErrorAndDoesNotCallGetInScope()
    {
        var (vm, repo, auth) = BuildForSave();
        auth.AuthorizeResult = Error.Forbidden(description: "no grant");
        var callsBefore = repo.InScopeCallCount;

        await vm.LoadTreeAsync(Today);

        Assert.Equal(callsBefore, repo.InScopeCallCount);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal("Không tải được cây đơn vị.", vm.StatusMessage);
    }

    [Fact]
    public async Task LoadTreeAsync_UsesAuthorizedDataScopeNotHardcodedGlobal()
    {
        var (vm, repo, auth) = BuildForSave();
        auth.AuthorizeResult = new DataScope(ScopeLevel.OwnOrgUnit, 7, "tester");
        repo.InScopeResult = [];

        await vm.LoadTreeAsync(Today);

        Assert.NotNull(repo.LastInScopeScope);
        Assert.Equal(ScopeLevel.OwnOrgUnit, repo.LastInScopeScope!.Level);
        Assert.Equal(7, repo.LastInScopeScope.RootOrgUnitId);
    }

    // ---- Scope-checked writes (2026-08-05 security fix, part 2) ----------------------------------
    // A user holding the Screen A function grant at ANY scope level (e.g. OwnOrgUnit) must not be able
    // to edit/close/add an org unit outside that scope, even though AuthorizeAsync/ResolveScopeAsync
    // succeeds (it only proves they hold the FUNCTION grant, not that the TARGET unit is in scope).

    // REWRITTEN 2026-08-21 (backlog 0.7). This used to seed repo.WithinScopeResult = false and prove the
    // ViewModel's OWN gate fired. That gate is gone -- it moved into IOrgUnitDeclarationService, where a
    // caller that is not this screen cannot skip it, and where real MySQL covers it
    // (OrgUnitDeclarationServiceTests.EditOrgUnitDeclarationAsync_TargetOutsideScope_...).
    //
    // What is still THIS layer's job, and is what the test now asserts: the screen surfaces the service's
    // refusal in Vietnamese and writes nothing. Seeding the repository flag here would prove nothing --
    // the VM no longer reads it on this path.
    [Fact]
    public async Task Save_Edit_ServiceRefusesOutOfScopeTarget_ShowsVnMessageAndWritesNothing()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            EditError = Error.Forbidden("OrgUnit.NotInScope", "Org unit 1 is not within actor's authorized scope."),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "ABCX";
        vm.Reason = "sửa";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be(
            "Bạn không có quyền sửa/đóng đơn vị này (ngoài phạm vi quản lý của bạn).");
        repo.LastUpsertOrgUnitId.Should().BeNull("nothing may reach the writer");
        declaration.EditCallCount.Should().Be(
            1, "the refusal must come from the service, not from a gate this screen kept");
    }

    [Fact]
    public async Task Save_Close_MapsOrgUnitNotInScope_ToExistingVnMessage()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Forbidden("OrgUnit.NotInScope", "out of scope"),
        };
        var (vm, repo, _) = BuildForSave(declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77); // Effective
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(5);
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        Assert.Equal(1, declaration.CloseCallCount);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(
            "Bạn không có quyền sửa/đóng đơn vị này (ngoài phạm vi quản lý của bạn).",
            vm.StatusMessage);
        Assert.Null(repo.LastCloseOrgUnitId);
        Assert.Equal(0, repo.CancelPlanCallCount);
    }

    [Fact]
    public async Task Save_Close_PendingVersion_MapsOrgUnitNotInScope_AndNeverCallsRepoCancelPlan()
    {
        // Pending still routes through the SAME declaration service call (null EffectiveThrough) —
        // NotInScope must surface as VN regardless of Status display branch.
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Forbidden("OrgUnit.NotInScope", "out of scope"),
        };
        var (vm, repo, _) = BuildForSave(declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88); // future -> Pending
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.Reason = "hủy kế hoạch";

        await vm.SaveCommand.Execute();

        Assert.Equal(1, declaration.CloseCallCount);
        Assert.Null(declaration.LastRequest!.EffectiveThrough);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(
            "Bạn không có quyền sửa/đóng đơn vị này (ngoài phạm vi quản lý của bạn).",
            vm.StatusMessage);
        Assert.Equal(0, repo.CancelPlanCallCount);
        Assert.Null(repo.LastCancelOrgUnitId);
    }

    // The Global-scope gate itself moved into IOrgUnitDeclarationService (2026-08-17, backlog 0.4b) and is
    // proven there on real MySQL. What stays this screen's job is the WORDING: the service reports a code,
    // and the operator must read a Vietnamese sentence, never the English Description.
    [Theory]
    [InlineData(null)] // root attempt
    [InlineData(9L)]   // child attempt -- the requirement applies to both
    public async Task Save_Add_ServiceDeniesForScope_ShowsTheVietnameseSentence_AndWritesNothing(long? parentId)
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            AddError = Error.Forbidden(
                "OrgUnit.AddRequiresGlobalScope", "Creating an org unit requires Global scope; actor 'tester' holds OwnOrgUnit."),
        };
        var (vm, repo, _) = BuildForSave(declaration);
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);
        vm.ParentId = parentId;

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be("Bạn không có quyền tạo đơn vị mới (yêu cầu quyền toàn hệ thống).");
        repo.CreateIdentityCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Save_GlobalScope_AddEditClose_AllStillSucceed_NoRegressionOnTheHappyPath()
    {
        // Global-scope user: Add, Edit, and Close must all still work exactly as before this fix --
        // if this breaks, the guard logic (not the test) is wrong.
        var (vmAdd, repoAdd, _) = BuildForSave();
        repoAdd.CreateIdentityResult = 42;
        repoAdd.UpsertResult = new UpsertResult(1, [], []);
        repoAdd.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vmAdd.BeginAddCommand.Execute();
        FillValidAddForm(vmAdd);
        vmAdd.ParentId = 9;
        await vmAdd.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vmAdd.Severity);
        Assert.Equal(1, repoAdd.CreateIdentityCallCount);

        var (vmEdit, repoEdit, _) = BuildForEdit();
        repoEdit.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmEdit.LoadAsync(1, Today);
        repoEdit.PreviewResult = [];
        repoEdit.UpsertResult = new UpsertResult(2, [], []);
        vmEdit.BeginEditCommand.Execute();
        vmEdit.OrgCode = "ABCX";
        vmEdit.Reason = "sửa";
        await vmEdit.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vmEdit.Severity);
        Assert.Equal(1, repoEdit.LastUpsertOrgUnitId);

        var closeDecl = new FakeOrgUnitDeclarationService();
        var (vmClose, repoClose, _) = BuildForEdit(declaration: closeDecl);
        repoClose.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmClose.LoadAsync(1, Today);
        closeDecl.CloseResult = new UpsertResult(0, [], []);
        repoClose.ByIdentityResultAfterClose = Dto(1, parentId: 5, Today.AddDays(-10), Today.AddDays(5), id: 77);
        vmClose.BeginCloseCommand.Execute();
        vmClose.EffectiveTo = Today.AddDays(5);
        vmClose.IsUndetermined = false;
        vmClose.Reason = "đóng đơn vị";
        await vmClose.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vmClose.Severity);
        Assert.Equal(1, closeDecl.CloseCallCount);
        Assert.Equal(Today.AddDays(5), closeDecl.LastRequest!.EffectiveThrough);
    }

    [Fact]
    public async Task Save_EditAndClose_InScopeNonGlobalUser_Succeeds_ProvesThisIsAScopeCheckNotABlanketGlobalRequirement()
    {
        var (vmEdit, repoEdit, auth) = BuildForSave();
        repoEdit.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmEdit.LoadAsync(1, Today);
        auth.AuthorizeResult = new DataScope(ScopeLevel.OwnOrgUnit, 9, "tester");
        repoEdit.WithinScopeResult = true; // unit 1 IS within root 9's scope
        repoEdit.PreviewResult = [];
        repoEdit.UpsertResult = new UpsertResult(2, [], []);
        vmEdit.BeginEditCommand.Execute();
        vmEdit.OrgCode = "ABCX";
        vmEdit.Reason = "sửa";

        await vmEdit.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vmEdit.Severity);
        Assert.Equal(1, repoEdit.LastUpsertOrgUnitId);

        var closeDecl = new FakeOrgUnitDeclarationService();
        var (vmClose, repoClose, authClose) = BuildForSave(closeDecl);
        repoClose.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmClose.LoadAsync(1, Today);
        authClose.AuthorizeResult = new DataScope(ScopeLevel.OwnOrgUnit, 9, "tester");
        repoClose.WithinScopeResult = true;
        closeDecl.CloseResult = new UpsertResult(0, [], []);
        repoClose.ByIdentityResultAfterClose = Dto(1, parentId: 5, Today.AddDays(-10), Today.AddDays(5), id: 77);
        vmClose.BeginCloseCommand.Execute();
        vmClose.EffectiveTo = Today.AddDays(5);
        vmClose.IsUndetermined = false;
        vmClose.Reason = "close unit";

        await vmClose.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vmClose.Severity);
        Assert.Equal(1, closeDecl.CloseCallCount);
        Assert.Equal(Today.AddDays(5), closeDecl.LastRequest!.EffectiveThrough);
        Assert.Null(repoClose.LastCloseOrgUnitId);
    }

    [Fact]
    public async Task ReadCallSites_StayHardcodedGlobal_RegressionGuardAgainstAWellMeaningButWrongCleanup()
    {
        // DELIBERATE regression guard: the 2 read call sites (parent-picker candidates, history load) are
        // NOT part of this scope-checked-write fix and must keep returning system-wide results for a
        // non-Global user -- someone will eventually be tempted to "finish the job" by threading the
        // resolved scope into these reads too; that is a deliberate, separately-decided change
        // (2026-08-05), not an oversight to silently fix here.
        //
        // It guarded THREE call sites until 2026-08-17: the N1 root-existence probe was the third. That
        // probe no longer exists in this ViewModel (backlog 0.4b) -- it runs inside
        // OrgUnitDeclarationService's own transaction and takes no DataScope at all, so "hardcoded Global"
        // stopped being a meaningful description of it. Its leg was removed rather than left: it kept
        // passing, but on RefreshTreeAndHistoryAsync's post-save tree reload, which resolves its scope from
        // AuthorizeAsync -- the exact opposite of the property the leg claimed to pin. Server-side coverage
        // is AST.Modules.IAM.Tests/Integration/OrgUnitDeclarationServiceTests A2-A4.
        var (vm, repo, auth) = BuildForSave();
        auth.AuthorizeResult = new DataScope(ScopeLevel.OwnOrgUnit, 9, "tester");

        // History read: still uses a hardcoded Global scope regardless of the caller's own (narrower) scope.
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.LoadAllHistoryAsync();
        Assert.NotNull(repo.LastHistoryScope);
        Assert.Equal(ScopeLevel.Global, repo.LastHistoryScope!.Level);

        // Parent-picker read: same -- reached via BeginAdd + typing a full EP.
        repo.EligibleParentsResult = [new OrgUnitPickerItem(9, "PAR — Cha")];
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;
        Assert.Single(vm.ParentCandidates);
    }

    // --- Brief 051/053 Fix Round 1: History generation-guard for the sole LoadAll path ---

    [Fact]
    public async Task LoadAllHistoryAsync_StaleFullLoad_DoesNotOverwriteANewerLoadAllHistoryAsync()
    {
        var (vm, repo) = Build();
        var holdFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var returnOrder = 0;
        // Hold the first GetHistoryInScopeAsync call at the async gate, then let the second call
        // complete first. HistoryResultFactory keys off return order (orgUnitId is always null on
        // the sole LoadAll path) so the late-completing call still yields "STALE" — mutating a
        // shared HistoryResult field would make both returns identical and the test tautological.
        repo.BeforeHistoryReturn = async _ =>
        {
            if (Interlocked.Increment(ref enteredCount) == 1)
            {
                firstEntered.TrySetResult();
                await holdFirst.Task;
            }
        };
        repo.HistoryResultFactory = _ =>
        {
            var order = Interlocked.Increment(ref returnOrder);
            return order == 1
                ? [Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "NEWER")]
                : [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "STALE")];
        };

        var staleLoad = vm.LoadAllHistoryAsync();
        await firstEntered.Task;

        await vm.LoadAllHistoryAsync();
        Assert.Equal("NEWER", Assert.Single(vm.HistoryRows).OrgCode);

        holdFirst.TrySetResult();
        await staleLoad;

        Assert.Equal("NEWER", Assert.Single(vm.HistoryRows).OrgCode);
    }

    [Fact]
    public async Task ClearHistory_WhileLoadAllHistoryAsyncIsInFlight_DoesNotRepopulateHistoryRows()
    {
        var (vm, repo) = Build();
        var heldAll = new TaskCompletionSource();
        repo.BeforeHistoryReturn = async id =>
        {
            if (id is null)
                await heldAll.Task;
        };
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "STALE")];

        var inFlight = vm.LoadAllHistoryAsync();
        vm.ClearHistory();
        Assert.Empty(vm.HistoryRows);

        heldAll.SetResult();
        await inFlight;

        Assert.Empty(vm.HistoryRows);
    }

    [Fact]
    public async Task LoadAllHistoryAsync_FailedLoad_RefreshHistoryAsyncRetriesFullHistory()
    {
        var (vm, repo) = Build();
        repo.HistoryException = new InvalidOperationException("db down");

        await vm.LoadAllHistoryAsync();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(1, repo.HistoryCallCount);

        repo.HistoryException = null;
        repo.HistoryResult = [Dto(1, parentId: null, Today, EffectivePeriod.OpenEnd)];
        await vm.RefreshHistoryAsync();

        Assert.Equal(2, repo.HistoryCallCount);
        Assert.Null(repo.LastHistoryOrgUnitId);
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_Success_RefreshHistoryAsync_AfterwardStillReloadsFullHistory()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgNameShortVn = "Chi nhanh";
        vm.Reason = "doi ten";
        await vm.SaveCommand.Execute();

        await vm.RefreshHistoryAsync();

        Assert.Null(repo.LastHistoryOrgUnitId);
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_Success_RefreshHistoryAsync_AfterwardStillReloadsFullHistory()
    {
        var (vm, repo, _) = BuildForSave();
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";
        await vm.SaveCommand.Execute();

        await vm.RefreshHistoryAsync();

        Assert.Null(repo.LastHistoryOrgUnitId);
    }

    // --- Brief 061: close rewire onto IOrgUnitDeclarationService ---

    [Fact]
    public async Task Save_Close_MapsOrgUnitVersionNotFound_ToVnMessage()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.NotFound("OrgUnit.VersionNotFound", "missing"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal("Không tìm thấy phiên bản đơn vị cho thao tác này.", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_MapsTemporalFkDependentsUncovered_ToVnMessage()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Conflict("TemporalFk.DependentsUncovered", "dependents"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(
            "Không thể đóng đơn vị — vẫn còn đơn vị con hoặc người dùng thuộc đơn vị này. Hãy xử lý các phụ thuộc trước khi đóng.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_BranchHandoff_PendingSendsNull_EffectiveSendsTypedDate()
    {
        // Discriminating fixture: FAILS if the VM resumes deciding Cancel vs Close via repo methods,
        // or if it always sends the typed date / always sends null regardless of Status.
        var pendingDecl = new FakeOrgUnitDeclarationService();
        var (vmPending, repoPending, _) = BuildForEdit(declaration: pendingDecl);
        repoPending.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88);
        await vmPending.LoadAsync(1, Today);
        vmPending.BeginCloseCommand.Execute();
        vmPending.EffectiveTo = Today.AddDays(9); // typed but must NOT be forwarded for Pending
        vmPending.IsUndetermined = false;
        await vmPending.SaveCommand.Execute();
        Assert.Equal(1, pendingDecl.CloseCallCount);
        Assert.Null(pendingDecl.LastRequest!.EffectiveThrough);
        Assert.Equal(0, repoPending.CancelPlanCallCount);
        Assert.Null(repoPending.LastCloseOrgUnitId);

        var effectiveDecl = new FakeOrgUnitDeclarationService();
        var (vmEffective, repoEffective, _) = BuildForEdit(declaration: effectiveDecl);
        repoEffective.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmEffective.LoadAsync(1, Today);
        vmEffective.BeginCloseCommand.Execute();
        vmEffective.EffectiveTo = Today.AddDays(5);
        vmEffective.IsUndetermined = false;
        await vmEffective.SaveCommand.Execute();
        Assert.Equal(1, effectiveDecl.CloseCallCount);
        Assert.Equal(Today.AddDays(5), effectiveDecl.LastRequest!.EffectiveThrough);
        Assert.Equal(0, repoEffective.CancelPlanCallCount);
        Assert.Null(repoEffective.LastCloseOrgUnitId);
    }

    [Fact]
    public async Task Save_BlankNote_IsAccepted_OnCloseAddAndEdit()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vmClose, repoClose, _) = BuildForEdit(declaration: declaration);
        repoClose.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmClose.LoadAsync(1, Today);
        vmClose.BeginCloseCommand.Execute();
        vmClose.EffectiveTo = Today;
        vmClose.IsUndetermined = false;
        vmClose.Reason = "";

        await vmClose.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Success, vmClose.Severity);
        Assert.Equal(1, declaration.CloseCallCount);
        Assert.Null(declaration.LastRequest!.Note);

        var (vmAdd, repoAdd, _) = BuildForSave();
        repoAdd.CreateIdentityResult = 42;
        repoAdd.UpsertResult = new UpsertResult(1, [], []);
        repoAdd.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
        vmAdd.BeginAddCommand.Execute();
        FillValidAddForm(vmAdd);
        vmAdd.ParentId = 9; // non-root Add — the fake declaration service does not model root uniqueness
        vmAdd.Reason = "";
        await vmAdd.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vmAdd.Severity);

        var (vmEdit, repoEdit, _) = BuildForEdit();
        repoEdit.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmEdit.LoadAsync(1, Today);
        repoEdit.PreviewResult = [];
        repoEdit.UpsertResult = new UpsertResult(2, [], []);
        vmEdit.BeginEditCommand.Execute();
        vmEdit.OrgCode = "ABCX"; // seed Dto orgCode "ABC" is below §2.2 length — other Edit tests do the same
        vmEdit.Reason = "";
        await vmEdit.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Success, vmEdit.Severity);
        Assert.Equal(1, repoEdit.LastUpsertOrgUnitId);
    }

    [Fact]
    public async Task CloseDateHint_OnStatusBanner_EffectiveWithDate_PendingNeverShows()
    {
        var (vmEffective, repoEffective) = Build();
        repoEffective.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vmEffective.LoadAsync(1, Today);
        vmEffective.BeginCloseCommand.Execute();
        Assert.Equal(StatusSeverity.None, vmEffective.Severity);

        vmEffective.EffectiveTo = new DateOnly(2026, 8, 10);
        Assert.Equal(StatusSeverity.Info, vmEffective.Severity);
        Assert.Equal(
            "Mã đơn vị ABC còn hiệu lực đến ngày 10/08/2026, chấm dứt hiệu lực từ ngày 11/08/2026.",
            vmEffective.StatusMessage);

        var (vmPending, repoPending) = Build();
        repoPending.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88);
        await vmPending.LoadAsync(1, Today);
        vmPending.BeginCloseCommand.Execute();
        vmPending.EffectiveTo = new DateOnly(2026, 8, 10);
        Assert.NotEqual(StatusSeverity.Info, vmPending.Severity);
        Assert.DoesNotContain("chấm dứt hiệu lực", vmPending.StatusMessage ?? string.Empty);
    }

    // --- Brief 061 Fix Round 1 ---

    public static TheoryData<string, string> CloseErrorCodeToVnRows() => new()
    {
        { VersionCloseRules.Codes.CloseDateRequired, "Cần nhập ngày kết thúc để đóng đơn vị." },
        { VersionCloseRules.Codes.CloseDateInPast, $"Ngày kết thúc phải từ ngày {Today.AddDays(-1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}." },
        { VersionCloseRules.Codes.CloseDateEqualsVersionEnd, "Ngày kết thúc trùng ngày hết hiệu lực hiện tại — chọn ngày sớm hơn." },
        { VersionCloseRules.Codes.CloseDateOutsideVersionPeriod, "Ngày kết thúc phải nằm trong kỳ hiệu lực của phiên bản đang đóng." },
        { VersionCloseRules.Codes.VersionAlreadyEnded, "Phiên bản này đã hết hiệu lực — không thể đóng lại. Chọn phiên bản còn hiệu lực." },
        { VersionCloseRules.Codes.CloseDateNotApplicableToCancelPlan, "Hủy hiệu lực không dùng ngày kết thúc — để trống ngày Đến." },
        { "OrgUnit.NotInScope", "Bạn không có quyền sửa/đóng đơn vị này (ngoài phạm vi quản lý của bạn)." },
        { "OrgUnit.VersionNotFound", "Không tìm thấy phiên bản đơn vị cho thao tác này." },
        { "VersionedRepository.VersionNotFound", "Không tìm thấy phiên bản đơn vị cho thao tác này." },
        { "VersionedRepository.NotAFuturePlan", "Dữ liệu đã thay đổi — vui lòng tải lại." },
        { "VersionedRepository.LockTimeout", "Hệ thống đang bận, vui lòng thử lại." },
        { "VersionedRepository.InvalidShrink", "Ngày kết thúc không hợp lệ với kỳ hiệu lực hiện tại — chọn ngày khác." },
        { "TemporalFk.DependentsUncovered", "Không thể đóng đơn vị — vẫn còn đơn vị con hoặc người dùng thuộc đơn vị này. Hãy xử lý các phụ thuộc trước khi đóng." },
        { "Authz.ScopeInsufficient", "Bạn không có đủ phạm vi quyền cho thao tác này trên đơn vị này." },
    };

    [Theory]
    [MemberData(nameof(CloseErrorCodeToVnRows))]
    public async Task Save_Close_MapsErrorCode_ToExactVietnamese_IgnoringSeedDescription(string code, string expectedVn)
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Validation(code, "SEED-DESCRIPTION-MUST-NOT-SURFACE"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;

        await vm.SaveCommand.Execute();

        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal(expectedVn, vm.StatusMessage);
        Assert.DoesNotContain("SEED-DESCRIPTION-MUST-NOT-SURFACE", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_AuthzNotGranted_KeepsServiceVietnameseDescription()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Forbidden("Authz.NotGranted", "Không được cấp quyền cho chức năng này."),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;

        await vm.SaveCommand.Execute();

        Assert.Equal("Không được cấp quyền cho chức năng này.", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_Pending_AbortConfirmation_WritesNothing_AndLeavesFormUntouched()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var confirm = new FakeConfirmationPrompt(confirm: false);
        var repo = new FakeOrgUnitRepository();
        var vm = new OrgUnitDeclarationViewModel(
            repo, declaration, new FixedDates(Today), new FakeCurrentUser("tester"),
            new FakeAuthorizationService(), confirm, new FakeBreakGlassPolicy());
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();

        Assert.False(vm.IsEffectivePeriodEnabled);
        Assert.Equal(OrgUnitCardMode.Closing, vm.Mode);
        Assert.Equal(VersionStatus.Pending, vm.Status);
        var dirtyBefore = vm.IsDirty;
        var toBefore = vm.EffectiveTo;
        var severityBefore = vm.Severity;
        var messageBefore = vm.StatusMessage;

        await vm.SaveCommand.Execute();

        Assert.True(confirm.WasCalled);
        Assert.Equal("Đóng mã đơn vị sẽ hủy kỳ hiệu lực của đơn vị", confirm.LastMessage);
        Assert.Equal(0, declaration.CloseCallCount);
        Assert.Null(declaration.LastRequest);
        Assert.Equal(OrgUnitCardMode.Closing, vm.Mode);
        Assert.Equal(VersionStatus.Pending, vm.Status);
        Assert.Equal(dirtyBefore, vm.IsDirty);
        Assert.Equal(toBefore, vm.EffectiveTo);
        Assert.Equal(severityBefore, vm.Severity);
        Assert.Equal(messageBefore, vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_Pending_Confirm_CallsServiceWithNullEffectiveThrough()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var confirm = new FakeConfirmationPrompt(confirm: true);
        var repo = new FakeOrgUnitRepository();
        var vm = new OrgUnitDeclarationViewModel(
            repo, declaration, new FixedDates(Today), new FakeCurrentUser("tester"),
            new FakeAuthorizationService(), confirm, new FakeBreakGlassPolicy());
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(1), EffectivePeriod.OpenEnd, id: 88);
        await vm.LoadAsync(1, Today);
        repo.ByIdentityResultAfterClose = Error.NotFound();
        vm.BeginCloseCommand.Execute();

        await vm.SaveCommand.Execute();

        Assert.True(confirm.WasCalled);
        Assert.Equal(1, declaration.CloseCallCount);
        Assert.Null(declaration.LastRequest!.EffectiveThrough);
        Assert.Equal(StatusSeverity.Success, vm.Severity);
    }

    [Fact]
    public async Task Save_Close_OpenEndLiteral_BlocksWithClearMessage_NeverCallsService()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.IsUndetermined = false;
        vm.EffectiveTo = EffectivePeriod.OpenEnd;

        await vm.SaveCommand.Execute();

        Assert.Equal(0, declaration.CloseCallCount);
        Assert.Equal(StatusSeverity.Error, vm.Severity);
        Assert.Equal("Ngày kết thúc không được là ngày không xác định.", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_Close_AfterFailedSave_RetypingDate_ClearsStaleError_AndShowsHint()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Validation(VersionCloseRules.Codes.CloseDateInPast, "past"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(-1);
        await vm.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Error, vm.Severity);

        declaration.CloseResult = new UpsertResult(0, [], []);
        vm.EffectiveTo = new DateOnly(2026, 8, 10);

        Assert.Equal(StatusSeverity.Info, vm.Severity);
        Assert.Contains("còn hiệu lực đến ngày", vm.StatusMessage!, StringComparison.Ordinal);
    }

    // Round-trip Cancel from Closing — does not assert RestoreSnapshot assignment order (either order
    // restores the same state under AstEffectivePeriod's IsUndetermined⇒To==null invariant).
    [Fact]
    public async Task CancelCommand_FromClosing_RestoresEffectiveToAndIsUndetermined_AndClearsDirty()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        Assert.True(vm.IsUndetermined);
        Assert.Null(vm.EffectiveTo);

        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(3);
        vm.IsUndetermined = false;
        Assert.True(vm.IsDirty);

        await vm.CancelCommand.Execute();

        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
        Assert.False(vm.IsDirty);
        Assert.True(vm.IsUndetermined);
        Assert.Null(vm.EffectiveTo);
    }

    [Fact]
    public async Task CancelCommand_FromClosing_ClearsFailedCloseErrorBanner()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Validation(VersionCloseRules.Codes.CloseDateInPast, "past"),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today.AddDays(-1);
        await vm.SaveCommand.Execute();
        Assert.Equal(StatusSeverity.Error, vm.Severity);

        await vm.CancelCommand.Execute();

        Assert.Equal(OrgUnitCardMode.ReadOnly, vm.Mode);
        Assert.Equal(StatusSeverity.None, vm.Severity);
        Assert.True(string.IsNullOrEmpty(vm.StatusMessage));
    }

    // Hardening (2026-08-10): a blank date on the retire branch must never reach the service — the VM
    // fails clear itself instead, so a branch disagreement between VM and server (e.g. a concurrent edit)
    // can never let a null date land on a server that has since switched to CancelPlan and execute an
    // unconfirmed cancel.
    [Fact]
    public async Task Save_Close_RetireBranch_BlankDate_FailsInVm_NeverCallsService()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, confirm) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        // Leave EffectiveTo blank (default post-BeginClose state) — the retire branch, not cancel-plan,
        // since EffectiveFrom is well before today.

        await vm.SaveCommand.Execute();

        declaration.CloseCallCount.Should().Be(0);
        confirm.WasCalled.Should().BeFalse();
        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().Be("Cần nhập ngày kết thúc để đóng đơn vị.");
    }

    [Fact]
    public async Task Save_Close_AuthzEmptyDescription_UsesGenericFallback()
    {
        var declaration = new FakeOrgUnitDeclarationService
        {
            CloseResult = Error.Forbidden("Authz.NotGranted", "   "),
        };
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;

        await vm.SaveCommand.Execute();

        Assert.Equal("Bạn không có quyền thực hiện thao tác này.", vm.StatusMessage);
    }

    [Theory]
    [InlineData(OrgUnitCardMode.ReadOnly, VersionStatus.Effective, false)]
    [InlineData(OrgUnitCardMode.Adding, VersionStatus.None, true)]
    [InlineData(OrgUnitCardMode.Editing, VersionStatus.Effective, true)]
    [InlineData(OrgUnitCardMode.Closing, VersionStatus.Effective, true)]
    [InlineData(OrgUnitCardMode.Closing, VersionStatus.Pending, false)]
    public async Task IsEffectivePeriodEnabled_MatchesModeAndCloseBranch(
        OrgUnitCardMode mode, VersionStatus status, bool expectedEnabled)
    {
        var (vm, repo) = Build();
        if (mode == OrgUnitCardMode.Adding)
        {
            repo.ByIdentityResult = Dto(42, parentId: 9, Today, EffectivePeriod.OpenEnd);
            vm.BeginAddCommand.Execute();
        }
        else
        {
            var from = status == VersionStatus.Pending ? Today.AddDays(1) : Today.AddDays(-10);
            repo.ByIdentityResult = Dto(1, parentId: 5, from, EffectivePeriod.OpenEnd, id: 77);
            await vm.LoadAsync(1, Today);
            if (mode == OrgUnitCardMode.Editing)
                vm.BeginEditCommand.Execute();
            else if (mode == OrgUnitCardMode.Closing)
                vm.BeginCloseCommand.Execute();
        }

        Assert.Equal(expectedEnabled, vm.IsEffectivePeriodEnabled);
    }

    // --- D1/D2 same-day cancel-plan cutover ---
    // A version whose EffectiveFrom == today shows the `Đang hiệu lực` label (VersionStatusResolver is
    // deliberately unchanged) yet the server now cancels it, not closes it (VersionCloseRules D1). These
    // tests pin that the SCREEN follows the same server-authoritative branch, not the Status label — the
    // pre-fix defect branched on `Status == Pending`, which is false here.

    [Fact]
    public async Task IsEffectivePeriodEnabled_FromEqualsToday_DisabledDespiteEffectiveLabel()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        // Sanity: the label really is Effective, not Pending — if this line goes red the rest of the
        // test no longer exercises the gap the pre-fix defect left open.
        vm.Status.Should().Be(VersionStatus.Effective);

        vm.BeginCloseCommand.Execute();

        vm.IsEffectivePeriodEnabled.Should().BeFalse("a From == today version is cancel-plan, same as Pending, even though its label reads Effective");
    }

    [Fact]
    public async Task Save_Close_FromEqualsToday_ShowsConfirmation_AndCancels_NotRetiresWithDate()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var confirm = new FakeConfirmationPrompt(confirm: true);
        var repo = new FakeOrgUnitRepository();
        var vm = new OrgUnitDeclarationViewModel(
            repo, declaration, new FixedDates(Today), new FakeCurrentUser("tester"),
            new FakeAuthorizationService(), confirm, new FakeBreakGlassPolicy());
        repo.ByIdentityResult = Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd, id: 88);
        repo.ByIdentityResultAfterClose = Dto(
            1, parentId: 5, Today.AddDays(-30), Today.AddDays(-1), id: 55, isActive: false, cancelled: true);
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.Reason = "hủy cùng ngày hiệu lực";

        await vm.SaveCommand.Execute();

        // Confirmation dialog must fire — this is the exact gap the pre-fix bug skipped for a same-day
        // version (it fell through the blank-EffectiveTo cancel path with no confirm at all).
        confirm.WasCalled.Should().BeTrue();
        confirm.LastMessage.Should().Be("Đóng mã đơn vị sẽ hủy kỳ hiệu lực của đơn vị");
        declaration.CloseCallCount.Should().Be(1);
        declaration.LastRequest.Should().NotBeNull();
        // Cancel, not retire-with-date: EffectiveThrough travels as null regardless of whatever was
        // left in the (disabled) EffectiveTo box.
        declaration.LastRequest!.EffectiveThrough.Should().BeNull();
        repo.CancelPlanCallCount.Should().Be(0); // cancel routes through the declaration service, never the repo directly
        repo.LastCloseOrgUnitId.Should().BeNull();
        vm.Severity.Should().Be(StatusSeverity.Success);
        // Requester decision 2026-08-10: the cancel branch must distinguish itself from an ordinary
        // close/retire so the operator's only signal after an irreversible cancel is not "Đã lưu.".
        vm.StatusMessage.Should().Be("Đã hủy.");
    }

    [Fact]
    public async Task Save_Close_FromEqualsTodayMinusOne_StillRetiresWithTypedDate()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, confirm) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-1), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);

        vm.BeginCloseCommand.Execute();
        // A version one day older than the D1 cutover keeps the ordinary retire-with-date flow: the strip
        // stays editable, not disabled like the same-day case above.
        vm.IsEffectivePeriodEnabled.Should().BeTrue();

        vm.EffectiveTo = Today.AddDays(5);
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        declaration.CloseCallCount.Should().Be(1);
        declaration.LastRequest.Should().NotBeNull();
        declaration.LastRequest!.EffectiveThrough.Should().Be(Today.AddDays(5));
        vm.Severity.Should().Be(StatusSeverity.Success);
        // Ordinary close/retire keeps the pre-existing wording, distinct from the cancel branch's "Đã hủy.".
        vm.StatusMessage.Should().Be("Đã lưu.");
        // The retire branch is unconfirmed-by-design (no dialog) — pin the other half of that guarantee
        // (the cancel-plan branch's confirmation is pinned by Save_Close_FromEqualsToday_... above).
        confirm.WasCalled.Should().BeFalse();
    }

    // FR9: load-bearing XAML binding — deleting IsEnabled="{Binding IsEffectivePeriodEnabled}" must RED this.
    [Fact]
    public void OrgUnitDeclarationView_BindsAstEffectivePeriodIsEnabled_ToIsEffectivePeriodEnabled()
    {
        var xamlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "AST", "Views", "Iam", "OrgUnit", "OrgUnitDeclarationView.xaml"));
        var xaml = File.ReadAllText(xamlPath);
        xaml.Should().Contain("IsEnabled=\"{Binding IsEffectivePeriodEnabled}\"");
    }

    // --- Brief 062 / REVISION 1 section D ---

    private static void AssertClearedCardKeepsBanner(OrgUnitDeclarationViewModel vm, string successMessage)
    {
        vm.Mode.Should().Be(OrgUnitCardMode.ReadOnly);
        vm.OrgCode.Should().BeEmpty();
        vm.OrgNameFullVn.Should().BeEmpty();
        vm.EffectiveFrom.Should().BeNull();
        vm.StatusMessage.Should().Be(successMessage);
        vm.Severity.Should().Be(StatusSeverity.Success);
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_Success_ClearsCardAndKeepsSuccessBanner()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        AssertClearedCardKeepsBanner(vm, "Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_Success_ClearsCardAndKeepsSuccessBanner()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CN001X";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        AssertClearedCardKeepsBanner(vm, "Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_Retire_ClearsCardAndKeepsDaLuu()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        await vm.SaveCommand.Execute();

        AssertClearedCardKeepsBanner(vm, "Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_CancelPlan_ClearsCardAndKeepsDaHuy()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var confirm = new FakeConfirmationPrompt(confirm: true);
        var repo = new FakeOrgUnitRepository();
        var vm = new OrgUnitDeclarationViewModel(
            repo, declaration, new FixedDates(Today), new FakeCurrentUser("tester"),
            new FakeAuthorizationService(), confirm, new FakeBreakGlassPolicy());
        repo.ByIdentityResult = Dto(1, parentId: 5, Today, EffectivePeriod.OpenEnd, id: 88);
        repo.ByIdentityResultAfterClose = Error.NotFound("EffectivePeriod.NoCoverage", "gone today");
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.Reason = "hủy cùng ngày hiệu lực";

        await vm.SaveCommand.Execute();

        confirm.WasCalled.Should().BeTrue();
        AssertClearedCardKeepsBanner(vm, "Đã hủy.");
    }

    [Fact]
    public async Task FinishCloseSuccessAsync_ProbeFailure_DoesNotPublishSuccess()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77);
        await vm.LoadAsync(1, Today);
        repo.ByIdentityResultAfterClose = Error.Failure("Db.Unavailable", "connection lost");
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
        vm.StatusMessage.Should().NotBe("Đã hủy.");
    }

    [Fact]
    public async Task FinishCloseSuccessAsync_LoadAsyncFailureAfterSuccessfulProbe_DoesNotPublishSuccess()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResultFactory = call => call switch
        {
            1 => Dto(1, parentId: 5, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 77),
            2 => Dto(1, parentId: 5, Today.AddDays(-10), Today, id: 77),
            _ => Error.Failure("Db.Unavailable", "reload failed"),
        };
        await vm.LoadAsync(1, Today);
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "đóng đơn vị";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
        vm.StatusMessage.Should().NotBe("Đã hủy.");
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_PostSaveHistoryReloadThrows_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        repo.HistoryException = new InvalidOperationException("boom");
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CN001X";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Warning);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_PostSaveHistoryReloadThrows_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        repo.HistoryException = new InvalidOperationException("boom");
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Warning);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_PostSaveScopeResolutionSoftFails_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        var auth = new FakeAuthorizationService();
        var (vm, repo, _) = BuildForEdit(authorization: auth);
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        // Call 1 = the write-scope check inside SaveCommand (must succeed for Edit to proceed);
        // call 2 = RefreshTreeAndHistoryAsync's own ResolveScopeAsync call, which soft-fails here.
        auth.AuthorizeResultFactory = call => call == 1
            ? new DataScope(ScopeLevel.Global, null, "tester")
            : Error.Forbidden("Authz.NotGranted", "not granted");
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CN001X";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Warning);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_PostSaveScopeResolutionSoftFails_SurfacesAWarningInsteadOfTheSuccessBanner()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var auth = new FakeAuthorizationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration, authorization: auth);
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        // Constraint: Close resolves no scope before the write, so this factory must fail from the
        // first AuthorizeAsync call. Matching Add/Edit's `call == 1` succeed-then-fail shape would
        // let the refresh succeed and make this test vacuous.
        auth.AuthorizeResultFactory = _ => Error.Forbidden("Authz.NotGranted", "not granted");
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        await vm.SaveCommand.Execute();

        vm.Severity.Should().Be(StatusSeverity.Warning);
        vm.StatusMessage.Should().NotBe("Đã lưu.");
    }

    [Fact]
    public async Task LoadAsync_DoesNotRaiseCardClearedAfterSave()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd);
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;

        await vm.LoadAsync(1, Today);

        raised.Should().Be(0);
    }

    [Fact]
    public void BeginAdd_FromEmptyCard_ParentEligibilityIsUnresolvedUntilPeriodIsComplete()
    {
        var (vm, _) = Build();
        vm.BeginAddCommand.Execute();

        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);

        vm.EffectiveFrom = Today;
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);
    }

    [Fact]
    public async Task DuringAdd_CompletePeriod_ParentEligibilityIsLoadingUntilCandidatesReturn()
    {
        var (vm, repo) = Build();
        var tcs = new TaskCompletionSource<IReadOnlyList<OrgUnitPickerItem>>();
        repo.EligibleParentsTcs = tcs;
        vm.BeginAddCommand.Execute();

        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;

        vm.ParentEligibility.Should().Be(ParentEligibilityState.Loading);

        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(vm.ParentEligibility)
                && vm.ParentEligibility == ParentEligibilityState.Resolved)
            {
                resolved.TrySetResult();
            }
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            tcs.SetResult([]);
            try
            {
                await resolved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                vm.ParentEligibility.Should().Be(
                    ParentEligibilityState.Resolved,
                    "ParentEligibility must become Resolved after the candidates task completes (2s timeout); this used to depend on the continuation running inline on the releasing thread");
            }

            vm.ParentEligibility.Should().Be(ParentEligibilityState.Resolved);
            vm.ParentCandidates.Should().BeEmpty();
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }
    }

    [Fact]
    public void DuringAdd_ResolvedEmpty_ParentEligibilityIsResolvedWithNoCandidates()
    {
        var (vm, _) = Build();
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;

        vm.ParentEligibility.Should().Be(ParentEligibilityState.Resolved);
        vm.ParentCandidates.Should().BeEmpty();
    }

    [Fact]
    public void DuringAdd_ResolvedNonEmpty_ParentEligibilityIsResolvedWithCandidates()
    {
        var (vm, repo) = Build();
        repo.EligibleParentsResult = [new OrgUnitPickerItem(9, "PAR — Cha")];
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;

        vm.ParentEligibility.Should().Be(ParentEligibilityState.Resolved);
        vm.ParentCandidates.Should().ContainSingle(c => c.Id == 9);
    }

    [Fact]
    public void DuringAdd_ToBeforeFrom_StillCountsAsCompletePeriodNotUnresolved()
    {
        var (vm, _) = Build();
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.EffectiveTo = Today.AddDays(-1);
        vm.IsUndetermined = false;

        vm.ParentEligibility.Should().NotBe(ParentEligibilityState.Unresolved);
    }

    // --- Brief 062 Fix Round 1 ---

    [Fact]
    public async Task ExecuteSaveAddAsync_VerificationSupersededByNewerLoad_DoesNotClear_NewerLoadOwnsCard()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityByOrgUnitId[10] = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        repo.ByIdentityByOrgUnitId[2] = Dto(2, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd,
            orgCode: "U2", orgNameFullVn: "Unit Two Full", orgNameShortVn: "Unit Two");
        var holdVerify = new TaskCompletionSource();
        var verifyStarted = new TaskCompletionSource();
        repo.BeforeByIdentityReturn = async (id, _) =>
        {
            if (id == 10)
            {
                verifyStarted.TrySetResult();
                await holdVerify.Task;
            }
        };
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        var save = vm.SaveCommand.Execute();
        await verifyStarted.Task;
        await vm.LoadAsync(2, Today);
        holdVerify.SetResult();
        await save;

        vm.OrgCode.Should().Be("U2");
        raised.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_VerificationSupersededByNewerLoad_DoesNotClear_NewerLoadOwnsCard()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityByOrgUnitId[3] = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        repo.ByIdentityByOrgUnitId[2] = Dto(2, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd,
            orgCode: "U2", orgNameFullVn: "Unit Two Full", orgNameShortVn: "Unit Two");
        await vm.LoadAsync(3, Today);
        var holdVerify = new TaskCompletionSource();
        var verifyStarted = new TaskCompletionSource();
        repo.BeforeByIdentityReturn = async (id, _) =>
        {
            if (id == 3)
            {
                verifyStarted.TrySetResult();
                await holdVerify.Task;
            }
        };
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CN001X";
        vm.Reason = "doi ten";

        var save = vm.SaveCommand.Execute();
        await verifyStarted.Task;
        await vm.LoadAsync(2, Today);
        holdVerify.SetResult();
        await save;

        vm.OrgCode.Should().Be("U2");
        raised.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_VerificationSupersededByNewerLoad_DoesNotClear_NewerLoadOwnsCard()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityByOrgUnitId[4] = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        repo.ByIdentityByOrgUnitId[2] = Dto(2, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd,
            orgCode: "U2", orgNameFullVn: "Unit Two Full", orgNameShortVn: "Unit Two");
        await vm.LoadAsync(4, Today);
        var n = 0;
        var holdVerify = new TaskCompletionSource();
        var verifyStarted = new TaskCompletionSource();
        repo.BeforeByIdentityReturn = async (id, _) =>
        {
            if (id != 4)
                return;
            n++;
            // 1 = FinishCloseSuccessAsync probe; 2 = LoadAsync verification.
            if (n == 2)
            {
                verifyStarted.TrySetResult();
                await holdVerify.Task;
            }
        };
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        var save = vm.SaveCommand.Execute();
        await verifyStarted.Task;
        await vm.LoadAsync(2, Today);
        holdVerify.SetResult();
        await save;

        vm.OrgCode.Should().Be("U2");
        raised.Should().Be(0);
    }

    [Fact]
    public void DuringAdd_ObsoleteQueryAfterIncomplete_DoesNotMoveEligibilityOrCandidates()
    {
        var (vm, repo) = Build();
        var tcs = new TaskCompletionSource<IReadOnlyList<OrgUnitPickerItem>>();
        repo.EligibleParentsTcs = tcs;
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Loading);

        vm.EffectiveFrom = null;
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);

        tcs.SetResult([new OrgUnitPickerItem(9, "PAR — Cha")]);
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);
        vm.ParentCandidates.Should().BeEmpty();
    }

    [Fact]
    public async Task DuringAdd_ObsoleteQueryAfterRelock_DoesNotMoveEligibilityOrCandidates()
    {
        var (vm, repo) = Build();
        repo.ByIdentityResult = Dto(1, parentId: null, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        await vm.LoadAsync(1, Today);
        var tcs = new TaskCompletionSource<IReadOnlyList<OrgUnitPickerItem>>();
        repo.EligibleParentsTcs = tcs;
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = new DateOnly(2021, 1, 1);
        vm.IsUndetermined = true;
        vm.IsParentLocked.Should().BeFalse();
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Loading);

        vm.EffectiveFrom = new DateOnly(2020, 6, 1);
        vm.EffectiveTo = new DateOnly(2020, 12, 31);
        vm.IsUndetermined = false;
        vm.IsParentLocked.Should().BeTrue();
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);

        tcs.SetResult([new OrgUnitPickerItem(9, "PAR — Cha")]);
        vm.IsParentLocked.Should().BeTrue();
        vm.ParentId.Should().Be(1);
        vm.ParentEligibility.Should().Be(ParentEligibilityState.Unresolved);
        vm.ParentCandidates.Should().BeEmpty();
    }

    [Fact]
    public void DuringAdd_ParentCandidatesQueryThrows_LeavesFailedNotLoading()
    {
        var (vm, repo) = Build();
        repo.EligibleParentsException = new InvalidOperationException("connection lost");
        vm.BeginAddCommand.Execute();
        vm.EffectiveFrom = Today;
        vm.IsUndetermined = true;

        vm.ParentEligibility.Should().Be(ParentEligibilityState.Failed);
        vm.Severity.Should().Be(StatusSeverity.Error);
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteSaveAddAsync_Success_RaisesCardClearedAfterSaveOnce()
    {
        var (vm, repo, _) = BuildForSave();
        repo.CreateIdentityResult = 10;
        repo.ByIdentityResult = Dto(10, parentId: null, Today, EffectivePeriod.OpenEnd, orgCode: "ABCD");
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginAddCommand.Execute();
        FillValidAddForm(vm);

        await vm.SaveCommand.Execute();

        raised.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteSaveEditAsync_Success_RaisesCardClearedAfterSaveOnce()
    {
        var (vm, repo, _) = BuildForEdit();
        repo.ByIdentityResult = Dto(3, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 30, orgCode: "CN001");
        await vm.LoadAsync(3, Today);
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginEditCommand.Execute();
        vm.OrgCode = "CN001X";
        vm.Reason = "doi ten";

        await vm.SaveCommand.Execute();

        raised.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteSaveCloseAsync_Success_RaisesCardClearedAfterSaveOnce()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityResult = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        await vm.LoadAsync(4, Today);
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        await vm.SaveCommand.Execute();

        raised.Should().Be(1);
    }

    // --- Brief 062 Fix Round 2 ---

    [Fact]
    public async Task ExecuteSaveCloseAsync_ExpectedAbsenceProbeSupersededByNewerLoad_DoesNotClear_NewerLoadOwnsCard()
    {
        var declaration = new FakeOrgUnitDeclarationService();
        var (vm, repo, _) = BuildForEdit(declaration: declaration);
        repo.ByIdentityByOrgUnitId[4] = Dto(4, parentId: 1, Today.AddDays(-10), EffectivePeriod.OpenEnd, id: 40, orgCode: "CN004");
        repo.ByIdentityByOrgUnitId[2] = Dto(2, parentId: null, Today.AddDays(-10), EffectivePeriod.OpenEnd,
            orgCode: "U2", orgNameFullVn: "Unit Two Full", orgNameShortVn: "Unit Two");
        await vm.LoadAsync(4, Today);
        // Close-through-today: the post-write probe at Today is expected absence (nothing left to show).
        repo.ByIdentityByOrgUnitId[4] = Error.NotFound("EffectivePeriod.NoCoverage", "gone today");
        var holdProbe = new TaskCompletionSource();
        var probeStarted = new TaskCompletionSource();
        repo.BeforeByIdentityReturn = async (id, _) =>
        {
            if (id != 4)
                return;
            probeStarted.TrySetResult();
            await holdProbe.Task;
        };
        var raised = 0;
        vm.CardClearedAfterSave += (_, _) => raised++;
        vm.BeginCloseCommand.Execute();
        vm.EffectiveTo = Today;
        vm.IsUndetermined = false;
        vm.Reason = "dong";

        var save = vm.SaveCommand.Execute();
        await probeStarted.Task;
        await vm.LoadAsync(2, Today);
        holdProbe.SetResult();
        await save;

        vm.OrgCode.Should().Be("U2");
        raised.Should().Be(0);
    }
}
