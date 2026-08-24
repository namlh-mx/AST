using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Iam.Repositories;
using AST.Modules.IAM;
using AST.Modules.IAM.Tests.TestSupport;
using ErrorOr;
using FluentAssertions;

namespace AST.Modules.IAM.Tests.Integration;

// Slice C2 — syncs the `function` catalog from code (docs/design-function-catalog-sync.md).
// AUTOMATIC sync only ADDS (case 1) + UPDATES metadata (case 2, exact-match case 7); REMOVE and RESTORE only FLAG
// (removal/reopen candidate), it does NOT do them itself. Tested against a real DB (SKIP if no DB).
public sealed class FunctionCatalogSyncTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod Epoch = new(new DateOnly(2000, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DataScope GlobalScope = new(ScopeLevel.Global, null, "system-sync");

    private FunctionCatalogSyncService Sync(FunctionRegistry registry) =>
        new(Functions, registry, new FixedBusinessDateProvider(Today));

    private static FunctionRegistry RegistryWith(params FunctionDescriptor[] descriptors)
    {
        var registry = new FunctionRegistry();
        foreach (var d in descriptors)
        {
            registry.Register(d);
        }

        return registry;
    }

    private static FunctionDescriptor Descriptor(string key, string businessCode, string displayName) =>
        new(key, businessCode, displayName, MenuGroupCodes.ConfigSecurity, $"{key}/Target", "perm", 1);

    private async Task<FunctionVersionDto?> ActiveByKeyAsync(string key) =>
        (await Functions.GetInScopeAsync(GlobalScope, Today)).FirstOrDefault(f => f.FunctionKey == key);

    [Fact]
    public async Task Sync_NewKey_CreatesFunctionAtEpoch()
    {
        SkipUnlessDbAvailable();

        var registry = RegistryWith(Descriptor("Iam.User.View", "FX001", "Xem người dùng"));

        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Contains("Iam.User.View", result.Value.Created);

        var dto = await ActiveByKeyAsync("Iam.User.View");
        Assert.NotNull(dto);
        Assert.Equal("Xem người dùng", dto!.DisplayName);
        Assert.Equal("FX001", dto.BusinessCode);
        Assert.Equal(new DateOnly(2000, 1, 1), dto.EffectiveFrom);
        Assert.Equal(EffectivePeriod.OpenEnd, dto.EffectiveTo);
        Assert.Equal("system-sync", dto.RecordedBy);
    }

    [Fact]
    public async Task Sync_MetadataChanged_UpsertsCase7_KeepsPermissionResolvable()
    {
        SkipUnlessDbAvailable();

        // Seeds a function at the epoch + 1 role_permission pointing to its IDENTITY.
        var functionId = await CreateFunctionAsync("Iam.User.Edit", Epoch);
        var role = await CreateRoleAsync("MC-ROLE", "Vai trò", Epoch);
        var rpId = await CreateGrantAsync(
            role, functionId, Epoch, ScopeLevel.OwnOrgUnit, "grant");

        var before = await ActiveByKeyAsync("Iam.User.Edit");

        // Registry changes the display name + business code.
        var registry = RegistryWith(Descriptor("Iam.User.Edit", "FX999", "Sửa người dùng (mới)"));
        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Contains("Iam.User.Edit", result.Value.MetadataUpdated);

        // The new active version carries the new metadata, same epoch period, with a DIFFERENT version id than the old one (case 7: soft-delete + insert).
        var after = await ActiveByKeyAsync("Iam.User.Edit");
        Assert.NotNull(after);
        Assert.Equal("Sửa người dùng (mới)", after!.DisplayName);
        Assert.Equal("FX999", after.BusinessCode);
        Assert.Equal(new DateOnly(2000, 1, 1), after.EffectiveFrom);
        Assert.Equal(EffectivePeriod.OpenEnd, after.EffectiveTo);
        Assert.NotEqual(before!.Id, after.Id);

        // NO overlap between the 2 active versions (the old one now has isactive=0) — confirmed by the integrity check grid.
        var violations = await IntegrityChecks.RunAllChecksAsync();
        Assert.DoesNotContain(violations, v => v.Kind == IntegrityViolationKind.OverlappingActivePeriods && v.Table == "function_version");

        // The permission grant still resolves (points to the identity, metadata change does not break it).
        var stillResolves = await RolePermissions.GetByIdentityAsync(rpId, Today);
        Assert.False(stillResolves.IsError, DescribeErrors(stillResolves.Errors));
    }

    [Fact]
    public async Task Sync_MetadataChanged_OnNonEpochPeriod_PreservesPeriod()
    {
        SkipUnlessDbAvailable();

        // Simulates a function already reopened on period [2021, open] (D != epoch). Changing metadata must NOT
        // pull effective_from back to 2000 (must exactly match the current period — F1 review lock).
        var reopenPeriod = new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd);
        await CreateFunctionAsyncWith("Iam.Report.View", "FX010", "Xem báo cáo", reopenPeriod);

        var registry = RegistryWith(Descriptor("Iam.Report.View", "FX010", "Xem báo cáo (mới)"));
        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Contains("Iam.Report.View", result.Value.MetadataUpdated);

        var after = await ActiveByKeyAsync("Iam.Report.View");
        Assert.NotNull(after);
        Assert.Equal("Xem báo cáo (mới)", after!.DisplayName);
        Assert.Equal(new DateOnly(2021, 1, 1), after.EffectiveFrom);   // NOT pulled back to 2000
        Assert.Equal(EffectivePeriod.OpenEnd, after.EffectiveTo);
    }

    [Fact]
    public async Task Sync_NoChange_IsNoop()
    {
        SkipUnlessDbAvailable();

        await CreateFunctionAsyncWith("Iam.Role.View", "FX002", "Xem vai trò", Epoch);
        var registry = RegistryWith(Descriptor("Iam.Role.View", "FX002", "Xem vai trò"));

        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Empty(result.Value.Created);
        Assert.Empty(result.Value.MetadataUpdated);
        Assert.Empty(result.Value.RemovalCandidates);
        Assert.Empty(result.Value.ReopenCandidates);
    }

    [Fact]
    public async Task Sync_RunTwice_SecondRunIsIdempotent()
    {
        SkipUnlessDbAvailable();

        var registry = RegistryWith(Descriptor("Iam.Role.Edit", "FX003", "Sửa vai trò"));

        var first = await Sync(registry).SyncAsync();
        Assert.False(first.IsError, DescribeErrors(first.Errors));
        Assert.Contains("Iam.Role.Edit", first.Value.Created);

        var second = await Sync(registry).SyncAsync();
        Assert.False(second.IsError, DescribeErrors(second.Errors));
        Assert.Empty(second.Value.Created);
        Assert.Empty(second.Value.MetadataUpdated);
        Assert.Empty(second.Value.RemovalCandidates);
        Assert.Empty(second.Value.ReopenCandidates);
    }

    [Fact]
    public async Task Sync_KeyRemovedFromCode_FlagsRemovalCandidate_DoesNotClose()
    {
        SkipUnlessDbAvailable();

        await CreateFunctionAsync("Iam.User.Delete", Epoch);   // active today
        var registry = new FunctionRegistry();                 // code no longer declares this key

        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Contains("Iam.User.Delete", result.Value.RemovalCandidates);

        // Does NOT auto-close — still active today.
        Assert.NotNull(await ActiveByKeyAsync("Iam.User.Delete"));
    }

    [Fact]
    public async Task Sync_ReaddPreviouslyClosedKey_FlagsReopenCandidate_NoDuplicateIdentity()
    {
        SkipUnlessDbAvailable();

        // Simulates a function that has been closed: its version period ended before today -> present in known keys, NOT active.
        await CreateFunctionAsync("Iam.Role.Delete", new EffectivePeriod(new DateOnly(2000, 1, 1), new DateOnly(2020, 12, 31)));
        var beforeCount = (await Functions.GetAllKnownFunctionKeysAsync()).Count(k => k == "Iam.Role.Delete");

        var registry = RegistryWith(Descriptor("Iam.Role.Delete", "FX004", "Xóa vai trò"));
        var result = await Sync(registry).SyncAsync();

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Contains("Iam.Role.Delete", result.Value.ReopenCandidates);
        Assert.DoesNotContain("Iam.Role.Delete", result.Value.Created);

        // Does NOT create a duplicate identity: the count of versions with this key does not increase, and there is no active version today.
        var afterCount = (await Functions.GetAllKnownFunctionKeysAsync()).Count(k => k == "Iam.Role.Delete");
        Assert.Equal(beforeCount, afterCount);
        Assert.Null(await ActiveByKeyAsync("Iam.Role.Delete"));
    }

    // =============================================================================================
    // The consumer half of backlog 0.4b-A. FunctionRepositoryCreateTests proves the REPOSITORY refuses
    // to mint a second identity; it cannot prove what this SERVICE does with that answer -- and the two
    // ways to get this branch wrong are both silent: counting the loser as `created` (claiming work this
    // run did not do), or treating it as an error (abandoning every descriptor after it).
    //
    // A fake repository is used deliberately. The branch under test belongs to the service, and the
    // outcome cannot be produced through real MySQL without a race harness, which would be testing the
    // repository again. This is not a DB mock standing in for a DB test (rule-testing) -- the DB-backed
    // proof of the same guard is FunctionRepositoryCreateTests T3/T4. Precedent: FakeFunctionRepository
    // in AST.Shell.Tests' RoleDeclarationViewModelTests.
    // =============================================================================================

    [Fact]
    public async Task Sync_KeyCreatedConcurrentlyByAnotherMachine_IsNotReported_NotAnError_AndTheNextKeyStillSyncs()
    {
        var repository = new LosesTheRaceOnFirstKeyRepository();
        var registry = RegistryWith(
            Descriptor("Iam.Race.Lost", "FX901", "Chức năng bị máy khác tạo trước"),
            Descriptor("Iam.Race.Won", "FX902", "Chức năng máy này tạo được"));

        var result = await new FunctionCatalogSyncService(
            repository, registry, new FixedBusinessDateProvider(Today)).SyncAsync();

        result.IsError.Should().BeFalse(
            "another workstation winning the race is normal startup traffic, not a failure");
        result.Value.Created.Should().NotContain(
            "Iam.Race.Lost", "this run did not create it -- reporting it would claim work another machine did");
        result.Value.Created.Should().Contain(
            "Iam.Race.Won", "the descriptor AFTER the lost one must still be processed");
        repository.CreateAttempts.Should().Be(
            2, "a losing key must not abort the loop -- both descriptors are attempted");
    }

    // Returns KeyAlreadyPresent for the FIRST key it is asked to create and Created for every later one,
    // so a service that stops at the losing key fails on the second assertion rather than passing quietly.
    private sealed class LosesTheRaceOnFirstKeyRepository : IFunctionRepository
    {
        public int CreateAttempts { get; private set; }

        public Task<ErrorOr<FunctionCreateOutcome>> CreateAsync(
            EffectivePeriod period, string functionKey, string businessCode, string displayName,
            string menuGroup, string navTarget, string recordedBy, string? reason)
        {
            CreateAttempts++;
            FunctionCreateOutcome outcome = CreateAttempts == 1
                ? new FunctionCreateOutcome.KeyAlreadyPresent()
                : new FunctionCreateOutcome.Created(CreateAttempts, new UpsertResult(CreateAttempts, [], []));
            return Task.FromResult<ErrorOr<FunctionCreateOutcome>>(outcome);
        }

        // Empty on both reads, so every descriptor takes the sync's create branch.
        public Task<IReadOnlyList<FunctionVersionDto>> GetInScopeAsync(DataScope scope, DateOnly asOf) =>
            Task.FromResult<IReadOnlyList<FunctionVersionDto>>([]);
        public Task<IReadOnlyList<string>> GetAllKnownFunctionKeysAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ErrorOr<FunctionVersionDto>> GetByIdentityAsync(long functionId, DateOnly asOf) =>
            throw new NotSupportedException();
        public Task<ErrorOr<FunctionVersionDto>> GetByKeyAsync(string functionKey, DateOnly asOf) =>
            throw new NotSupportedException();
        public Task<ErrorOr<UpsertResult>> UpsertAsync(
            long functionId, EffectivePeriod period, string functionKey, string businessCode,
            string displayName, string menuGroup, string navTarget, string recordedBy, string? reason) =>
            throw new NotSupportedException();
    }

    // Seeds a function with explicit metadata + period (the base helper CreateFunctionAsync hardcodes FX000/a test name).
    private async Task<long> CreateFunctionAsyncWith(string key, string businessCode, string displayName, EffectivePeriod period)
    {
        var id = await InsertHeaderAsync("function");
        var result = await Functions.UpsertAsync(
            id, period, key, businessCode, displayName, MenuGroupCodes.ConfigSecurity, $"{key}/Target", "tester", "seed");
        Assert.False(result.IsError, DescribeErrors(result.Errors));
        return id;
    }
}
