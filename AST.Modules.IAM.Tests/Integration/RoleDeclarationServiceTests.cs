using System.Data;
using System.Text.Json;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using FluentAssertions.Execution;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Pins RoleDeclarationService (AST.Modules.IAM/RoleDeclarationService.cs) — closes OPEN-B2: "Edit role +
// Revoke a grant + Add a grant in one Save". Contract: AST.Core/Iam/IRoleDeclarationService.cs.
// Real MySQL, no mocking of persistence (rule-testing invariant 1) — IAuthorizationService/IBreakGlassPolicy/
// ICurrentWindowsUser are non-persistence seams, plain hand-rolled fakes (same style as
// AuthorizationServiceTests.FakeBreakGlassPolicy), never Moq.
public sealed class RoleDeclarationServiceTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2021 = new(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));

    private const string Actor = "tester";

    private RoleRepository RoleRepo => (RoleRepository)Roles;
    private RolePermissionRepository GrantRepo => (RolePermissionRepository)RolePermissions;
    private FunctionRepository FunctionRepo => (FunctionRepository)Functions;

    private const string ExpectedActorUsername = Actor;

    private RoleDeclarationService Service => BuildService();

    private RoleDeclarationService BuildService(
        IAuthorizationService? authorization = null,
        IBreakGlassPolicy? breakGlass = null,
        string actor = Actor,
        IAuditLogWriter? auditLog = null) =>
        new(
            RoleRepo,
            GrantRepo,
            FunctionRepo,
            Connections,
            auditLog ?? new AuditLogWriter(),
            authorization ?? new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, actor)),
            breakGlass ?? new FakeBreakGlassPolicy(),
            new FakeCurrentWindowsUser(actor),
            new FixedBusinessDateProvider(Today));

    private RoleDeclarationService CreateServiceWithAuditWriter(IAuditLogWriter writer) =>
        BuildService(auditLog: writer);

    // =========================================================================================
    // AC1 — Edit + Revoke + Add in one Save.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_EditPlusRevokePlusAdd_PersistsAllThree()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R1", "Vai trò gốc", OpenFrom2020);
        var revokedFunction = await CreateFunctionAsync("B2.Fn.Revoked", OpenFrom2020);
        var addedFunction = await CreateFunctionAsync("B2.Fn.Added", OpenFrom2020);

        var grantToRevoke = await CreateGrantAsync(role, revokedFunction, OpenFrom2020, ScopeLevel.Global);

        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        // Save derives [today, OpenEnd]; this test pins Edit+Revoke+Add persistence, not the period.
        var request = await EditSaveRequestAsync(
            role, "B2-R1", "Vai trò đã sửa",
            grantIdentityIdsToRevoke: [grantToRevoke],
            grantsToAdd: [new RolePermissionGrantToAdd(addedFunction, ScopeLevel.OwnOrgUnit, "grant note")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Value.RevokedRolePermissionIds.Should().Equal(grantToRevoke);
        result.Value.AddedRolePermissionIds.Should().HaveCount(1);
        var addedGrant = result.Value.AddedRolePermissionIds[0];

        (await CountRowsAsync("role_version", "role_id", role)).Should().BeGreaterThan(roleRowsBefore);
        var editedRole = await Roles.GetByIdentityAsync(role, Today);
        editedRole.IsError.Should().BeFalse(DescribeErrors(editedRole.Errors));
        editedRole.Value.RoleName.Should().Be("Vai trò đã sửa");

        // Revoked grant: a revoke stops the grant FROM today, so with the model's INCLUSIVE end its last
        // effective day is yesterday — it is already gone AS OF today, not the day after. (This assertion
        // previously expected coverage through today, which was the retroactive-coverage defect itself.)
        var revokedYesterday = await RolePermissions.GetByIdentityAsync(grantToRevoke, Today.AddDays(-1));
        revokedYesterday.IsError.Should().BeFalse(DescribeErrors(revokedYesterday.Errors));
        revokedYesterday.Value.EffectiveTo.Should().Be(Today.AddDays(-1));
        (await RolePermissions.GetByIdentityAsync(grantToRevoke, Today)).IsError.Should().BeTrue(
            "the revoked grant must no longer be effective on the day of the revoke");

        // Added grant: a brand-new, active identity.
        var addedRow = await RolePermissions.GetByIdentityAsync(addedGrant, Today);
        addedRow.IsError.Should().BeFalse(DescribeErrors(addedRow.Errors));
        addedRow.Value.FunctionId.Should().Be(addedFunction);
        addedRow.Value.ScopeLevel.Should().Be(ScopeLevel.OwnOrgUnit);
        (await CountRowsAsync("role_permission_version", "role_permission_id", addedGrant)).Should().Be(1);
    }

    // =========================================================================================
    // AC2/AC3/AC8b — forced failure on the LAST add rolls back role + every grant + every audit row,
    // and leaves no header row either: identities are minted inside the same transaction that writes
    // their first version (§7), so the rollback removes them and no compensation step exists.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_FailureOnLastAdd_RoleAndAllGrantsAreRolledBack()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R2", "Vai trò gốc", OpenFrom2020);
        var wideFunction = await CreateFunctionAsync("B2.Fn.Wide", OpenFrom2020);
        // Deliberately narrower than the requested grant period below — a REAL business reason (STRICT
        // temporal-FK, D8) for the LAST add to fail, not a synthetic one.
        var narrowFunction = await CreateFunctionAsync("B2.Fn.Narrow", Year2021);

        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        // Save derives [today, OpenEnd]; Year2021 function coverage is what produces ParentGap.
        var request = await EditSaveRequestAsync(
            role, "B2-R2", "Vai trò đã sửa",
            grantsToAdd:
            [
                new RolePermissionGrantToAdd(wideFunction, ScopeLevel.Global, "first add"),
                new RolePermissionGrantToAdd(narrowFunction, ScopeLevel.Global, "second add - fails"),
            ]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");

        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(
            roleRowsBefore, "the role edit must roll back together with the failed grant add");
        var stillOriginal = await Roles.GetByIdentityAsync(role, Today);
        stillOriginal.Value.RoleName.Should().Be("Vai trò gốc");
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_FailureOnLastAdd_LeavesNoGrantHeaderBehind()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R3", "Vai trò gốc", OpenFrom2020);
        var wideFunction = await CreateFunctionAsync("B2.Fn.Wide3", OpenFrom2020);
        var narrowFunction = await CreateFunctionAsync("B2.Fn.Narrow3", Year2021);

        var headerRowsBefore = await CountAllRowsAsync("role_permission");

        // Discriminator: ParentGap on the Year2021 function must be reached, i.e. the first add really did
        // mint and write. A period-validation reject before that point would make this test vacuous.
        var request = await EditSaveRequestAsync(
            role, "B2-R3", "Vai trò đã sửa",
            grantsToAdd:
            [
                new RolePermissionGrantToAdd(wideFunction, ScopeLevel.Global, "first add"),
                new RolePermissionGrantToAdd(narrowFunction, ScopeLevel.Global, "second add - fails"),
            ]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(
            "TemporalFk.ParentGap", "the real failure this test relies on is the STRICT temporal-FK check on " +
            "narrowFunction — pin the code so a different reject cannot silently re-mask it");
        (await CountAllRowsAsync("role_permission")).Should().Be(
            headerRowsBefore,
            "the first add's identity is minted inside the composite transaction, so the rollback removes it — " +
            "no compensation step runs, and none is relied on");
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_NewRole_FailureOnLastAdd_LeavesNoRoleHeaderAndNoGrantHeader()
    {
        SkipUnlessDbAvailable();

        // The atomicity criterion on the path that mints BOTH kinds of identity: a brand-new role and its
        // grants. Counting header rows is the whole point — a zero-version header passes every version-row
        // assertion in this file.
        var wideFunction = await CreateFunctionAsync("B2.Fn.WideNew", OpenFrom2020);
        var narrowFunction = await CreateFunctionAsync("B2.Fn.NarrowNew", Year2021);

        var roleHeadersBefore = await CountAllRowsAsync("role");
        var grantHeadersBefore = await CountAllRowsAsync("role_permission");

        var result = await BuildService().SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-NEWFAIL", "Vai trò mới", false, null,
            [],
            [
                new RolePermissionGrantToAdd(wideFunction, ScopeLevel.Global, "first add"),
                new RolePermissionGrantToAdd(narrowFunction, ScopeLevel.Global, "second add - fails"),
            ]));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(
            "TemporalFk.ParentGap", "the role write and the first add must both have succeeded before the " +
            "failure — otherwise nothing was minted and this test proves nothing");
        (await CountAllRowsAsync("role")).Should().Be(
            roleHeadersBefore, "a failed save may not leave a role header behind");
        (await CountAllRowsAsync("role_permission")).Should().Be(
            grantHeadersBefore, "a failed save may not leave a grant header behind");
        (await RoleRepo.GetCodeOwnersAsync("B2-NEWFAIL", Today)).Should().BeEmpty(
            "the code must be free again — nothing about this save survived");
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_FailureOnLastAdd_FirstAddsAuditRowDoesNotSurvive()
    {
        SkipUnlessDbAvailable();

        // THE DISCRIMINATOR (rule-adversarial-lens): if the audit write were ever moved OUTSIDE the composite
        // transaction (e.g. a second auto-committing connection), the FIRST add's audit row would survive
        // this rollback even though the overall Save failed. Asserting the whole audit_log table is empty
        // catches that mutation; asserting only "no error" would not.
        var role = await CreateRoleAsync("B2-R4", "Vai trò gốc", OpenFrom2020);
        var wideFunction = await CreateFunctionAsync("B2.Fn.Wide4", OpenFrom2020);
        var narrowFunction = await CreateFunctionAsync("B2.Fn.Narrow4", Year2021);

        // Discriminator: the composite must actually run so an audit write outside the transaction
        // would survive this rollback.
        var request = await EditSaveRequestAsync(
            role, "B2-R4", "Vai trò đã sửa",
            grantsToAdd:
            [
                new RolePermissionGrantToAdd(wideFunction, ScopeLevel.Global, "first add - would audit"),
                new RolePermissionGrantToAdd(narrowFunction, ScopeLevel.Global, "second add - fails"),
            ]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("TemporalFk.ParentGap");
        (await CountAllRowsAsync("audit_log")).Should().Be(
            0, "the first add's audit row must be rolled back with everything else, proving it shares the composite transaction");
    }

    // =========================================================================================
    // AC4/AC5/AC6 — bidirectional admin-flag gate, orchestrated.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_GrantAdminFlag_NonBreakGlassActor_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-ADM1", "Vai trò", OpenFrom2020, isAdminRole: false);
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        var request = await EditSaveRequestAsync(
            role, "B2-ADM1", "Vai trò", isAdminRole: true, reason: "escalate");

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy()).SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.AdminFlagChangeNotAuthorized");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_ClearAdminFlag_NonBreakGlassActor_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-ADM2", "Vai trò admin", OpenFrom2020, isAdminRole: true);
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        var request = await EditSaveRequestAsync(
            role, "B2-ADM2", "Vai trò admin", isAdminRole: false, reason: "demote");

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy()).SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.AdminFlagChangeNotAuthorized");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        var stillAdminPast = await Roles.GetByIdentityAsync(role, new DateOnly(2020, 6, 1));
        stillAdminPast.Value.IsAdminRole.Should().BeTrue();
        var stillAdminToday = await Roles.GetByIdentityAsync(role, Today);
        stillAdminToday.Value.IsAdminRole.Should().BeTrue();
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_GrantAdminFlag_BreakGlassActor_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-ADM3", "Vai trò", OpenFrom2020, isAdminRole: false);

        var request = await EditSaveRequestAsync(
            role, "B2-ADM3", "Vai trò", isAdminRole: true, reason: "escalate");

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy(Actor)).SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var past = await Roles.GetByIdentityAsync(role, new DateOnly(2020, 6, 1));
        past.Value.IsAdminRole.Should().BeFalse();
        var row = await Roles.GetByIdentityAsync(role, Today);
        row.Value.IsAdminRole.Should().BeTrue();
    }

    // =========================================================================================
    // AC7/AC8a — concurrency: AdminFlagLockKey Enlisted unconditionally, blocks a concurrent holder.
    // THE DISCRIMINATOR for AC8's "Enlist(AdminFlagLockKey) deleted" mutation: without that Enlist, the
    // composite would never block on this external holder and the assertions below would fail.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_ExternalHolderOfAdminFlagLock_BlocksThenProceedsAfterRelease()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-LOCK", "Vai trò", OpenFrom2020);
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        await using var blocker = await HoldLockAsync(RoleRepository.AdminFlagLockKey);

        // This test is about AdminFlagLockKey blocking a concurrent holder, not about the period.
        var request = await EditSaveRequestAsync(role, "B2-LOCK", "Vai trò composite");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(cancellationToken);

        var saveTask = Task.Run(async () => await BuildService().SaveRoleDeclarationAsync(request));

        await WaitUntilUserLockWaiterAsync(observer, RoleRepository.AdminFlagLockKey, cancellationToken);

        saveTask.IsCompleted.Should().BeFalse(
            "the composite must still be waiting on AdminFlagLockKey (Enlisted unconditionally), not have skipped past it");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);

        await ReleaseLockAsync(blocker, RoleRepository.AdminFlagLockKey);

        var result = await saveTask;
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountRowsAsync("role_version", "role_id", role)).Should().BeGreaterThan(roleRowsBefore);
    }

    // =========================================================================================
    // AC9 — audit row shape: one role-change per save plus exactly 1 per grant Add/Revoke/Cancel.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_Success_WritesExactlyOneAuditRowPerGrantEvent()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-AUD", "Vai trò", OpenFrom2020);
        var revokedFunction = await CreateFunctionAsync("B2.Fn.AudRevoked", OpenFrom2020);
        var addedFunction = await CreateFunctionAsync("B2.Fn.AudAdded", OpenFrom2020);

        var grantToRevoke = await CreateGrantAsync(role, revokedFunction, OpenFrom2020, ScopeLevel.Global);

        // This test is about audit-row shape, not the period.
        var request = await EditSaveRequestAsync(
            role, "B2-AUD", "Vai trò đã sửa",
            grantIdentityIdsToRevoke: [grantToRevoke],
            grantsToAdd: [new RolePermissionGrantToAdd(addedFunction, ScopeLevel.Global, "note")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var addedGrant = result.Value.AddedRolePermissionIds[0];

        var auditRows = await ReadAuditRowsForTargetAsync($"role:{role}");
        auditRows.Should().HaveCount(3, "one role-change plus one row per grant event (1 revoke + 1 add), all under role:{id}");
        auditRows.Count(r => r.EventType == "role-change").Should().Be(1);
        auditRows.Count(r => r.EventType == "permission-change").Should().Be(2);
        auditRows.Where(r => r.EventType == "permission-change")
            .Should().Contain(r => ReadGrantRolePermissionId(r.Detail) == grantToRevoke);
        auditRows.Where(r => r.EventType == "permission-change")
            .Should().Contain(r => ReadGrantRolePermissionId(r.Detail) == addedGrant);

        var occurredAt = await ReadAnyAuditOccurredAtAsync();
        occurredAt.Should().NotBe(default);
    }

    // Pins the CONTENT of a Save-path audit row for either revoke
    // branch. Follows the same read-the-`detail`-column-and-parse precedent as
    // CloseRoleDeclarationAsync's own audit tests above, applied to Save's per-grant JSON shape
    // (RoleDeclarationService.BuildDetailJson's private `AuditDetail` record: action/roleId/
    // rolePermissionId/functionId/scopeLevel/from/to/note, camelCase).

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokeRetireBranch_AuditRowRecordsActionRevokeAndActualPersistedEnd()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F4-T1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("F4.Fn.T1", OpenFrom2020);

        var grantToRevoke = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "F4-T1", "Vai trò",
            grantIdentityIdsToRevoke: [grantToRevoke]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var detail = FindGrantAuditDetail(await ReadAuditRowsForTargetAsync($"role:{role}"), grantToRevoke);
        using var json = JsonDocument.Parse(detail);
        json.RootElement.GetProperty("action").GetString().Should().Be(
            "revoke", "R1: a grant whose own EffectiveFrom is in the past must Retire, audited as action \"revoke\"");
        json.RootElement.GetProperty("to").GetString().Should().Be(
            Today.AddDays(-1).ToString("yyyy-MM-dd"),
            "the audit row must report the end ACTUALLY PERSISTED (today - 1, inclusive-end convention), not the raw revoke-request date");
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokeCancelPlanBranch_AuditRowRecordsActionCancelAndOriginalPeriod()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F4-T2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("F4.Fn.T2", OpenFrom2020);

        // The grant's own coverage starts TODAY — R2's Cancel-plan branch.
        var todayOpen = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var grantToRevoke = await CreateGrantAsync(role, function, todayOpen, ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "F4-T2", "Vai trò",
            grantIdentityIdsToRevoke: [grantToRevoke]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var detail = FindGrantAuditDetail(await ReadAuditRowsForTargetAsync($"role:{role}"), grantToRevoke);
        using var json = JsonDocument.Parse(detail);
        json.RootElement.GetProperty("action").GetString().Should().Be(
            "cancel", "R2: a grant whose own EffectiveFrom is today or later must Cancel-plan, audited as action \"cancel\"");
        json.RootElement.GetProperty("from").GetString().Should().Be(
            Today.ToString("yyyy-MM-dd"), "nothing was closed — the audit row reports the grant's ORIGINAL EffectiveFrom");
        json.RootElement.GetProperty("to").GetString().Should().Be(
            EffectivePeriod.OpenEnd.ToString("yyyy-MM-dd"),
            "nothing was closed — the audit row reports the grant's ORIGINAL EffectiveTo, not a truncated one");
    }

    // =========================================================================================
    // AC10 — P7 denial returns before any service-owned repository work or identity-header mint.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_AuthorizationDenied_ReturnsBeforeAnyWrite()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-P7", "Vai trò", OpenFrom2020);
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));

        var request = await EditSaveRequestAsync(role, "B2-P7", "Vai trò đã sửa");

        var recordingConnections = new RecordingConnectionFactory(Connections);
        var service = new RoleDeclarationService(
            RoleRepo, GrantRepo, FunctionRepo, recordingConnections, new AuditLogWriter(),
            denied, new FakeBreakGlassPolicy(), new FakeCurrentWindowsUser(Actor), new FixedBusinessDateProvider(Today));

        var result = await service.SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        (await CountAllRowsAsync("audit_log")).Should().Be(0);
        recordingConnections.CallCount.Should().Be(
            0, "P7 denial must short-circuit before CompositeWrite opens a connection (no identity lock, no admin-flag lock)");
    }

    // Discriminator for the P7-before-any-write invariant the Edit pin above cannot see: a NewRole with a
    // grant-to-add mints a `role` header (ResolveNewRoleIdentityAsync) and a `role_permission` header (the
    // grant-add loop) — both inside `composite.ExecuteAsync` since 2026-08-17. If AuthorizeAsync were ever
    // moved after the composite begins, those writes would run for an unauthorized actor. The Edit fixture
    // has an existing role and empty GrantsToAdd, so no mint happens there and the pin stays green.
    [Fact]
    public async Task SaveRoleDeclarationAsync_AuthorizationDenied_NewRoleWithGrants_MintsNoHeaders()
    {
        SkipUnlessDbAvailable();

        var function = await CreateFunctionAsync("B2.Fn.P7New", OpenFrom2020);
        var roleHeadersBefore = await CountAllRowsAsync("role");
        var grantHeadersBefore = await CountAllRowsAsync("role_permission");

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));

        var request = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(),
            "B2-P7-NEW",
            "Vai trò mới",
            false,
            null,
            [],
            [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "would mint")]);

        var recordingConnections = new RecordingConnectionFactory(Connections);
        var recordingScopeFilter = new StandardScopeFilterBuilder();
        var recordingResolver = new EffectivePeriodResolver();
        var recordingPeriodEditor = new PeriodEditor();
        var recordingFkRegistry = IamTemporalFkEdges.CreateRegistry();
        var recordingDates = new FixedBusinessDateProvider(Today);
        var recordingRoleRepo = new RoleRepository(
            recordingConnections, recordingScopeFilter, recordingResolver, recordingPeriodEditor,
            FkValidator, recordingFkRegistry, recordingDates);
        var recordingGrantRepo = new RolePermissionRepository(
            recordingConnections, recordingScopeFilter, recordingResolver, recordingPeriodEditor,
            FkValidator, recordingFkRegistry, recordingDates);
        var recordingFunctionRepo = new FunctionRepository(
            recordingConnections, recordingScopeFilter, recordingResolver, recordingPeriodEditor,
            FkValidator, recordingFkRegistry, recordingDates);
        var service = new RoleDeclarationService(
            recordingRoleRepo, recordingGrantRepo, recordingFunctionRepo, recordingConnections, new AuditLogWriter(),
            denied, new FakeBreakGlassPolicy(), new FakeCurrentWindowsUser(Actor), new FixedBusinessDateProvider(Today));

        var result = await service.SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        result.FirstError.Code.Should().Be("Authz.NotGranted");
        using (new AssertionScope())
        {
            (await CountAllRowsAsync("role")).Should().Be(
                roleHeadersBefore, "P7 denial must not mint a role header");
            (await CountAllRowsAsync("role_permission")).Should().Be(
                grantHeadersBefore, "P7 denial must not mint a role_permission header");
        }

        (await CountAllRowsAsync("audit_log")).Should().Be(0);
        recordingConnections.CallCount.Should().Be(
            0, "P7 denial: no service-owned repository work and no identity-header mint begins before authorisation succeeds.");
    }

    // =========================================================================================
    // FR1 — a revoke id must actually belong to the role being declared; cross-role revoke is rejected,
    // nothing written, no audit row (fix round 1).
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokeGrantBelongingToDifferentRole_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var ownerRole = await CreateRoleAsync("B2-FR1-OWNER", "Vai trò sở hữu", OpenFrom2020);
        var otherRole = await CreateRoleAsync("B2-FR1-OTHER", "Vai trò khác", OpenFrom2020);
        var function = await CreateFunctionAsync("B2.Fn.FR1", OpenFrom2020);

        var foreignGrant = await CreateGrantAsync(otherRole, function, OpenFrom2020, ScopeLevel.Global);

        var ownerRoleRowsBefore = await CountRowsAsync("role_version", "role_id", ownerRole);

        // This test is about the FR1 cross-role-revoke rejection.
        var request = await EditSaveRequestAsync(
            ownerRole, "B2-FR1-OWNER", "Vai trò đã sửa",
            grantIdentityIdsToRevoke: [foreignGrant]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.NotOwnedByRole");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);

        (await CountRowsAsync("role_version", "role_id", ownerRole)).Should().Be(
            ownerRoleRowsBefore, "the role edit must not be committed when the revoke cross-check fails");

        var foreignGrantStill = await RolePermissions.GetByIdentityAsync(foreignGrant, Today);
        foreignGrantStill.IsError.Should().BeFalse(DescribeErrors(foreignGrantStill.Errors));
        foreignGrantStill.Value.EffectiveTo.Should().Be(
            EffectivePeriod.OpenEnd, "the foreign grant must not have been closed");

        (await CountAllRowsAsync("audit_log")).Should().Be(
            0, "no audit row may be written for a revoke that was rejected before any write");
    }

    // =========================================================================================
    // FR2 — a forced failure alongside a revoke rolls the revoke's effective_to UPDATE back too (AC2's
    // revoke-path rollback was previously unproven).
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_FailureOnAdd_WithRevokeInSameRequest_RevokeIsRolledBack()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-FR2", "Vai trò gốc", OpenFrom2020);
        var revokedFunction = await CreateFunctionAsync("B2.Fn.FR2Revoked", OpenFrom2020);
        // Deliberately narrower than the requested grant period below — a REAL business reason (STRICT
        // temporal-FK, D8) for the add to fail, not a synthetic one.
        var narrowFunction = await CreateFunctionAsync("B2.Fn.FR2Narrow", Year2021);

        var grantToRevoke = await CreateGrantAsync(role, revokedFunction, OpenFrom2020, ScopeLevel.Global);

        // This test is about the revoke's effective_to UPDATE rolling back with the failed add.
        var request = await EditSaveRequestAsync(
            role, "B2-FR2", "Vai trò đã sửa",
            grantIdentityIdsToRevoke: [grantToRevoke],
            grantsToAdd: [new RolePermissionGrantToAdd(narrowFunction, ScopeLevel.Global, "fails")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");

        var revokedGrantAfterRollback = await RolePermissions.GetByIdentityAsync(grantToRevoke, Today);
        revokedGrantAfterRollback.IsError.Should().BeFalse(DescribeErrors(revokedGrantAfterRollback.Errors));
        revokedGrantAfterRollback.Value.EffectiveTo.Should().Be(
            EffectivePeriod.OpenEnd,
            "the revoke's effective_to UPDATE must roll back together with the failed add, proving Revoke shares the composite transaction");
    }

    // =========================================================================================
    // FR6 — an actor authorized only at a narrower scope than Global must be rejected; Role is a
    // system-wide (Global-only) entity.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_ActorAuthorizedOnlyAtNarrowerScope_IsRejected_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-FR6", "Vai trò", OpenFrom2020);
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        var narrowScope = new FakeAuthorizationService(new DataScope(ScopeLevel.OwnOrgUnit, 1, Actor));

        var request = await EditSaveRequestAsync(role, "B2-FR6", "Vai trò đã sửa");

        var result = await BuildService(authorization: narrowScope).SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.ScopeInsufficient");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        (await CountAllRowsAsync("audit_log")).Should().Be(0);
    }

    // =========================================================================================
    // FR7 — pins current behavior: revoking a not-yet-effective (future-dated) grant fails with
    // EffectivePeriod.NoCoverage (resolved by TODAY, D5) rather than a misleading generic error.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokeFutureDatedGrant_FailsWithNoCoverage()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-FR7", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("B2.Fn.FR7", OpenFrom2020);

        var futurePeriod = new EffectivePeriod(Today.AddDays(30), EffectivePeriod.OpenEnd);
        var futureGrant = await CreateGrantAsync(role, function, futurePeriod, ScopeLevel.Global);

        // This test is about revoking a future-dated grant (NoCoverage), not about the period.
        var request = await EditSaveRequestAsync(
            role, "B2-FR7", "Vai trò đã sửa",
            grantIdentityIdsToRevoke: [futureGrant]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("EffectivePeriod.NoCoverage");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);
    }

    // =========================================================================================
    // Brief 060 — CloseRoleDeclarationAsync: Close (retire) a version whose EffectiveFrom is strictly
    // before today, or Cancel-plan one whose EffectiveFrom is today or later, both through the SERVICE
    // (never IRoleRepository directly).
    // =========================================================================================

    [Fact]
    public async Task CloseRoleDeclarationAsync_RetireCurrentlyEffectiveNonAdminRole_NoDependents_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-R1", "Vai trò", OpenFrom2020);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var remnant = await Roles.GetByIdentityAsync(role, Today.AddDays(-1));
        remnant.IsError.Should().BeFalse(DescribeErrors(remnant.Errors));
        remnant.Value.EffectiveTo.Should().Be(Today.AddDays(-1));
        (await Roles.GetByIdentityAsync(role, Today)).IsError.Should().BeTrue(
            "the retired role must no longer be effective from today");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_RetireBlockedWhenUserStillAssigned_SurfacesTemporalFkDependentsUncovered()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-R2", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("B060ORG", "Đơn vị", "B060ORG", null, OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        (await Users.UpsertAsync(user, OpenFrom2020, "b060.u", "U", org, role, "tester", "seed"))
            .IsError.Should().BeFalse();

        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var userRowsBefore = await CountRowsAsync("user_version", "user_id", user);

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");
        (await CountRowsAsync("user_version", "user_id", user)).Should().Be(
            userRowsBefore, "user_version must not be mutated when the role retire is BLOCKed through the service");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_RetireAutoCutsExclusivelyOwnedGrant_ThroughService()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-R3", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("B060.Fn.One", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var cutAt = Today.AddDays(-1);

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        // P11: the reverse-FK check must NOT block here — the grant is cut first, in the SAME transaction,
        // proving the service delegates to the SAME auto-cutting engine as a direct RoleRepository.CloseVersionAsync call.
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Errors.Should().NotContain(e => e.Code == "TemporalFk.DependentsUncovered");

        var grantAtCut = await RolePermissions.GetByIdentityAsync(grant, cutAt);
        grantAtCut.IsError.Should().BeFalse(DescribeErrors(grantAtCut.Errors));
        grantAtCut.Value.EffectiveTo.Should().Be(cutAt);
        (await RolePermissions.GetByIdentityAsync(grant, cutAt.AddDays(1))).IsError.Should().BeTrue(
            "the exclusively-owned grant must not survive the parent role it belongs to");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_FutureDatedVersion_CancelsPlanInsteadOfRetiring()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-R4", "Vai trò hiện tại", OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
        var futureVersionId = await InsertRoleVersionAsync(
            role, "B060-R4", "Kế hoạch 2027", futurePlan);

        // Server derives Cancel from the version's own EffectiveFrom (2027, strictly after business Today) —
        // the request never says which branch to take. EffectiveThrough must be null on this branch.
        var request = new CloseRoleDeclarationRequest(role, futureVersionId, "hủy kế hoạch");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var history = await Roles.GetHistoryAsync(role);
        var targetRow = history.Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        targetRow.IsActive.Should().BeFalse();
        targetRow.Status.Should().Be(VersionLifecycleStatus.Cancelled,
            "the future-dated branch must go through RoleRepository.CancelPlanAsync (sets status='cancelled'), not CloseVersionAsync");
    }

    // TASK 0 (2026-08-11) — Step 1, RED-first: pins design-effective-period.md §3 ("a single business
    // operation captures D ... ONCE, used consistently for every parameter within that operation") on
    // the cancel path. Pre-fix, CloseRoleDeclarationAsync reads `dates.Today` ONCE to derive the
    // Retire-vs-CancelPlan branch, then RoleRepository.CancelPlanAsync -> the base
    // VersionedRepository.CancelVersionCoreAsync independently RE-READS its own injected
    // IBusinessDateProvider for the cancel-eligibility guard -- 2 reads of "today" for ONE operation.
    // AdvancingBusinessDateProvider, shared between the service and a fresh RoleRepository built for
    // this test only, returns `Today` on the FIRST read and `Today.AddDays(1)` on every read after --
    // simulating a midnight rollover landing exactly between those two reads. The target version starts
    // exactly `Today` (open-ended) -- cancellable per D1 (`>=`) using the FIRST read, but the engine's
    // own SECOND (rolled-over) read would see it as "already started" and wrongly BLOCK with
    // VersionedRepository.NotAFuturePlan. Before the fix this test is RED for that reason; after the fix
    // (the engine takes the caller-captured `operationDate` instead of re-reading its own clock) it is
    // GREEN and the cancel persists (status = 'cancelled', isactive = 0).
    [Fact]
    public async Task CloseRoleDeclarationAsync_MidnightRolloverBetweenServiceAndEngineReads_CancelSucceeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-ROLL", "Vai trò hiện tại", OpenFrom2020);
        long newVersionId = 0;
        var seed = await new CompositeWrite(Connections).Enlist(Roles, role)
            .ExecuteAsync(async context =>
            {
                var write = await Roles.UpsertAsync(
                    context, role, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "B060-ROLL", "Kế hoạch hôm nay",
                    isAdminRole: false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit,
                    new OperationDate(Today), Actor, "plan");
                if (write.IsError)
                {
                    return write.Errors;
                }

                newVersionId = write.Value.NewVersionId;
                return Result.Success;
            });
        seed.IsError.Should().BeFalse(DescribeErrors(seed.Errors));

        var advancing = new AdvancingBusinessDateProvider(Today);
        var rolloverRoleRepo = new RoleRepository(
            Connections, new StandardScopeFilterBuilder(), new EffectivePeriodResolver(), new PeriodEditor(),
            FkValidator, IamTemporalFkEdges.CreateRegistry(), advancing);
        var service = new RoleDeclarationService(
            rolloverRoleRepo, GrantRepo, FunctionRepo, Connections, new AuditLogWriter(),
            new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, Actor)), new FakeBreakGlassPolicy(),
            new FakeCurrentWindowsUser(Actor), advancing);

        var request = new CloseRoleDeclarationRequest(role, newVersionId, "hủy kế hoạch");

        var result = await service.CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.NotAFuturePlan");

        var history = await Roles.GetHistoryAsync(role);
        var targetRow = history.Should().ContainSingle(h => h.Id == newVersionId).Subject;
        targetRow.IsActive.Should().BeFalse();
        targetRow.Status.Should().Be(VersionLifecycleStatus.Cancelled);
    }

    // Guard branch not explicitly enumerated in the brief (server-side version lookup can miss) — rule-testing invariant 6.
    [Fact]
    public async Task CloseRoleDeclarationAsync_VersionIdNotFound_ReturnsClearNotFound()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-R6", "Vai trò", OpenFrom2020);

        var request = new CloseRoleDeclarationRequest(role, VersionId: 999_999_999, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.VersionNotFound");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);
    }

    // =========================================================================================
    // review fix round (F2/F3/F4) — resolve the target from ACTIVE versions only, and validate
    // the close date against both "yesterday (today - 1) or later" and the target version's own
    // effective period.
    // =========================================================================================

    // F3 (Task A): re-submitting an already-cancelled version id must return the SERVICE's own
    // Role.VersionNotFound, not fall through and let the engine surface a different code for a
    // pre-existing id (VersionedRepository.VersionNotFound).
    [Fact]
    public async Task CloseRoleDeclarationAsync_VersionAlreadyCancelled_ReturnsServiceOwnedVersionNotFound_LeavesStateUnchanged()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060F3-R1", "Vai trò hiện tại", OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
        var futureVersionId = await InsertRoleVersionAsync(
            role, "B060F3-R1", "Kế hoạch 2027", futurePlan);

        var cancelRequest = new CloseRoleDeclarationRequest(role, futureVersionId, "cancel");
        (await BuildService().CloseRoleDeclarationAsync(cancelRequest)).IsError.Should().BeFalse();

        // Re-submit the SAME already-cancelled version id.
        var retryRequest = new CloseRoleDeclarationRequest(role, futureVersionId, "retry cancel");
        var result = await BuildService().CloseRoleDeclarationAsync(retryRequest);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.VersionNotFound");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);

        var historyAfter = await Roles.GetHistoryAsync(role);
        var targetRow = historyAfter.Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        targetRow.Status.Should().Be(VersionLifecycleStatus.Cancelled, "the version's cancelled state must remain unchanged by the rejected re-submission");
        targetRow.IsActive.Should().BeFalse();
    }

    // F3 (Task A): the same resolve restriction for an already-superseded (inactive, not cancelled)
    // version id — an exact-match edit deactivates the old version without cancelling it.
    [Fact]
    public async Task CloseRoleDeclarationAsync_VersionAlreadySuperseded_ReturnsServiceOwnedVersionNotFound()
    {
        SkipUnlessDbAvailable();

        // Seed a version that already starts today so the save (which derives [today, OpenEnd]) is an
        // exact-match re-edit and supersedes it (isactive=0, status='normal'). A 2020 seed would instead be
        // closed at yesterday and left active, which is not the superseded-id surface this test pins.
        var role = await CreateRoleAsync("B060F3-R2", "Vai trò", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var originalVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        // Exact-match re-edit fully replaces the original version — isactive=0, status='normal'.
        var edit = await EditSaveRequestAsync(role, "B060F3-R2", "Vai trò đã sửa", reason: "edit");
        (await BuildService().SaveRoleDeclarationAsync(edit)).IsError.Should().BeFalse();

        var historyBeforeRetry = await Roles.GetHistoryAsync(role);
        var originalRow = historyBeforeRetry.Should().ContainSingle(h => h.Id == originalVersionId).Subject;
        originalRow.IsActive.Should().BeFalse("the exact-match edit must have superseded the original version");
        originalRow.Status.Should().Be(VersionLifecycleStatus.Normal, "superseded is a different concept than cancelled");

        var request = new CloseRoleDeclarationRequest(role, originalVersionId, "retire stale");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.VersionNotFound");
        result.FirstError.Type.Should().Be(ErrorType.NotFound);

        var historyAfter = await Roles.GetHistoryAsync(role);
        var originalRowAfter = historyAfter.Should().ContainSingle(h => h.Id == originalVersionId).Subject;
        originalRowAfter.EffectiveTo.Should().Be(originalRow.EffectiveTo, "the rejected request must not touch the superseded version's period");
    }

    // F3-class error surface (Task C): a close date outside the TARGET version's own effective period
    // must fail at the service with a service-owned code, not reach the engine and surface
    // VersionedRepository.InvalidShrink.
    // Note: an ALREADY-ENDED active version (effective_to in the past) must be rejected with
    // its OWN code stating the real reason, not the date-range guard (the two guards are jointly
    // unsatisfiable for an expired version, so the range guard would previously fire with a misleading
    // message). Was CloseRoleDeclarationAsync_CloseDateOutsideVersionPeriod_IsRejected_TargetUnchanged —
    // renamed to reflect the corrected error surface for this exact scenario.
    [Fact]
    public async Task CloseRoleDeclarationAsync_VersionAlreadyEnded_IsRejected_TargetUnchanged()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060F3C-R1", "Vai trò", Year2021);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, new DateOnly(2021, 6, 1))).Value.Id;

        // Today (2026-07-03) is already past Year2021's own effective_to (2021-12-31).
        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire outside period");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.VersionAlreadyEnded);
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        var unchanged = await Roles.GetByIdentityAsync(role, new DateOnly(2021, 6, 1));
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(new DateOnly(2021, 12, 31), "a rejected close on an already-ended version must not touch effective_to");
    }

    // =========================================================================================
    // F2 (Task D) — a successful Close/Cancel writes an audit_log row recording the ACTOR; a guard
    // blocking inside the composite transaction leaves no audit_log row.
    // The two tests below use roles with NO grants, which is why they see exactly ONE row. That is the
    // no-cascade case, not the general rule: since B1/B3b (2026-08-15) a stop writes 1 + N rows — one
    // for the stop plus one per grant the P11 cascade cut or cancelled, all sharing one operationId.
    // The general shape is covered by the `Close_WithTwoAffectedGrants_*` / `Close_ChildEvents_*` tests.
    // =========================================================================================

    [Fact]
    public async Task CloseRoleDeclarationAsync_SuccessfulClose_WritesOneAuditLogRowForActor()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060F2-R1", "Vai trò", OpenFrom2020);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountAllRowsAsync("audit_log")).Should().Be(1, "exactly one audit_log row must be written for a successful Close");
        // B3b (2026-08-15): the target moved from `role_version:{id}` to `role:{roleId}` -- the journal
        // is opened per role, and the precise version already lives in `detail`.
        var closeRows = await ReadAuditRowsForTargetAsync($"role:{role}");
        closeRows.Should().HaveCount(1);
        closeRows.Single().Username.Should().Be(Actor, "the audit row must record the actor who performed the close");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_CancelFuturePlanWithNoAdjacentPredecessor_WritesAuditRowForCanceller_NotCreator()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060F2-R2", "Vai trò hiện tại", OpenFrom2020);
        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
        var futureVersionId = await InsertRoleVersionAsync(
            role, "B060F2-R2", "Kế hoạch 2027", futurePlan);

        // No adjacent predecessor exists for this plan (OpenFrom2020's version does not end the day
        // before 2027-01-01) — the exact hole F2 identified: the cancel path's ONLY DB write on this
        // no-predecessor branch is `UPDATE role_version SET isactive=0, status='cancelled' ...`, which never
        // touches recorded_by, so without an audit_log row there is ZERO record of who cancelled it.
        const string canceller = "canceller";
        var request = new CloseRoleDeclarationRequest(role, futureVersionId, "cancel plan");

        var result = await BuildService(actor: canceller).CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        // B3b (2026-08-15): the target moved from `role_version:{id}` to `role:{roleId}`.
        var cancelRows = await ReadAuditRowsForTargetAsync($"role:{role}");
        cancelRows.Should().HaveCount(1);
        cancelRows.Single().Username.Should().Be(canceller, "THE DISCRIMINATOR: the audit row must record the CANCELLER, not the plan's creator");

        var historyRow = (await Roles.GetHistoryAsync(role)).Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        historyRow.RecordedBy.Should().Be(
            Actor,
            "the role_version row itself still shows the CREATOR (recorded_by is never updated on a no-predecessor cancel) — " +
            "proving the audit_log row is the ONLY place the canceller's identity is recorded");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_RetireBlockedByTemporalFk_WritesNoAuditLogRow()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060F2-R3", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("B060F2O", "Đơn vị", "B060F2O", null, OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        (await Users.UpsertAsync(user, OpenFrom2020, "b060f2.u", "U", org, role, "tester", "seed"))
            .IsError.Should().BeFalse();

        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await BuildService().CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");

        // THE DISCRIMINATOR: this guard fires INSIDE CloseVersionAsync(context, ...), before the audit
        // write is ever attempted — if the audit write were ever hoisted to run unconditionally, or on a
        // separate auto-committing connection outside the composite transaction, this table would be
        // non-empty despite the BLOCK.
        (await CountAllRowsAsync("audit_log")).Should().Be(0, "a Close blocked by the temporal-FK guard must leave no audit_log row");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_AdminFlaggedRole_NonBreakGlassActor_IsForbidden_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-ADM1", "Vai trò admin", OpenFrom2020, isAdminRole: true);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire admin role");

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy()).CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.AdminFlagChangeNotAuthorized");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_AdminFlaggedRole_BreakGlassActor_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-ADM2", "Vai trò admin", OpenFrom2020, isAdminRole: true);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire admin role");

        var result = await BuildService(breakGlass: new FakeBreakGlassPolicy(Actor)).CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await Roles.GetByIdentityAsync(role, Today.AddDays(1))).IsError.Should().BeTrue(
            "the admin role must be retired the day after the cut when a break-glass actor performs the close");
    }

    // THE DISCRIMINATOR (rule-adversarial-lens "SoT reads & discriminating fixtures"): a call-recording
    // IDbConnectionFactory proves the repository never even opened a connection, not merely that the
    // returned error code looked right — an implementation that read the target version BEFORE checking
    // authorization would still surface the same Forbidden error code by coincidence.
    [Fact]
    public async Task CloseRoleDeclarationAsync_AuthorizationDenied_ShortCircuitsBeforeAnyRepositoryCall()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-P7A", "Vai trò", OpenFrom2020);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var recordingConnections = new RecordingConnectionFactory(Connections);
        var recordingRoleRepo = new RoleRepository(
            recordingConnections, new StandardScopeFilterBuilder(), new EffectivePeriodResolver(), new PeriodEditor(),
            FkValidator, IamTemporalFkEdges.CreateRegistry(), new FixedBusinessDateProvider(Today));

        var denied = new FakeAuthorizationService(Error.Forbidden("Authz.NotGranted", "Không được cấp quyền."));
        var service = new RoleDeclarationService(
            recordingRoleRepo, GrantRepo, FunctionRepo, Connections, new AuditLogWriter(),
            denied, new FakeBreakGlassPolicy(), new FakeCurrentWindowsUser(Actor), new FixedBusinessDateProvider(Today));

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await service.CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        recordingConnections.CallCount.Should().Be(
            0, "P7 denial must short-circuit before the repository ever opens a connection to read the target version");
    }

    [Fact]
    public async Task CloseRoleDeclarationAsync_ActorAuthorizedOnlyAtNarrowerScope_ShortCircuitsBeforeAnyRepositoryCall()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B060-P7B", "Vai trò", OpenFrom2020);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var recordingConnections = new RecordingConnectionFactory(Connections);
        var recordingRoleRepo = new RoleRepository(
            recordingConnections, new StandardScopeFilterBuilder(), new EffectivePeriodResolver(), new PeriodEditor(),
            FkValidator, IamTemporalFkEdges.CreateRegistry(), new FixedBusinessDateProvider(Today));

        var narrowScope = new FakeAuthorizationService(new DataScope(ScopeLevel.OwnOrgUnit, 1, Actor));
        var service = new RoleDeclarationService(
            recordingRoleRepo, GrantRepo, FunctionRepo, Connections, new AuditLogWriter(),
            narrowScope, new FakeBreakGlassPolicy(), new FakeCurrentWindowsUser(Actor), new FixedBusinessDateProvider(Today));

        var request = new CloseRoleDeclarationRequest(role, roleVersionId, "retire");

        var result = await service.CloseRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Authz.ScopeInsufficient");
        recordingConnections.CallCount.Should().Be(
            0, "scope-insufficient must short-circuit before the repository ever opens a connection to read the target version");
    }

    // =========================================================================================
    // B3b (slice 2, Task 3) — one gesture, one operation id. A role stop writes ONE parent
    // `role-close` event plus one `permission-change` child event per P11 auto-cut outcome, all at
    // target `role:{roleId}`, all sharing one operation id in `detail`. The child events distinguish
    // a grant that was CUT (had effective days behind it) from one that was CANCELLED (never took
    // effect) -- reading the engine's reported AutoCutAction, never re-deriving it from dates.
    // =========================================================================================

    [Fact]
    public async Task Close_WithTwoAffectedGrants_WritesParentAndTwoChildEventsSharingOneOperationId()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("B3B-R1", "Vai trò đang chạy", monthAgo);
        var shrunkFunction = await CreateFunctionAsync("B3b.Fn.Shrunk1", monthAgo);
        var cancelledFunction = await CreateFunctionAsync("B3b.Fn.Cancelled1", monthAgo);
        await CreateGrantAsync(
            role, shrunkFunction, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        await CreateGrantAsync(
            role, cancelledFunction, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, target.Value.Id, "đóng vai trò"));
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var rows = await ReadAuditRowsForTargetAsync($"role:{role}");
        rows.Should().HaveCount(3, "one parent stop plus one event per affected grant");

        var parent = rows.Should().ContainSingle(r => r.EventType == "role-close").Subject;
        var operationId = ReadOperationId(parent.Detail);
        operationId.Should().NotBeNullOrWhiteSpace();

        rows.Where(r => r.EventType == "permission-change").Should().HaveCount(2);
        rows.Should().OnlyContain(
            r => ReadOperationId(r.Detail) == operationId,
            "every row of one gesture carries the same operation id, so the journal groups with no timestamp heuristic");

        // Discriminator: "same id" is also satisfied by a CONSTANT id. A second, unrelated stop must
        // not join the first gesture's group.
        var otherRole = await CreateRoleAsync("B3B-R2", "Vai trò khác", OpenFrom2020);
        var otherTarget = await Roles.GetByIdentityAsync(otherRole, Today);
        otherTarget.IsError.Should().BeFalse(DescribeErrors(otherTarget.Errors));

        var otherClose = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(otherRole, otherTarget.Value.Id, "đóng vai trò khác"));
        otherClose.IsError.Should().BeFalse(DescribeErrors(otherClose.Errors));

        var otherRows = await ReadAuditRowsForTargetAsync($"role:{otherRole}");
        ReadOperationId(otherRows.Single(r => r.EventType == "role-close").Detail)
            .Should().NotBe(operationId, "each gesture mints its own id -- a constant would merge two operations into one journal entry");
    }

    [Fact]
    public async Task Close_ChildEvents_RecordShrunkAndCancelledDistinctly()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("B3B-R3", "Vai trò đang chạy", monthAgo);
        var shrunkFunction = await CreateFunctionAsync("B3b.Fn.Shrunk2", monthAgo);
        var cancelledFunction = await CreateFunctionAsync("B3b.Fn.Cancelled2", monthAgo);
        var shrunkGrant = await CreateGrantAsync(
            role, shrunkFunction, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var cancelledGrant = await CreateGrantAsync(
            role, cancelledFunction, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var closedVersionId = target.Value.Id;

        var close = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, closedVersionId, "đóng vai trò"));
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var rows = await ReadAuditRowsForTargetAsync($"role:{role}");

        // Assert every field slice 3 will render -- an operation-id-only test lets a wrong parent
        // branch, a wrong grant identity or a wrong period reach slice 3 undetected.
        var parent = rows.Single(r => r.EventType == "role-close");
        var parentDetail = ParseDetail(parent.Detail);
        parentDetail.Action.Should().Be("close", "the role had been in force, so this is a close, not a cancel");
        parentDetail.RoleId.Should().Be(role);
        parentDetail.RoleVersionId.Should().Be(closedVersionId);
        parentDetail.EffectiveThrough.Should().Be(Today.AddDays(-1));

        var children = rows.Where(r => r.EventType == "permission-change").Select(r => ParseDetail(r.Detail)).ToList();

        var cut = children.Should().ContainSingle(d => d.Action == "cut").Subject;
        cut.RolePermissionId.Should().Be(shrunkGrant);
        cut.FunctionId.Should().Be(shrunkFunction);
        cut.To.Should().Be(Today.AddDays(-1), "a cut grant's event reports the period that SURVIVED");

        var cancelled = children.Should().ContainSingle(d => d.Action == "cancel").Subject;
        cancelled.RolePermissionId.Should().Be(cancelledGrant);
        cancelled.From.Should().Be(Today);
        cancelled.To.Should().Be(EffectivePeriod.OpenEnd, "nothing survived, so the event reports what was lost");
    }

    // 2026-08-15: the "different id" proof above uses two DIFFERENT roles, so
    // `operationId = roleId.ToString()` passes it -- and would then merge every gesture on ONE role
    // into a single journal entry, which is the exact defect the operation id exists to prevent.
    // Two gestures on the SAME identity must still mint different ids.
    [Fact]
    public async Task TwoGesturesOnTheSameRole_MintDifferentOperationIds()
    {
        SkipUnlessDbAvailable();

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B3B-R6", "Vai trò một danh tính", false, null, [], []));
        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));
        var role = save.Value.RoleId;

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, target.Value.Id, "đóng vai trò"));
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var rows = await ReadAuditRowsForTargetAsync($"role:{role}");
        var saveId = ReadOperationId(rows.Single(r => r.EventType == "role-change").Detail);
        var stopId = ReadOperationId(rows.Single(r => r.EventType == "role-close").Detail);

        saveId.Should().NotBeNullOrWhiteSpace();
        stopId.Should().NotBeNullOrWhiteSpace();
        stopId.Should().NotBe(saveId,
            "two gestures on ONE role identity are two journal entries -- an id derived from the role id would merge them");
    }

    // An audit write that fails must take the version write down with it -- a stop that is not
    // journalled is worse than a stop that did not happen, because nothing will ever show it.
    [Fact]
    public async Task Close_WhenAChildAuditWriteFails_RollsBackTheWholeStop()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("B3B-R4", "Vai trò đang chạy", monthAgo);
        var shrunkFunction = await CreateFunctionAsync("B3b.Fn.Shrunk3", monthAgo);
        var cancelledFunction = await CreateFunctionAsync("B3b.Fn.Cancelled3", monthAgo);
        var shrunkGrant = await CreateGrantAsync(
            role, shrunkFunction, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var cancelledGrant = await CreateGrantAsync(
            role, cancelledFunction, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var closedVersionId = target.Value.Id;

        var roleVersionRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var shrunkGrantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", shrunkGrant);

        // The existing seam does NOT work here: AlwaysFailingAuditLogWriter fails
        // the FIRST write, which is the parent -- so a service that swallowed a CHILD error would stay
        // green. FailingOnNthAuditLogWriter succeeds until `failOnCall`, letting the parent row through
        // and failing the first CHILD.
        var writer = new FailingOnNthAuditLogWriter(new AuditLogWriter(), failOnCall: 2);
        var service = CreateServiceWithAuditWriter(writer);

        var close = await service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, closedVersionId, "đóng vai trò"));

        close.IsError.Should().BeTrue();
        close.Errors.Should().Contain(e => e.Code == "Audit.WriteFailed",
            "the child audit failure is what must surface -- not some later symptom of it");
        writer.CallCount.Should().Be(2, "the parent row was written and the FIRST CHILD is what failed");

        (await ReadAuditRowsForTargetAsync($"role:{role}")).Should().BeEmpty(
            "the parent audit row rolled back with the child that failed -- a gesture is all or nothing");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(
            roleVersionRowsBefore, "no remnant, no new version: the version write rolled back too");
        (await QueryVersionStateAsync("role_version", closedVersionId)).EffectiveTo.Should().Be(
            EffectivePeriod.OpenEnd, "the targeted version is untouched, not merely un-remnanted");
        (await CountCancelledAsync("role_permission_version", "role_permission_id", cancelledGrant)).Should().Be(
            0, "the cascade rolled back too");
        (await CountRowsAsync("role_permission_version", "role_permission_id", shrunkGrant)).Should().Be(
            shrunkGrantRowsBefore, "and the shrink's remnant is gone with it");
    }

    // 2026-08-15: failing the FIRST child only proves the loop's first iteration is checked. An
    // implementation that handles child 1 and swallows child 2 still passes that test and would commit a
    // parent plus a partial cascade -- a journal that lies by omission. Fail the SECOND child too.
    [Fact]
    public async Task Close_WhenTheSecondChildAuditWriteFails_RollsBackTheWholeStop()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("B3B-R7", "Vai trò đang chạy", monthAgo);
        var shrunkFunction = await CreateFunctionAsync("B3b.Fn.Shrunk4", monthAgo);
        var cancelledFunction = await CreateFunctionAsync("B3b.Fn.Cancelled4", monthAgo);
        var shrunkGrant = await CreateGrantAsync(
            role, shrunkFunction, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var cancelledGrant = await CreateGrantAsync(
            role, cancelledFunction, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var closedVersionId = target.Value.Id;

        var roleVersionRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var shrunkGrantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", shrunkGrant);

        var writer = new FailingOnNthAuditLogWriter(new AuditLogWriter(), failOnCall: 3);
        var service = CreateServiceWithAuditWriter(writer);

        var close = await service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, closedVersionId, "đóng vai trò"));

        close.IsError.Should().BeTrue();
        close.Errors.Should().Contain(e => e.Code == "Audit.WriteFailed");
        writer.CallCount.Should().Be(3, "the parent and the first child were written and the SECOND CHILD is what failed");

        (await ReadAuditRowsForTargetAsync($"role:{role}")).Should().BeEmpty(
            "the parent AND the child that succeeded both rolled back -- a partially journalled gesture is the defect");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleVersionRowsBefore);
        (await QueryVersionStateAsync("role_version", closedVersionId)).EffectiveTo.Should().Be(
            EffectivePeriod.OpenEnd, "the targeted version is untouched");
        (await CountCancelledAsync("role_permission_version", "role_permission_id", cancelledGrant)).Should().Be(0);
        (await CountRowsAsync("role_permission_version", "role_permission_id", shrunkGrant)).Should().Be(
            shrunkGrantRowsBefore);
    }

    private sealed class FailingOnNthAuditLogWriter(IAuditLogWriter inner, int failOnCall) : IAuditLogWriter
    {
        public int CallCount { get; private set; }

        public async Task<ErrorOr<Success>> WriteAsync(
            AuditLogEntry entry, IDbTransaction transaction, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == failOnCall)
            {
                return Error.Failure("Audit.WriteFailed", $"injected failure on call {failOnCall}");
            }

            return await inner.WriteAsync(entry, transaction, cancellationToken);
        }
    }

    // =========================================================================================
    // Constraint: one Save that revokes a grant and re-adds the same function must leave at most
    // one active grant covering today. Retire vs cancel is VersionCloseRules.BranchFor(today,
    // grantPeriod). Every grant-to-add starts on the operation date — Save derives [today, OpenEnd];
    // the request carries no period.
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokePastGrantAndReAddSameFunction_NoRetroactiveDuplicateCoverage()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("HIGH1-T1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("HIGH1.Fn.T1", OpenFrom2020);

        var grantToRevoke = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "HIGH1-T1", "Vai trò",
            grantIdentityIdsToRevoke: [grantToRevoke],
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "re-scope")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var newGrantId = result.Value.AddedRolePermissionIds[0];

        // R4 — THE DISCRIMINATOR: at most one active grant on this function may cover today. A
        // DuplicateGrant conflict here means the revoke-then-readd left two simultaneously-active rows.
        var resolvedToday = await RolePermissions.GetGrantAsync(role, function, Today);
        resolvedToday.IsError.Should().BeFalse(
            "R4: revoking and re-adding a grant on the same function in one Save must never leave two " +
            "simultaneously-active grants covering today (RolePermission.DuplicateGrant)");
        resolvedToday.Value.Id.Should().Be(
            (await RolePermissions.GetByIdentityAsync(newGrantId, Today)).Value.Id,
            "the surviving active grant for today must be the NEW one, not the retired one");

        // R1 — the retired grant's own EffectiveFrom (2020) is in the past, so it must RETIRE: its
        // last effective day becomes today - 1 (it ceases FROM today).
        // Closing a version deactivates the original row and appends a shortened remnant
        // (VersionedRepository.CloseVersionCoreAsync), so the raw timeline holds 2 rows for a retire — the
        // discriminating claim is that exactly ONE of them is still active, and that it ceases from today.
        var retiredHistory = await RolePermissions.GetHistoryAsync(grantToRevoke);
        retiredHistory.Should().HaveCount(2, "pins the append-only shape — the original deactivated row PLUS the shortened remnant");
        var retiredVersion = retiredHistory.Should().ContainSingle(h => h.IsActive).Subject;
        retiredVersion.EffectiveTo.Should().Be(
            Today.AddDays(-1), "R1: a grant whose own EffectiveFrom is in the past must retire, ceasing FROM today");

        // The newly-added grant starts on the operation date (Save derives [today, OpenEnd]).
        var newGrantRow = await RolePermissions.GetByIdentityAsync(newGrantId, Today);
        newGrantRow.IsError.Should().BeFalse(DescribeErrors(newGrantRow.Errors));
        newGrantRow.Value.EffectiveFrom.Should().Be(
            Today, "R3: an added grant must never take effect before today (no retroactive permission)");
        newGrantRow.Value.ScopeLevel.Should().Be(ScopeLevel.Global);
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_RevokeSameDayGrantAndReAdd_CancelsInsteadOfRetiring()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("HIGH1-T2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("HIGH1.Fn.T2", OpenFrom2020);

        // The grant's own coverage starts TODAY — a same-day correction, not an edit of an
        // already-effective grant.
        var todayOpen = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var grantToRevoke = await CreateGrantAsync(role, function, todayOpen, ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "HIGH1-T2", "Vai trò",
            grantIdentityIdsToRevoke: [grantToRevoke],
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "re-scope same day")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var newGrantId = result.Value.AddedRolePermissionIds[0];

        // R2 — THE DISCRIMINATOR: a grant whose own EffectiveFrom == today has not completed a single
        // effective day, so revoking it must CANCEL it outright (status='cancelled', isactive=0), not close it
        // to a date before its own start (there is no such date — VersionClose.CloseDateInPast territory).
        // The revoked identity must therefore have NO row still resolvable today — a cancel-plan never
        // appends a successor "closed" version (mirrors RoleRepository.CancelPlanAsync's no-append shape
        // for the analogous role-close case), unlike a Retire which appends a shortened successor row.
        var retiredHistory = await RolePermissions.GetHistoryAsync(grantToRevoke);
        retiredHistory.Should().Contain(
            h => h.Status == VersionLifecycleStatus.Cancelled && !h.IsActive,
            "R2: a revoked grant whose own EffectiveFrom is today or later must be CANCELLED, not retired to a date before its own start");
        var revokedResolvedToday = await RolePermissions.GetByIdentityAsync(grantToRevoke, Today);
        revokedResolvedToday.IsError.Should().BeTrue(
            "R2: a cancelled grant has zero effective days — it must not still resolve as active on today");

        // R4 — exactly one active grant on this function covers today (the new one).
        var resolvedToday = await RolePermissions.GetGrantAsync(role, function, Today);
        resolvedToday.IsError.Should().BeFalse(
            "R4: no day may carry two active grants for the same (role, function)");
        resolvedToday.Value.Id.Should().Be(
            (await RolePermissions.GetByIdentityAsync(newGrantId, Today)).Value.Id);
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_AddOnlyGrant_StartsToday_NotRolesPastStart()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("HIGH1-T3", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("HIGH1.Fn.T3", OpenFrom2020);

        // Pure Add (no revoke): the grant still starts today, not the role's 2020 seed.
        var request = await EditSaveRequestAsync(
            role, "HIGH1-T3", "Vai trò",
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "new grant")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var newGrantId = result.Value.AddedRolePermissionIds[0];

        var newGrantRow = await RolePermissions.GetByIdentityAsync(newGrantId, Today);
        newGrantRow.IsError.Should().BeFalse(DescribeErrors(newGrantRow.Errors));
        newGrantRow.Value.EffectiveFrom.Should().Be(
            Today, "an added grant must start today, not the role's past EffectiveFrom");
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_AddOnlyGrant_StartsToday_NeverBeforeRolesOwnCoverage()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("HIGH1-T4", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("HIGH1.Fn.T4", OpenFrom2020);

        var request = await EditSaveRequestAsync(
            role, "HIGH1-T4", "Vai trò",
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "new grant")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var newGrantId = result.Value.AddedRolePermissionIds[0];

        var newGrantRow = await RolePermissions.GetByIdentityAsync(newGrantId, Today);
        newGrantRow.IsError.Should().BeFalse(DescribeErrors(newGrantRow.Errors));
        newGrantRow.Value.EffectiveFrom.Should().Be(
            Today, "an added grant must start today, never before the parent role's own coverage");
    }

    // =========================================================================================
    // docs/design-iam-schema.md:232-233 — (role_id, function_id) non-overlap invariant, validated at
    // the app level (never implemented before this fix; only caught at READ time by
    // RolePermissionRepository.GetGrantAsync's RolePermission.DuplicateGrant).
    // =========================================================================================

    [Fact]
    public async Task SaveRoleDeclarationAsync_AddFunctionAlreadyActivelyGranted_ReturnsOverlappingGrant()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("OVL-T1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("OVL.Fn.T1", OpenFrom2020);

        // Existing active grant already covers today (and beyond) — an unrelated Add for the SAME
        // function must never be allowed to create a second, overlapping active grant.
        var existingGrant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "OVL-T1", "Vai trò",
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "duplicate")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.OverlappingGrant");
        result.FirstError.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_TwoAddsSameFunctionInOneRequest_SecondReturnsOverlappingGrant()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("OVL-T2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("OVL.Fn.T2", OpenFrom2020);

        // No pre-existing grant at all — the conflict must be caught between the two IN-REQUEST adds
        // themselves, proving the check runs in-loop (sees its own earlier iteration's write) rather
        // than only up-front against pre-existing rows.
        var request = await EditSaveRequestAsync(
            role, "OVL-T2", "Vai trò",
            grantsToAdd:
            [
                new RolePermissionGrantToAdd(function, ScopeLevel.Global, "first add"),
                new RolePermissionGrantToAdd(function, ScopeLevel.OwnOrgUnit, "second add - conflicts"),
            ]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.OverlappingGrant");
        result.FirstError.Type.Should().Be(ErrorType.Conflict);
    }

    // Bounds discriminators for the overlap SQL predicate (`effective_from <= @to AND @from <=
    // effective_to`): an existing grant ending exactly `today - 1` is ADJACENT to a new grant starting
    // today (must PASS), while one ending exactly `today` OVERLAPS it (must CONFLICT).

    [Fact]
    public async Task SaveRoleDeclarationAsync_AddAfterExistingGrantEndsYesterday_Adjacent_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("OVL-T4", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("OVL.Fn.T4", OpenFrom2020);

        // Existing grant ends yesterday; the add (derived [today, OpenEnd]) is adjacent, not overlapping.
        var existingGrant = await CreateGrantAsync(
            role, function, new EffectivePeriod(OpenFrom2020.From, Today.AddDays(-1)), ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "OVL-T4", "Vai trò",
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "adjacent, not overlapping")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task SaveRoleDeclarationAsync_AddWhileExistingGrantEndsToday_Overlaps_ReturnsOverlappingGrant()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("OVL-T5", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("OVL.Fn.T5", OpenFrom2020);

        // Existing grant ends today; the add (derived [today, OpenEnd]) overlaps on that shared day.
        var existingGrant = await CreateGrantAsync(
            role, function, new EffectivePeriod(OpenFrom2020.From, Today), ScopeLevel.OwnOrgUnit);

        var request = await EditSaveRequestAsync(
            role, "OVL-T5", "Vai trò",
            grantsToAdd: [new RolePermissionGrantToAdd(function, ScopeLevel.Global, "overlaps on today")]);

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.OverlappingGrant");
        result.FirstError.Type.Should().Be(ErrorType.Conflict);
    }

    // Constraint: a save of a role that has run since 2020 derives [today, OpenEnd] and therefore
    // closes the old version at yesterday — last month still resolves the old name.
    [Fact]
    public async Task SaveRoleDeclarationAsync_RenameRoleSeededFromPast_LastMonthKeepsOldName_OldVersionEndsYesterday_TodayHasNewName()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("NB-T2", "Tên cũ", OpenFrom2020);
        var lastMonth = Today.AddMonths(-1);
        var yesterday = Today.AddDays(-1);

        var request = await EditSaveRequestAsync(role, "NB-T2", "Tên mới");

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var past = await Roles.GetByIdentityAsync(role, lastMonth);
        past.IsError.Should().BeFalse(DescribeErrors(past.Errors));
        past.Value.RoleName.Should().Be("Tên cũ");
        past.Value.EffectiveTo.Should().Be(yesterday);

        var current = await Roles.GetByIdentityAsync(role, Today);
        current.IsError.Should().BeFalse(DescribeErrors(current.Errors));
        current.Value.RoleName.Should().Be("Tên mới");
        current.Value.EffectiveFrom.Should().Be(Today);
    }

    // Save of a past-seeded role derives [today, OpenEnd] and succeeds (overlap-head close yesterday).
    [Fact]
    public async Task SaveRoleDeclarationAsync_FromEqualsToday_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("NB-T4", "Vai trò gốc", OpenFrom2020);

        var request = await EditSaveRequestAsync(role, "NB-T4", "Vai trò đã sửa hôm nay");

        var result = await BuildService().SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var edited = await Roles.GetByIdentityAsync(role, Today);
        edited.IsError.Should().BeFalse(DescribeErrors(edited.Errors));
        edited.Value.RoleName.Should().Be("Vai trò đã sửa hôm nay");
    }

    // =========================================================================================
    // B2 / settled item 8 — code resolves to its own identity under a code lock.
    // =========================================================================================

    [Fact]
    public async Task Save_NewRoleWithAClosedRolesCode_ReattachesToTheSameIdentity()
    {
        SkipUnlessDbAvailable();

        var existing = await CreateRoleAsync("B2-R1", "Vai trò cũ",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R1", "Vai trò dùng lại", false, null, [], []));

        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));
        save.Value.RoleId.Should().Be(existing, "the code's one historical owner is reused, not duplicated");
        save.Value.ReattachedToExistingIdentity.Should().BeTrue();
        (await CountRowsAsync("role_version", "role_id", existing)).Should().Be(2,
            "the reattached declaration is a NEW version on the SAME identity -- the gap between them is the point");
    }

    [Fact]
    public async Task Save_NewRoleWithAPaddedCodeOfALiveRole_IsRefused_AndMintsNoSecondIdentity()
    {
        SkipUnlessDbAvailable();

        var live = await CreateRoleAsync("FR21-A", "Vai trò đang chạy", OpenFrom2020);
        var headersBefore = await CountAllRowsAsync("role");

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "  FR21-A  ", "Vai trò khác", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeInUse");
        (await CountAllRowsAsync("role")).Should().Be(headersBefore,
            "a padded spelling of a live code must not mint a second identity for it");
        (await RoleRepo.GetCodeOwnersAsync("FR21-A", Today)).Should().ContainSingle();
    }

    [Fact]
    public async Task Save_NewRoleWithAPaddedUnusedCode_StoresTheTrimmedCodeEverywhere()
    {
        SkipUnlessDbAvailable();

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "  FR31-A  ", "Vai trò mới", false, null, [], []));
        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));

        var stored = await ReadRoleCodeForVersionAsync(save.Value.RoleVersionId);
        stored.Should().Be("FR31-A", "the persisted column is what every later ownership read compares against");

        (await RoleRepo.GetCodeOwnersAsync("FR31-A", Today)).Should().ContainSingle(o => o.RoleId == save.Value.RoleId,
            "a normalised read must find the row this save wrote -- if it cannot, the identity is already orphaned from its own code");

        var roleEvent = (await ReadAuditRowsForTargetAsync($"role:{save.Value.RoleId}"))
            .Single(r => r.EventType == "role-change");
        JsonDocument.Parse(roleEvent.Detail).RootElement.GetProperty("roleCode").GetString()
            .Should().Be("FR31-A", "the journal must record the code as stored, not as typed");
    }

    [Fact]
    public async Task Save_NewRoleWithACodeThatIsCurrentlyInForce_IsRefused()
    {
        SkipUnlessDbAvailable();

        var live = await CreateRoleAsync("B2-R2", "Vai trò đang chạy", OpenFrom2020);
        var versionsBefore = await CountRowsAsync("role_version", "role_id", live);

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R2", "Vai trò của người khác", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeInUse");
        (await CountRowsAsync("role_version", "role_id", live)).Should().Be(versionsBefore,
            "the live role must be untouched -- this is the lost-update-by-reattachment case");
        (await Roles.GetByIdentityAsync(live, Today)).Value.RoleName.Should().Be("Vai trò đang chạy");
    }

    [Fact]
    public async Task Save_NewRoleWithAnUnusedCode_MintsAFreshIdentity()
    {
        SkipUnlessDbAvailable();

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R3", "Vai trò mới", false, null, [], []));

        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));
        save.Value.ReattachedToExistingIdentity.Should().BeFalse();
        (await CountRowsAsync("role_version", "role_id", save.Value.RoleId)).Should().Be(1);

        var roleEvent = (await ReadAuditRowsForTargetAsync($"role:{save.Value.RoleId}"))
            .Single(r => r.EventType == "role-change");
        JsonDocument.Parse(roleEvent.Detail).RootElement.GetProperty("action").GetString()
            .Should().Be("add");
    }

    [Fact]
    public async Task Save_NewRoleWithACodeOwnedByTwoIdentities_FailsAsAmbiguous()
    {
        SkipUnlessDbAvailable();

        var first = await CreateRoleAsync("B2-R4", "Vai trò A", new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));
        var second = await CreateRoleAsync("B2-R4", "Vai trò B", new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31)));

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R4", "Vai trò C", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeIdentityAmbiguous");
        (await CountRowsAsync("role_version", "role_id", first)).Should().Be(1);
        (await CountRowsAsync("role_version", "role_id", second)).Should().Be(1);
    }

    [Fact]
    public async Task Save_NewRoleThatFailsAfterMinting_LeavesNoOrphanHeader()
    {
        SkipUnlessDbAvailable();

        var headersBefore = await CountAllRowsAsync("role");

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R5", "Vai trò", false, null, [],
            [new RolePermissionGrantToAdd(999_999_999, ScopeLevel.Global, null)]));

        save.IsError.Should().BeTrue();
        (await CountAllRowsAsync("role")).Should().Be(
            headersBefore, "the header is minted inside the transaction that fails, so the rollback removes it");
    }

    [Fact]
    public async Task Save_TwoConcurrentDeclarationsOfOneCode_OneWinsAndTheOtherFailsOwnershipChanged()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;
        var headersBefore = await CountAllRowsAsync("role");

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(RoleRepository.CodeLockKey("B2-R6"));
        try
        {
            var request = () => new SaveRoleDeclarationRequest(
                new RoleSaveTarget.NewRole(), "B2-R6", "Vai trò", false, null, [], []);

            var a = Task.Run(() => Service.SaveRoleDeclarationAsync(request()), ct);
            var b = Task.Run(() => Service.SaveRoleDeclarationAsync(request()), ct);

            await WaitUntilUserLockWaiterAsync(observer, RoleRepository.CodeLockKey("B2-R6"), ct, minWaiters: 2);

            await ReleaseLockAsync(gate, RoleRepository.CodeLockKey("B2-R6"));
            var results = await Task.WhenAll(a, b);

            results.Count(r => !r.IsError).Should().Be(1, "exactly one declaration may win");
            results.Should().ContainSingle(r => r.IsError && r.Errors.Any(e => e.Code == "Role.CodeOwnershipChanged"),
                "the loser must be told its guess was superseded, not silently write a second identity for one code");
        }
        finally
        {
            await gate.DisposeAsync();
        }

        (await RoleRepo.GetCodeOwnersAsync("B2-R6", Today)).Should().ContainSingle(
            "one code, one identity -- this is the whole point of B2");
        (await CountAllRowsAsync("role")).Should().Be(headersBefore + 1,
            "only the winner's identity exists: the loser never mints one, because minting now happens " +
            "inside the transaction it never gets to commit");
    }

    [Fact]
    public async Task Save_NewRoleWhoseCodeGainsAnOwnerBetweenTheTwoReads_FailsAsOwnershipChanged()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(RoleRepository.CodeLockKey("B2-OC"));
        try
        {
            var request = () => new SaveRoleDeclarationRequest(
                new RoleSaveTarget.NewRole(), "B2-OC", "Vai trò", false, null, [], []);

            var first = Task.Run(() => Service.SaveRoleDeclarationAsync(request()), ct);
            var second = Task.Run(() => Service.SaveRoleDeclarationAsync(request()), ct);

            await WaitUntilUserLockWaiterAsync(observer, RoleRepository.CodeLockKey("B2-OC"), ct, minWaiters: 2);
            await ReleaseLockAsync(gate, RoleRepository.CodeLockKey("B2-OC"));

            var results = await Task.WhenAll(first, second);
            results.Should().ContainSingle(r => r.IsError && r.Errors.Any(e => e.Code == "Role.CodeOwnershipChanged"),
                "the pre-composite guess saw zero owners; once another save commits under the code lock, re-decision must fail");
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    // The two tests above cover the divergence in ONE direction: the guess saw no owner and the locked read
    // found one. These two cover the reverse — the guess re-attached to a dormant owner, so THAT identity is
    // the only one the composite locked. If the locked read then disagrees, minting a different identity
    // would write outside every lock held, and re-attaching a different one would need a lock §7 forbids
    // taking now. Both must refuse and write nothing.

    [Fact]
    public async Task Save_ReattachTargetLosesTheCodeBetweenTheTwoReads_FailsAsOwnershipChanged()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;

        var dormant = await CreateRoleAsync(
            "B2-RD1", "Vai trò cũ", new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        var roleHeadersBefore = await CountAllRowsAsync("role");
        var versionsBefore = await CountRowsAsync("role_version", "role_id", dormant);

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(RoleRepository.CodeLockKey("B2-RD1"));
        try
        {
            // The lock-free guess re-attaches to `dormant`; the save then blocks on the code lock.
            var save = Task.Run(
                () => Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
                    new RoleSaveTarget.NewRole(), "B2-RD1", "Vai trò dùng lại", false, null, [], [])),
                ct);

            await WaitUntilUserLockWaiterAsync(observer, RoleRepository.CodeLockKey("B2-RD1"), ct, minWaiters: 1);

            // Ownership disappears while the save waits.
            await RecodeRoleVersionsAsync(dormant, "B2-RD1-MOVED");
            await ReleaseLockAsync(gate, RoleRepository.CodeLockKey("B2-RD1"));

            var result = await save;
            result.IsError.Should().BeTrue();
            result.Errors.Should().Contain(
                e => e.Code == "Role.CodeOwnershipChanged",
                "the identity the composite locked no longer owns the code; the operator must reload rather " +
                "than have a fresh identity minted under a lock that was taken for a different one");
        }
        finally
        {
            await gate.DisposeAsync();
        }

        (await CountAllRowsAsync("role")).Should().Be(
            roleHeadersBefore, "refusing must not mint an identity — the in-transaction mint is reachable here");
        (await CountRowsAsync("role_version", "role_id", dormant)).Should().Be(
            versionsBefore, "the former owner must be untouched too");
    }

    [Fact]
    public async Task Save_CodeGainsADormantOwnerBetweenTheTwoReads_FailsAsOwnershipChanged()
    {
        SkipUnlessDbAvailable();

        // The last uncovered cell of the divergence matrix: the guess saw NO owner (so nothing was
        // enlisted and a mint was expected), and the locked read finds a DORMANT one. The existing
        // concurrency tests all produce an IN-FORCE owner, which is refused one branch earlier.
        var ct = TestContext.Current.CancellationToken;

        var roleHeadersBefore = await CountAllRowsAsync("role");

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(RoleRepository.CodeLockKey("B2-RD3"));
        long dormant;
        try
        {
            var save = Task.Run(
                () => Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
                    new RoleSaveTarget.NewRole(), "B2-RD3", "Vai trò mới", false, null, [], [])),
                ct);

            await WaitUntilUserLockWaiterAsync(observer, RoleRepository.CodeLockKey("B2-RD3"), ct, minWaiters: 1);

            dormant = await CreateRoleAsync(
                "B2-RD3", "Vai trò cũ", new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
            await ReleaseLockAsync(gate, RoleRepository.CodeLockKey("B2-RD3"));

            var result = await save;
            result.IsError.Should().BeTrue();
            result.Errors.Should().Contain(
                e => e.Code == "Role.CodeOwnershipChanged",
                "re-attaching now would need the lock of an identity this composite never enlisted, and " +
                "minting anyway would put two identities on one code");
        }
        finally
        {
            await gate.DisposeAsync();
        }

        (await CountAllRowsAsync("role")).Should().Be(
            roleHeadersBefore + 1, "only the dormant owner created mid-flight — the save itself minted nothing");
        (await CountRowsAsync("role_version", "role_id", dormant)).Should().Be(
            1, "the dormant owner must not have gained the refused declaration as a version");
    }

    [Fact]
    public async Task Save_ReattachTargetIsReplacedByAnotherOwnerBetweenTheTwoReads_FailsAsOwnershipChanged()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;

        var dormant = await CreateRoleAsync(
            "B2-RD2", "Vai trò cũ", new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        var other = await CreateRoleAsync(
            "B2-RD2-OTHER", "Vai trò khác", new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        var roleHeadersBefore = await CountAllRowsAsync("role");
        var dormantVersionsBefore = await CountRowsAsync("role_version", "role_id", dormant);
        var otherVersionsBefore = await CountRowsAsync("role_version", "role_id", other);

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        var gate = await HoldLockAsync(RoleRepository.CodeLockKey("B2-RD2"));
        try
        {
            var save = Task.Run(
                () => Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
                    new RoleSaveTarget.NewRole(), "B2-RD2", "Vai trò dùng lại", false, null, [], [])),
                ct);

            await WaitUntilUserLockWaiterAsync(observer, RoleRepository.CodeLockKey("B2-RD2"), ct, minWaiters: 1);

            // The code changes hands while the save waits: one dormant owner, but not the enlisted one.
            await RecodeRoleVersionsAsync(dormant, "B2-RD2-MOVED");
            await RecodeRoleVersionsAsync(other, "B2-RD2");
            await ReleaseLockAsync(gate, RoleRepository.CodeLockKey("B2-RD2"));

            var result = await save;
            result.IsError.Should().BeTrue();
            result.Errors.Should().Contain(
                e => e.Code == "Role.CodeOwnershipChanged",
                "the code's dormant owner is now an identity this composite never locked — writing it would " +
                "be a write outside every lock held");
        }
        finally
        {
            await gate.DisposeAsync();
        }

        (await CountAllRowsAsync("role")).Should().Be(roleHeadersBefore);
        (await CountRowsAsync("role_version", "role_id", dormant)).Should().Be(dormantVersionsBefore);
        (await CountRowsAsync("role_version", "role_id", other)).Should().Be(
            otherVersionsBefore, "the new owner must not be written either — it was never locked");
    }

    [Fact]
    public async Task Save_NewRoleWithACodeWhoseOwnerIsLiveUnderADifferentCode_IsRefused()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R9-OLD", "Vai trò",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-1)));
        await InsertRoleVersionAsync(role, "B2-R9-NEW", "Vai trò đổi mã",
            new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var versionsBefore = await CountRowsAsync("role_version", "role_id", role);

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R9-OLD", "Vai trò khác hẳn", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeInUse");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(versionsBefore);
    }

    [Fact]
    public async Task Save_NewRoleWithACodeWhoseOwnerHasAFutureVersion_StopsAsAnIntegrityFault()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R10", "Vai trò",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        await InsertRoleVersionAsync(role, "B2-R10", "Bản tương lai không hợp lệ",
            new EffectivePeriod(Today.AddDays(5), EffectivePeriod.OpenEnd));

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R10", "Vai trò mới", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeOwnerNotDormant");
    }

    [Fact]
    public async Task Save_NewRoleWithACodeClosedToday_StillReattaches()
    {
        SkipUnlessDbAvailable();

        var closedToday = await CreateRoleAsync("B2-R11", "Vai trò ngừng hôm nay",
            new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-1)));

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "B2-R11", "Vai trò dùng lại", false, null, [], []));

        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));
        save.Value.RoleId.Should().Be(closedToday);
        save.Value.ReattachedToExistingIdentity.Should().BeTrue();
    }

    [Fact]
    public async Task Save_ExistingRoleWithANonAsciiExpectedCode_ReturnsAClearDomainError()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("VT-KẾ", "Vai trò", OpenFrom2020);
        var version = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, version, "VT-KẾ"), "VT-KE", "Vai trò", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeNotAscii");
    }

    [Fact]
    public async Task Save_ExistingRoleRenamedIntoAnotherIdentitysHistoricalCode_IsRejected()
    {
        SkipUnlessDbAvailable();

        await CreateRoleAsync("B2-R7-OLD", "Vai trò đã ngừng", new EffectivePeriod(new DateOnly(2020, 1, 1), Today.AddDays(-2)));
        var live = await CreateRoleAsync("B2-R7-LIVE", "Vai trò đang chạy", OpenFrom2020);
        var version = (await Roles.GetByIdentityAsync(live, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(live, version, "B2-R7-LIVE"), "B2-R7-OLD", "Vai trò đang chạy",
            false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeOwnedByAnotherIdentity");
    }

    [Fact]
    public async Task Save_ExistingRoleWithAWrongExpectedCode_IsRejected()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B2-R8", "Vai trò", OpenFrom2020);
        var version = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, version, "B2-R8-WRONG"), "B2-R8", "Tên mới", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.ExpectedCodeMismatch");
    }

    [Fact]
    public async Task Save_NonAsciiCode_ReturnsAClearDomainError()
    {
        SkipUnlessDbAvailable();

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "VT-KẾ", "Vai trò", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeNotAscii");
    }

    [Fact]
    public async Task Save_NewRoleWithNulInCode_ReturnsCodeNotAscii()
    {
        SkipUnlessDbAvailable();

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.NewRole(), "AB\u0000CD", "Vai trò", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.CodeNotAscii");
    }

    // =========================================================================================
    // B4 — reject a save whose expected version has been superseded.
    // =========================================================================================

    [Fact]
    public async Task Save_ExpectedVersionIsStale_RejectsWithoutWritingAnything()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B4-R1", "Tên gốc", OpenFrom2020);
        var stale = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var first = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, stale, "B4-R1"), "B4-R1", "Tên của người thứ nhất", false, null, [], []));
        first.IsError.Should().BeFalse(DescribeErrors(first.Errors));

        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var auditRowsBefore = (await ReadAuditRowsForTargetAsync($"role:{role}")).Count;

        var second = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, stale, "B4-R1"), "B4-R1", "Tên của người thứ hai", false, null, [], []));

        second.IsError.Should().BeTrue();
        second.Errors.Should().Contain(e => e.Code == "Role.VersionOutOfDate");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore, "a rejected save writes no version");
        (await ReadAuditRowsForTargetAsync($"role:{role}")).Count.Should().Be(auditRowsBefore,
            "and no audit row either -- the check is inside the composite, so the rollback covers both");
        (await Roles.GetByIdentityAsync(role, Today)).Value.RoleName.Should().Be("Tên của người thứ nhất");
    }

    [Fact]
    public async Task Save_ConcurrentGrantAddedAfterLoad_RejectsRatherThanCommittingAStaleGrantSet()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B4-R2", "Vai trò", OpenFrom2020);
        var functionA = await CreateFunctionAsync("B4.Fn.A", OpenFrom2020);
        var functionB = await CreateFunctionAsync("B4.Fn.B", OpenFrom2020);
        var loaded = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var other = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, loaded, "B4-R2"), "B4-R2", "Vai trò", false, null, [],
            [new RolePermissionGrantToAdd(functionA, ScopeLevel.Global, null)]));
        other.IsError.Should().BeFalse(DescribeErrors(other.Errors));

        var stalePush = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, loaded, "B4-R2"), "B4-R2", "Vai trò", false, null, [],
            [new RolePermissionGrantToAdd(functionB, ScopeLevel.Global, null)]));

        stalePush.IsError.Should().BeTrue();
        stalePush.Errors.Should().Contain(e => e.Code == "Role.VersionOutOfDate");
        (await CountRowsAsync("role_permission_version", "role_id", role)).Should().Be(1,
            "operator 2's grant is intact and operator 1's was not written");
    }

    [Fact]
    public async Task Save_RoleCancelledSinceLoad_ReportsVersionOutOfDate_NotARawResolverError()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B4-R3", "Vai trò", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var loaded = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var close = await Service.CloseRoleDeclarationAsync(new CloseRoleDeclarationRequest(role, loaded, null));
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, loaded, "B4-R3"), "B4-R3", "Tên mới", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "Role.VersionOutOfDate");
        save.Errors.Should().NotContain(e => e.Code.Contains("NoCoverage"),
            "the operator is told to reload, not shown a period-resolution internal");
    }

    [Fact]
    public async Task Save_ExpectedVersionMatches_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B4-R4", "Tên gốc", OpenFrom2020);
        var current = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, current, "B4-R4"), "B4-R4", "Tên mới", false, null, [], []));

        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));
        (await Roles.GetByIdentityAsync(role, Today)).Value.RoleName.Should().Be("Tên mới");
    }

    [Fact]
    public async Task Save_RoleWithTwoOverlappingActiveVersions_SurfacesTheIntegrityFault_NotVersionOutOfDate()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B4-R5", "Vai trò", OpenFrom2020);
        var loaded = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        await InsertRoleVersionAsync(role, "B4-R5", "Bản chồng lấn", OpenFrom2020);

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, loaded, "B4-R5"), "B4-R5", "Tên mới", false, null, [], []));

        save.IsError.Should().BeTrue();
        save.Errors.Should().Contain(e => e.Code == "EffectivePeriod.OverlappingVersions");
        save.Errors.Should().NotContain(e => e.Code == "Role.VersionOutOfDate");
    }

    // =========================================================================================
    // B3a — one role-change event per save; grant events share its operation id.
    // =========================================================================================

    [Fact]
    public async Task Save_WritesOneRoleEvent_AndEveryGrantEventSharesItsOperationId()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B3-R1", "Vai trò", OpenFrom2020);
        var functionToAdd = await CreateFunctionAsync("B3.Fn.Add", OpenFrom2020);
        var functionToRevoke = await CreateFunctionAsync("B3.Fn.Rev", OpenFrom2020);
        var grantToRevoke = await CreateGrantAsync(role, functionToRevoke, OpenFrom2020, ScopeLevel.Global);
        var version = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, version, "B3-R1"), "B3-R1", "Vai trò", false, null,
            [grantToRevoke], [new RolePermissionGrantToAdd(functionToAdd, ScopeLevel.Global, null)]));
        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));

        var rows = await ReadAuditRowsForTargetAsync($"role:{role}");
        rows.Should().HaveCount(3, "one role event plus one per grant touched -- all under the role's own target");
        rows.Count(r => r.EventType == "role-change").Should().Be(1);
        rows.Count(r => r.EventType == "permission-change").Should().Be(2);
        rows.Select(r => ReadOperationId(r.Detail)).Distinct().Should().ContainSingle(
            "one user gesture is one operation id -- grouping must never need a timestamp heuristic");
    }

    [Fact]
    public async Task Save_TwoSavesOfTheSameRole_ProduceTwoDistinctOperationIds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B3-R2", "Vai trò", OpenFrom2020);
        var v1 = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        (await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, v1, "B3-R2"), "B3-R2", "Tên 1", false, null, [], [])))
            .IsError.Should().BeFalse();

        var v2 = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        (await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, v2, "B3-R2"), "B3-R2", "Tên 2", false, null, [], [])))
            .IsError.Should().BeFalse();

        (await ReadAuditRowsForTargetAsync($"role:{role}")).Select(r => ReadOperationId(r.Detail))
            .Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Save_RoleEventCarriesTheVersionItProduced_AndTheActor()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B3-R3", "Vai trò", OpenFrom2020);
        var version = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var save = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, version, "B3-R3"), "B3-R3", "Tên mới", false, "đổi tên", [], []));
        save.IsError.Should().BeFalse(DescribeErrors(save.Errors));

        var row = (await ReadAuditRowsForTargetAsync($"role:{role}")).Single(r => r.EventType == "role-change");
        row.Username.Should().Be(ExpectedActorUsername);
        row.Detail.Should().Contain(save.Value.RoleVersionId.ToString());
        JsonDocument.Parse(row.Detail).RootElement.GetProperty("action").GetString()
            .Should().Be("edit");
    }

    [Fact]
    public async Task Save_AuditWriteFails_RollsBackTheVersionToo()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("B3-R4", "Vai trò", OpenFrom2020);
        var version = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var versionsBefore = await CountRowsAsync("role_version", "role_id", role);

        var service = CreateServiceWithAuditWriter(new AlwaysFailingAuditLogWriter());
        var save = await service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, version, "B3-R4"), "B3-R4", "Tên mới", false, null, [], []));

        save.IsError.Should().BeTrue();
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(versionsBefore,
            "the event log and the versions it describes can never disagree -- one transaction, both or neither");
        (await Roles.GetByIdentityAsync(role, Today)).Value.RoleName.Should().Be("Vai trò");
    }

    // FR-3 (brief 075 fix round): the code-scoped lock is kept even though AdminFlagLockKey serialises
    // every save today — unrelated codes must not wait on each other's code lock.
    [Fact]
    public async Task Save_CodeLockSerialisesSameCode_AllowsUnrelatedCodeWhileFirstIsParked()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;
        const string codeA = "FR3-A";
        const string codeB = "FR3-B";
        var keyA = RoleRepository.CodeLockKey(codeA);

        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(ct);

        await using var gate = await HoldLockAsync(keyA);
        try
        {
            var saveA = Task.Run(
                () => Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
                    new RoleSaveTarget.NewRole(), codeA, "Vai trò A", false, null, [], [])),
                ct);

            await WaitUntilUserLockWaiterAsync(observer, keyA, ct, minWaiters: 1);

            var saveB = await Service.SaveRoleDeclarationAsync(new SaveRoleDeclarationRequest(
                new RoleSaveTarget.NewRole(), codeB, "Vai trò B", false, null, [], []));

            saveB.IsError.Should().BeFalse(DescribeErrors(saveB.Errors),
                "a different code must not wait on the first save's code lock");

            await ReleaseLockAsync(gate, keyA);
            var resultA = await saveA;
            resultA.IsError.Should().BeFalse(DescribeErrors(resultA.Errors));
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    private sealed class AlwaysFailingAuditLogWriter : IAuditLogWriter
    {
        public Task<ErrorOr<Success>> WriteAsync(
            AuditLogEntry entry, IDbTransaction transaction, CancellationToken cancellationToken = default) =>
            Task.FromResult<ErrorOr<Success>>(Error.Failure("Test.AuditWriteFailed", "injected"));
    }

    // ---------------------------------------------------------------------------------------

    private sealed class RecordingConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int CallCount { get; private set; }

        public IDbConnection CreateConnection()
        {
            CallCount++;
            return inner.CreateConnection();
        }
    }

    // T1 — the end-to-end shape of the same defect the repository-level tests in
    // OperationDateGuardTests pin: ONE close operation must run entirely on the date it captured.
    //
    // The service captures D and derives the close date from it (role is Immediate, so the only legal end
    // is D - 1, the INCLUSIVE end meaning "ceases from today"). The role repository below has already
    // rolled over to D + 1. If the repository's close guard consults its own clock instead of the date the
    // operation handed it, it computes "yesterday" as D and accepts a close through D — a full extra day
    // of coverage for a role that the operation's own business date says stopped a day earlier. That is
    // the FAIL-OPEN direction: nothing errors, a wrong row is persisted, and no same-day test can see it.
    [Fact]
    public async Task Close_persists_the_end_derived_from_the_operations_own_date_even_when_the_repositorys_clock_has_rolled_over()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("OPD-SVC", "Service close role", new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd));
        var versionId = (await Roles.GetHistoryAsync(role)).Single().Id;

        var roleRepoAhead = BuildRoleRepositoryWithClock(Today.AddDays(1));
        var service = new RoleDeclarationService(
            roleRepoAhead, GrantRepo, FunctionRepo, Connections, new AuditLogWriter(),
            new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, Actor)), new FakeBreakGlassPolicy(),
            new FakeCurrentWindowsUser(Actor), new FixedBusinessDateProvider(Today));

        var result = await service.CloseRoleDeclarationAsync(new CloseRoleDeclarationRequest(role, versionId, "retire"));

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var active = (await Roles.GetHistoryAsync(role)).Where(v => v.IsActive).ToList();
        active.Should().ContainSingle();
        active.Single().EffectiveTo.Should().Be(
            Today.AddDays(-1),
            "the close date is derived from the date the OPERATION captured, never from a clock read further down");
    }

    // ---------------------------------------------------------------------------------------

    private sealed class FakeAuthorizationService(ErrorOr<DataScope> outcome) : IAuthorizationService
    {
        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) => Task.FromResult(outcome);
        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => Task.FromResult(!outcome.IsError);
    }

    private sealed class FakeBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);
        public bool IsBreakGlassAdmin(string username) => _admins.Contains(username);
    }

    private sealed class FakeCurrentWindowsUser(string? username) : ICurrentWindowsUser
    {
        public string? Username => username;
    }

    // Moves every version of one role to a different code — how a test makes code ownership change hands
    // while a save is parked on the code lock. Raw SQL on purpose: no writer may re-code a role's whole
    // history, so this state is only reachable as fixture manipulation.
    private async Task RecodeRoleVersionsAsync(long roleId, string newRoleCode)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE role_version SET role_code = @newRoleCode WHERE role_id = @roleId",
            new { roleId, newRoleCode });
    }

    private async Task<long> CountRowsAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {versionTable} WHERE {identityColumn} = @identityId",
            new { identityId });
    }

    private async Task<SaveRoleDeclarationRequest> EditSaveRequestAsync(
        long roleId,
        string roleCode,
        string roleName,
        bool isAdminRole = false,
        string? reason = "save",
        IReadOnlyList<long>? grantIdentityIdsToRevoke = null,
        IReadOnlyList<RolePermissionGrantToAdd>? grantsToAdd = null,
        string? expectedRoleCode = null)
    {
        var versionId = (await Roles.GetByIdentityAsync(roleId, Today)).Value.Id;
        return new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(roleId, versionId, expectedRoleCode ?? roleCode),
            roleCode,
            roleName,
            isAdminRole,
            reason,
            grantIdentityIdsToRevoke ?? [],
            grantsToAdd ?? []);
    }

    private async Task<string> ReadRoleCodeForVersionAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string>(
            "SELECT role_code FROM role_version WHERE id = @versionId",
            new { versionId }) ?? throw new InvalidOperationException($"role_version {versionId} not found.");
    }

    private async Task<IReadOnlyList<(string EventType, string? Username, string Detail)>> ReadAuditRowsForTargetAsync(
        string target)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<(string EventType, string? Username, string Detail)>(
            "SELECT event_type AS EventType, username AS Username, detail AS Detail FROM audit_log WHERE target = @target ORDER BY id",
            new { target });
        return rows.AsList();
    }

    private static string ReadOperationId(string detailJson)
    {
        using var json = JsonDocument.Parse(detailJson);
        return json.RootElement.GetProperty("operationId").GetString()
            ?? throw new InvalidOperationException("operationId missing from audit detail.");
    }

    private static long ReadGrantRolePermissionId(string detailJson)
    {
        using var json = JsonDocument.Parse(detailJson);
        return json.RootElement.GetProperty("rolePermissionId").GetInt64();
    }

    private static string FindGrantAuditDetail(
        IReadOnlyList<(string EventType, string? Username, string Detail)> rows,
        long rolePermissionId)
    {
        foreach (var row in rows.Where(r => r.EventType == "permission-change"))
        {
            if (ReadGrantRolePermissionId(row.Detail) == rolePermissionId)
            {
                return row.Detail;
            }
        }

        throw new InvalidOperationException(
            $"No permission-change audit row found for rolePermissionId {rolePermissionId}.");
    }

    private async Task<DateTime> ReadAnyAuditOccurredAtAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<DateTime>("SELECT occurred_at FROM audit_log LIMIT 1");
    }

    // B3b (Task 3): a shared shape for BOTH the parent (RoleCloseAuditDetail) and child (AuditDetail)
    // JSON shapes -- the fields each shape does not carry deserialize to null rather than throwing, so
    // one probe/parse helper covers both without a second data-access style.
    private static readonly JsonSerializerOptions AuditDetailProbeJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record AuditDetailProbe(
        string Action,
        string OperationId,
        long RoleId,
        long? RoleVersionId,
        long? RolePermissionId,
        long? FunctionId,
        DateOnly? EffectiveThrough,
        DateOnly? From,
        DateOnly? To,
        string? Note);

    private static AuditDetailProbe ParseDetail(string detailJson) =>
        JsonSerializer.Deserialize<AuditDetailProbe>(detailJson, AuditDetailProbeJsonOptions)
        ?? throw new InvalidOperationException("audit detail JSON deserialized to null.");

    // B3b (Task 3): same connection/parameter style as CountRowsAsync above -- reads a version row's
    // own period columns directly, so a rolled-back close can be proven to have left the TARGETED
    // version untouched (not merely un-remnanted).
    private async Task<(DateOnly EffectiveFrom, DateOnly EffectiveTo)> QueryVersionStateAsync(
        string versionTable, long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            $"SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo FROM {versionTable} WHERE id = @versionId",
            new { versionId });
    }

    // B3b (Task 3): same connection/parameter style as CountRowsAsync above, extended with a WHERE
    // fragment -- mirrors AutoCutDependentTests.CountWhereAsync/CountCancelledAsync (Task 1), added
    // here separately because that pair is private to AutoCutDependentTests.cs, not shared.
    private async Task<long> CountWhereAsync(string table, string identityColumn, long identityId, string whereFragment)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {table} WHERE {identityColumn} = @identityId AND {whereFragment}",
            new { identityId });
    }

    private Task<long> CountCancelledAsync(string table, string identityColumn, long identityId) =>
        CountWhereAsync(table, identityColumn, identityId, "status = 'cancelled'");
}
