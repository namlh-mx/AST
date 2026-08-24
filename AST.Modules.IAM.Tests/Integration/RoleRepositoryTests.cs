using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Entities;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Brief 058 — Role Upsert/read/P6/identity-compensate + Model 2 grant forward path.
public sealed class RoleRepositoryTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod TodayOpen = new(Today, EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod ExistingBase = new(new DateOnly(2020, 3, 1), new DateOnly(2020, 6, 30));
    private static readonly EffectivePeriod Year2025 = new(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    private static readonly DataScope Global = new(ScopeLevel.Global, null, "tester");

    // --- Identity create + compensate ---

    [Fact]
    public async Task CreateIdentity_ThenDeleteEmpty_LeavesNoOrphanRole()
    {
        SkipUnlessDbAvailable();

        var id = await Roles.CreateIdentityAsync();
        await Roles.DeleteEmptyIdentityAsync(id);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM `role` WHERE id = @id", new { id });
        count.Should().Be(0);
    }

    [Fact]
    public async Task CreateIdentity_ThenDeleteEmpty_LeavesNoOrphanRolePermission()
    {
        SkipUnlessDbAvailable();

        var id = await RolePermissions.CreateIdentityAsync();
        await RolePermissions.DeleteEmptyIdentityAsync(id);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM role_permission WHERE id = @id", new { id });
        count.Should().Be(0);
    }

    [Fact]
    public async Task DeleteEmptyIdentity_AfterUpsert_DoesNotDeleteRoleWithVersions()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("KEEP-R", "Keep", OpenFrom2020);
        await Roles.DeleteEmptyIdentityAsync(id);

        var still = await Roles.GetByIdentityAsync(id, Today);
        still.IsError.Should().BeFalse(DescribeErrors(still.Errors));
    }

    // --- Read path: isactive AND period-covers-D ---

    [Fact]
    public async Task GetByIdentityAsync_IsActive0_Invisible()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("INV-ACT", "Invisible active", OpenFrom2020);
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "UPDATE role_version SET isactive = 0 WHERE role_id = @id AND isactive = 1", new { id });

        var result = await Roles.GetByIdentityAsync(id, Today);
        result.IsError.Should().BeTrue();
        result.FirstError.Type.Should().Be(ErrorOr.ErrorType.NotFound);
    }

    [Fact]
    public async Task GetByIdentityAsync_OutOfPeriod_Invisible()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("INV-PER", "Invisible period", Year2025);

        var outside = await Roles.GetByIdentityAsync(id, new DateOnly(2024, 6, 1));
        outside.IsError.Should().BeTrue();
        outside.FirstError.Type.Should().Be(ErrorOr.ErrorType.NotFound);

        var inside = await Roles.GetByIdentityAsync(id, new DateOnly(2025, 6, 1));
        inside.IsError.Should().BeFalse(DescribeErrors(inside.Errors));
        inside.Value.RoleCode.Should().Be("INV-PER");
    }

    // --- P6 ---

    [Fact]
    public async Task UpsertAsync_DuplicateCode_Overlapping_ReturnsRoleCodeInUse()
    {
        SkipUnlessDbAvailable();

        _ = await CreateRoleAsync("DUPCODE", "First", OpenFrom2020);
        var other = await Roles.CreateIdentityAsync();
        var composite = new CompositeWrite(Connections).Enlist(Roles, other);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, other, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "DUPCODE", "Second", false, adminFlagChangeAuthorized: false, VersionOperationKind.Add, new OperationDate(Today), "tester", "dup");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.CodeInUse");
    }

    [Fact]
    public async Task UpsertAsync_SelfExtend_SameCode_Succeeds()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("SELFEXT", "Self", OpenFrom2020);
        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd),
                "SELFEXT", "Self extended", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "extend");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    // --- 8-case algebra on role_version ---

    [Fact]
    public async Task UpsertAsync_AlgebraCase1_Disjoint_WarnsNotBlocked()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("RALG1", "Case1", ExistingBase);
        UpsertResult? inner = null;
        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd),
                "RALG1", "Case1 later", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "case1");
            if (write.IsError)
            {
                return write.Errors;
            }

            inner = write.Value;
            return Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        inner.Should().NotBeNull();
        inner!.Warnings.Should().ContainSingle();
        var active = await GetActiveRolePeriodsAsync(id);
        active.Should().HaveCount(2);
    }

    // --- Temporal-FK (child grant vs role) ---

    [Fact]
    public async Task RolePermissionUpsert_ExceedsRoleCoverage_ReturnsTemporalFkParentGap()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("TFK-R", "Narrow role", Year2025);
        var function = await CreateFunctionAsync("Iam.Role.Tfk", OpenFrom2020);
        var grantId = await RolePermissions.CreateIdentityAsync();

        var result = await RolePermissions.UpsertAsync(
            grantId, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global,
            VersionOperationKind.Add, new OperationDate(Today), "tester", "grant");

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");
    }

    // --- Model 2: two grant identities, same (role,function), disjoint periods ---

    [Fact]
    public async Task Model2_TwoGrantIdentities_DisjointPeriods_BothActiveNonOverlapping()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("M2-ROLE", "Model2", OpenFrom2020);
        var function = await CreateFunctionAsync("Iam.Role.Model2", OpenFrom2020);

        var firstPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2022, 12, 31));
        var secondPeriod = new EffectivePeriod(new DateOnly(2023, 1, 1), EffectivePeriod.OpenEnd);

        var g1 = await CreateGrantAsync(role, function, firstPeriod, ScopeLevel.OwnOrgUnit, "grant1");
        var g2 = await CreateGrantAsync(role, function, secondPeriod, ScopeLevel.Global, "grant2");

        var active = await RolePermissions.GetActiveGrantsForPeriodAsync(role, OpenFrom2020);
        active.Where(g => g.FunctionId == function).Should().HaveCount(2);
        active.Select(g => g.RolePermissionId).Should().BeEquivalentTo([g1, g2]);

        var at2021 = await RolePermissions.GetGrantAsync(role, function, new DateOnly(2021, 6, 1));
        at2021.IsError.Should().BeFalse(DescribeErrors(at2021.Errors));
        at2021.Value.RolePermissionId.Should().Be(g1);
        at2021.Value.ScopeLevel.Should().Be(ScopeLevel.OwnOrgUnit);

        var at2024 = await RolePermissions.GetGrantAsync(role, function, new DateOnly(2024, 6, 1));
        at2024.IsError.Should().BeFalse(DescribeErrors(at2024.Errors));
        at2024.Value.RolePermissionId.Should().Be(g2);
        at2024.Value.ScopeLevel.Should().Be(ScopeLevel.Global);
    }

    [Fact]
    public async Task GetHistoryAsync_IncludesInactiveVersions()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("HIST-R", "History", TodayOpen);
        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        (await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, TodayOpen, "HIST-R", "History corrected", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "corr");
            return write.IsError ? write.Errors : Result.Success;
        })).IsError.Should().BeFalse();

        var history = await Roles.GetHistoryAsync(id);
        history.Should().HaveCountGreaterThanOrEqualTo(2);
        history.Count(h => !h.IsActive).Should().BeGreaterThanOrEqualTo(1);
        history.Should().Contain(h => h.IsActive && h.RoleName == "History corrected");
    }

    // --- Brief 059 / Fix Round 1: admin-flag authority + composite-only grant ---

    [Fact]
    public async Task UpsertAsync_Composite_AdminFlagAuthorized_Succeeds()
    {
        SkipUnlessDbAvailable();

        var id = await Roles.CreateIdentityAsync();
        var roleRepo = (RoleRepository)Roles;
        var connections = Connections;
        var composite = new CompositeWrite(connections)
            .Enlist(roleRepo, id)
            .Enlist(RoleRepository.AdminFlagLockKey);

        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "ADM-C-OK", "Admin composite ok",
                isAdminRole: true, adminFlagChangeAuthorized: true,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "admin");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var row = await Roles.GetByIdentityAsync(id, Today);
        row.IsError.Should().BeFalse(DescribeErrors(row.Errors));
        row.Value.IsAdminRole.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_Composite_AdminFlagUnauthorized_ReturnsForbidden()
    {
        SkipUnlessDbAvailable();

        var id = await Roles.CreateIdentityAsync();
        var roleRepo = (RoleRepository)Roles;
        var connections = Connections;
        var composite = new CompositeWrite(connections).Enlist(roleRepo, id);

        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "ADM-C-NO", "Admin composite no",
                isAdminRole: true, adminFlagChangeAuthorized: false,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "admin");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e =>
            e.Code == "Role.AdminFlagChangeNotAuthorized" && e.Type == ErrorType.Forbidden);
        (await CountRoleVersionRowsAsync(id)).Should().Be(0, "Forbidden must write no version row");
    }

    [Fact]
    public async Task UpsertAsync_NonAdmin_UnauthorizedFlagIrrelevant_Succeeds()
    {
        SkipUnlessDbAvailable();

        var id = await Roles.CreateIdentityAsync();
        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "NADM", "Not admin", isAdminRole: false, adminFlagChangeAuthorized: false,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task UpsertAsync_Composite_TurnOffAdmin_Unauthorized_ReturnsForbidden()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("ADM-C-OFF-NO", "Admin c off no", OpenFrom2020, isAdminRole: true);
        var rowsBefore = await CountRoleVersionRowsAsync(id);
        var roleRepo = (RoleRepository)Roles;
        var connections = Connections;
        var composite = new CompositeWrite(connections).Enlist(roleRepo, id);

        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.UpsertAsync(
                context, id, TodayOpen, "ADM-C-OFF-NO", "Demoted",
                isAdminRole: false, adminFlagChangeAuthorized: false,
                VersionOperationKind.Edit, new OperationDate(Today), "tester", "demote");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e =>
            e.Code == "Role.AdminFlagChangeNotAuthorized" && e.Type == ErrorType.Forbidden);
        (await CountRoleVersionRowsAsync(id)).Should().Be(rowsBefore);
        var stillAdminPast = await Roles.GetByIdentityAsync(id, new DateOnly(2020, 6, 1));
        stillAdminPast.IsError.Should().BeFalse(DescribeErrors(stillAdminPast.Errors));
        stillAdminPast.Value.IsAdminRole.Should().BeTrue();
        var stillAdminToday = await Roles.GetByIdentityAsync(id, Today);
        stillAdminToday.IsError.Should().BeFalse(DescribeErrors(stillAdminToday.Errors));
        stillAdminToday.Value.IsAdminRole.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_Composite_TurnOffAdmin_Authorized_Succeeds()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("ADM-C-OFF-OK", "Admin c off ok", OpenFrom2020, isAdminRole: true);
        var roleRepo = (RoleRepository)Roles;
        var connections = Connections;
        var composite = new CompositeWrite(connections)
            .Enlist(roleRepo, id)
            .Enlist(RoleRepository.AdminFlagLockKey);

        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.UpsertAsync(
                context, id, TodayOpen, "ADM-C-OFF-OK", "Demoted",
                isAdminRole: false, adminFlagChangeAuthorized: true,
                VersionOperationKind.Edit, new OperationDate(Today), "tester", "demote");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var past = await Roles.GetByIdentityAsync(id, new DateOnly(2020, 6, 1));
        past.IsError.Should().BeFalse(DescribeErrors(past.Errors));
        past.Value.IsAdminRole.Should().BeTrue();
        var current = await Roles.GetByIdentityAsync(id, Today);
        current.IsError.Should().BeFalse(DescribeErrors(current.Errors));
        current.Value.IsAdminRole.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_UnauthorizedOverlapOfAdminAmongMultipleActive_ReturnsForbidden()
    {
        SkipUnlessDbAvailable();

        // Discriminating FR7/FR8: two simultaneously-active overlapping open versions (admin + non-admin).
        // Immediate writers cannot persist the later slice via Upsert, so both are SQL-seeded. The attack
        // period is Immediate-legal (Today+OpenEnd) and overlaps the admin slice covering today.
        var adminPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
        var laterNonAdmin = new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd);

        var id = await CreateRoleAsync("ADM-MULTI", "Multi active", adminPeriod, isAdminRole: true);
        _ = await InsertRoleVersionAsync(
            id, "ADM-MULTI", "Later non-admin", laterNonAdmin, isAdminRole: false);

        var active = await GetActiveRolePeriodsAsync(id);
        active.Should().HaveCount(2);

        var rowsBefore = await CountRoleVersionRowsAsync(id);
        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "ADM-MULTI", "Unauthorized demote", isAdminRole: false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "attack");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.AdminFlagChangeNotAuthorized");
        result.FirstError.Type.Should().Be(ErrorType.Forbidden);
        (await CountRoleVersionRowsAsync(id)).Should().Be(rowsBefore);
        var stillAdmin = await Roles.GetByIdentityAsync(id, new DateOnly(2020, 6, 15));
        stillAdmin.IsError.Should().BeFalse(DescribeErrors(stillAdmin.Errors));
        stillAdmin.Value.IsAdminRole.Should().BeTrue();
    }

    // --- Cancel-plan (N6) — mirrors OrgUnitEditAlgebraTests' own CancelPlanAsync_FutureVersion_*/
    // CancelPlanAsync_AlreadyEffectiveVersion_BLOCKS shape (same test-family pattern), adapted to Role. ---

    [Fact]
    public async Task CancelPlanAsync_FutureVersion_DeactivatesAndMarksCancelled_IdentitySurvives()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync("PLAN-R", "Hiện tại", OpenFrom2020);
        var futureVersionId = await InsertRoleVersionAsync(
            id, "PLAN-R", "Kế hoạch 2027",
            new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd));

        var cancel = await roleRepo.CancelPlanAsync(id, futureVersionId, Today, adminFlagChangeAuthorized: false, "tester", "bỏ kế hoạch");
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var flags = await GetRoleVersionFlagsAsync(futureVersionId);
        flags.IsActive.Should().BeFalse();
        flags.Cancelled.Should().BeTrue();

        var today = await Roles.GetByIdentityAsync(id, Today);
        today.IsError.Should().BeFalse(DescribeErrors(today.Errors));
        today.Value.RoleName.Should().Be("Hiện tại");
    }

    [Fact]
    public async Task CancelPlanAsync_AlreadyEffectiveVersion_BLOCKS()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync("PLAN-RB", "Hiện tại B", OpenFrom2020);
        var current = await Roles.GetByIdentityAsync(id, Today);
        current.IsError.Should().BeFalse(DescribeErrors(current.Errors));

        var cancel = await roleRepo.CancelPlanAsync(id, current.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "thử hủy bản đang hiệu lực");

        cancel.IsError.Should().BeTrue();
        cancel.Errors.Should().Contain(e =>
            e.Type == ErrorType.Validation && e.Code == "VersionedRepository.NotAFuturePlan");
    }

    // Optional "Ghi chú" (role_version.reason is nullable, migrations/V002__role.sql) -- pins the wrapper's
    // `reason ?? string.Empty` coercion so a caller can omit it without CS8622/an exception.
    [Fact]
    public async Task CancelPlanAsync_NullReason_CoercedToEmptyString_Succeeds()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync("PLAN-RN", "Ghi chú rỗng", OpenFrom2020);
        var futureVersionId = await InsertRoleVersionAsync(
            id, "PLAN-RN", "Kế hoạch không ghi chú",
            new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd));

        var cancel = await roleRepo.CancelPlanAsync(id, futureVersionId, Today, adminFlagChangeAuthorized: false, "tester", reason: null);
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var flags = await GetRoleVersionFlagsAsync(futureVersionId);
        flags.IsActive.Should().BeFalse();
        flags.Cancelled.Should().BeTrue();
    }

    // --- Security fix: predecessor-aware admin-flag gate on Cancel.
    // CancelVersionCoreAsync (AST.Infrastructure/VersionedRepository.cs) restores an adjacent predecessor
    // when cancelling a future plan, copying its business columns VERBATIM -- including is_admin_role. An
    // ordinary (non-break-glass) actor cancelling a plain, non-admin future plan could previously restore
    // an admin predecessor to active, open-ended coverage: a cancel could GRANT admin coverage, the exact
    // mirror of what UpsertAsync's bidirectional gate already forbids. Only test (a) DISCRIMINATES the gate
    // -- it is the one that fails if the gate is deleted. (b) and (c) are over-blocking complements: they
    // pin that break-glass authority still gets through and that a non-admin predecessor is not blocked,
    // and both would pass with the gate removed. Do not read this block as "3 gate proofs". ---

    [Fact]
    public async Task CancelPlanAsync_PredecessorIsAdmin_Unauthorized_BLOCKS_StateUnchanged()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync(
            "SEC-ADM1", "Admin retiring", new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)),
            isAdminRole: true);
        var futureVersionId = await InsertRoleVersionAsync(
            id, "SEC-ADM1", "Kế hoạch không admin",
            new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd), isAdminRole: false);

        var cancel = await roleRepo.CancelPlanAsync(
            id, futureVersionId, Today, adminFlagChangeAuthorized: false, "tester", "hủy kế hoạch không admin");

        cancel.IsError.Should().BeTrue();
        cancel.FirstError.Code.Should().Be("Role.AdminFlagChangeNotAuthorized");
        cancel.FirstError.Type.Should().Be(ErrorType.Forbidden);

        // Discriminating: the exploit would leave the predecessor deactivated + a remnant re-inserted, and
        // the target cancelled -- assert neither happened, i.e. the gate actually blocked the write.
        var predecessor = await GetRoleVersionByEffectiveFromAsync(id, new DateOnly(2020, 1, 1));
        predecessor.EffectiveTo.Should().Be(new DateOnly(2026, 12, 31));
        predecessor.IsActive.Should().BeTrue();

        var futureFlags = await GetRoleVersionFlagsAsync(futureVersionId);
        futureFlags.IsActive.Should().BeTrue();
        futureFlags.Cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task CancelPlanAsync_PredecessorIsAdmin_Authorized_RestoresActiveAdminRemnant()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync(
            "SEC-ADM2", "Admin retiring", new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)),
            isAdminRole: true);
        var futureVersionId = await InsertRoleVersionAsync(
            id, "SEC-ADM2", "Kế hoạch không admin",
            new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd), isAdminRole: false);

        var cancel = await roleRepo.CancelPlanAsync(
            id, futureVersionId, Today, adminFlagChangeAuthorized: true, "tester", "break-glass hủy");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var restored = await Roles.GetByIdentityAsync(id, new DateOnly(2030, 1, 1));
        restored.IsError.Should().BeFalse(DescribeErrors(restored.Errors));
        restored.Value.IsActive.Should().BeTrue();
        restored.Value.IsAdminRole.Should().BeTrue();
        restored.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
    }

    [Fact]
    public async Task CancelPlanAsync_PredecessorIsNotAdmin_Unauthorized_Succeeds()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync(
            "SEC-ADM3", "Not admin", new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)),
            isAdminRole: false);
        var futureVersionId = await InsertRoleVersionAsync(
            id, "SEC-ADM3", "Kế hoạch không admin",
            new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd), isAdminRole: false);

        var cancel = await roleRepo.CancelPlanAsync(
            id, futureVersionId, Today, adminFlagChangeAuthorized: false, "tester", "hủy kế hoạch không admin");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var restored = await Roles.GetByIdentityAsync(id, new DateOnly(2030, 1, 1));
        restored.IsError.Should().BeFalse(DescribeErrors(restored.Errors));
        restored.Value.IsAdminRole.Should().BeFalse();
    }

    // =========================================================================================
    // 2026-08-12 — role is Immediate: EVERY writer (not only the service)
    // must reject R1 (forward-dated start) / R2 (bounded end) / R3 (a close date other than
    // today - 1). RED against the current RoleRepository, which has none of these guards yet —
    // the base engine only checks the shrink stays within [From, To) of the target version.
    // =========================================================================================

    public enum ImmediateWriter { RoleComposite, GrantPlain, GrantComposite }

    // Constraint: renaming a past-seeded role is a today-start write (old version closed yesterday),
    // not an in-place rewrite of the running version. Also pins that ValidateUpsertAsync stays inert
    // for Role — a role with existing history remains editable when the write starts today.
    [Fact]
    public async Task UpsertAsync_RenameRoleSeededFromPast_LastMonthKeepsOldName_OldVersionEndsYesterday_TodayHasNewName()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("DEC-PIN", "Tên cũ", OpenFrom2020);
        var lastMonth = Today.AddMonths(-1);
        var yesterday = Today.AddDays(-1);
        var todayOpen = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);

        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, todayOpen, "DEC-PIN", "Tên mới", isAdminRole: false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "rename");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var past = await Roles.GetByIdentityAsync(id, lastMonth);
        past.IsError.Should().BeFalse(DescribeErrors(past.Errors));
        past.Value.RoleName.Should().Be("Tên cũ");
        past.Value.EffectiveTo.Should().Be(yesterday);

        var current = await Roles.GetByIdentityAsync(id, Today);
        current.IsError.Should().BeFalse(DescribeErrors(current.Errors));
        current.Value.RoleName.Should().Be("Tên mới");
        current.Value.EffectiveFrom.Should().Be(Today);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRole_HistoricalStart_IsRejected()
    {
        SkipUnlessDbAvailable();

        var id = await CreateRoleAsync("IMM-INERT", "Vai trò gốc", OpenFrom2020);
        var rowsBefore = await CountRoleVersionRowsAsync(id);

        var composite = new CompositeWrite(Connections).Enlist(Roles, id);
        var result = await composite.ExecuteAsync(async context =>
        {
            var write = await Roles.UpsertAsync(
                context, id, OpenFrom2020, "IMM-INERT", "Vai trò đã đổi tên", isAdminRole: false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "edit");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.EffectiveFromMustBeToday");
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        (await CountRoleVersionRowsAsync(id)).Should().Be(rowsBefore);
        (await Roles.GetByIdentityAsync(id, Today)).Value.RoleName.Should().Be("Vai trò gốc");
    }

    [Theory]
    [InlineData(ImmediateWriter.RoleComposite)]
    [InlineData(ImmediateWriter.GrantPlain)]
    [InlineData(ImmediateWriter.GrantComposite)]
    public async Task No_role_or_grant_writer_can_persist_a_forward_dated_start(ImmediateWriter writer)
    {
        SkipUnlessDbAvailable();

        var forwardDated = new EffectivePeriod(Today.AddDays(30), EffectivePeriod.OpenEnd);

        switch (writer)
        {
            case ImmediateWriter.RoleComposite:
            {
                var id = await Roles.CreateIdentityAsync();
                var composite = new CompositeWrite(Connections).Enlist(Roles, id);
                var compositeResult = await composite.ExecuteAsync(async context =>
                {
                    var write = await Roles.UpsertAsync(
                        context, id, forwardDated, "IMM-RC", "Forward role composite", isAdminRole: false,
                        adminFlagChangeAuthorized: false, VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");
                    return write.IsError ? write.Errors : Result.Success;
                });

                compositeResult.IsError.Should().BeTrue();
                compositeResult.FirstError.Code.Should().Be("Role.EffectiveFromMustBeToday");
                compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);
                (await CountRoleVersionRowsAsync(id)).Should().Be(0);
                break;
            }
            case ImmediateWriter.GrantPlain:
            {
                var role = await CreateRoleAsync("IMM-GP-R", "Role", OpenFrom2020);
                var function = await CreateFunctionAsync("Imm.Gp.Fn", OpenFrom2020);
                var grantId = await RolePermissions.CreateIdentityAsync();
                var result = await RolePermissions.UpsertAsync(
                    grantId, forwardDated, role, function, ScopeLevel.Global, VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");

                result.IsError.Should().BeTrue();
                result.FirstError.Code.Should().Be("RolePermission.EffectiveFromMustBeToday");
                result.FirstError.Type.Should().Be(ErrorType.Validation);
                (await CountRowsAsync("role_permission_version", "role_permission_id", grantId)).Should().Be(0);
                break;
            }
            case ImmediateWriter.GrantComposite:
            {
                var role = await CreateRoleAsync("IMM-GC-R", "Role", OpenFrom2020);
                var function = await CreateFunctionAsync("Imm.Gc.Fn", OpenFrom2020);
                var grantId = await RolePermissions.CreateIdentityAsync();
                var grantRepo = (RolePermissionRepository)RolePermissions;
                var roleRepoForGrant = (RoleRepository)Roles;
                var functionRepo = (FunctionRepository)Functions;
                // role_permission_version has 2 temporal-FK PARENTS (role_id, function_id) — both must
                // be Enlisted up front, same requirement RoleDeclarationService documents on its own
                // composite (CompositeWrite.NotEnlisted otherwise).
                var composite = new CompositeWrite(Connections)
                    .Enlist(grantRepo, grantId)
                    .Enlist(roleRepoForGrant, role)
                    .Enlist(functionRepo, function);
                var compositeResult = await composite.ExecuteAsync(async context =>
                {
                    var write = await grantRepo.UpsertAsync(
                        context, grantId, forwardDated, role, function, ScopeLevel.Global,
                        VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");
                    return write.IsError ? write.Errors : Result.Success;
                });

                compositeResult.IsError.Should().BeTrue();
                compositeResult.FirstError.Code.Should().Be("RolePermission.EffectiveFromMustBeToday");
                compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);
                (await CountRowsAsync("role_permission_version", "role_permission_id", grantId)).Should().Be(0);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(writer), writer, null);
        }
    }

    [Fact]
    public async Task UpsertAsync_Composite_BoundedEnd_ReturnsEffectiveToMustBeOpen_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var id = await Roles.CreateIdentityAsync();
        var roleRepo = (RoleRepository)Roles;
        var boundedEnd = new EffectivePeriod(Today, Today.AddDays(30));
        var composite = new CompositeWrite(Connections).Enlist(roleRepo, id);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.UpsertAsync(
                context, id, boundedEnd, "IMM-BE2", "Bounded role composite", isAdminRole: false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("Role.EffectiveToMustBeOpen");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);
        (await CountRoleVersionRowsAsync(id)).Should().Be(0);
    }

    // Stop path (R3): RoleRepository.CloseVersionAsync's only current guard is the shrink-range
    // check (`newTo` within [From, To) of the target) — no "must equal today - 1" restriction
    // exists yet, so closing to a date 5 days in the FUTURE currently SUCCEEDS.
    [Fact]
    public async Task CloseVersionAsync_EndOtherThanTodayMinusOne_ReturnsCloseDateMustBeImmediate_TargetUnchanged()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync("IMM-CLOSE1", "Vai trò", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(id, Today)).Value.Id;

        var result = await roleRepo.CloseVersionAsync(id, versionId, Today.AddDays(5), new OperationDate(Today), "tester", "close");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.CloseDateMustBeImmediate");
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        var unchanged = await Roles.GetByIdentityAsync(id, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a rejected close must not touch effective_to");
    }

    [Fact]
    public async Task CloseVersionAsync_Composite_EndOtherThanTodayMinusOne_ReturnsCloseDateMustBeImmediate_TargetUnchanged()
    {
        SkipUnlessDbAvailable();

        var roleRepo = (RoleRepository)Roles;
        var id = await CreateRoleAsync("IMM-CLOSE2", "Vai trò", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(id, Today)).Value.Id;
        var composite = new CompositeWrite(Connections).Enlist(roleRepo, id);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await roleRepo.CloseVersionAsync(context, id, versionId, Today.AddDays(5), new OperationDate(Today), "tester", "close");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("Role.CloseDateMustBeImmediate");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);

        var unchanged = await Roles.GetByIdentityAsync(id, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a rejected close must not touch effective_to");
    }

    // Pins the Immediate close guard on the BASE type. The two tests above call through RoleRepository,
    // which would stay green if ValidateClose were moved into a `new CloseVersionAsync` on the concrete
    // type — a base-typed caller would walk straight past that hiding.
    [Fact]
    public async Task CloseVersionAsync_ThroughVersionedRepositoryTypedReference_EndOtherThanTodayMinusOne_ReturnsCloseDateMustBeImmediate_ZeroRowsChanged()
    {
        SkipUnlessDbAvailable();

        VersionedRepository<RoleVersionEntity> baseTyped = (RoleRepository)Roles;
        var id = await CreateRoleAsync("IMM-CLOSE-BASE", "Vai trò", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(id, Today)).Value.Id;
        var rowsBefore = await CountRoleVersionRowsAsync(id);

        var result = await baseTyped.CloseVersionAsync(
            id, versionId, Today.AddDays(5), new OperationDate(Today), "tester", "close");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("Role.CloseDateMustBeImmediate");
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        (await CountRoleVersionRowsAsync(id)).Should().Be(rowsBefore, "a rejected close must not write");

        var unchanged = await Roles.GetByIdentityAsync(id, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a rejected close must not touch effective_to");
    }

    private async Task<long> CountRowsAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {versionTable} WHERE {identityColumn} = @identityId",
            new { identityId });
    }

    private async Task<(DateOnly EffectiveTo, bool IsActive)> GetRoleVersionByEffectiveFromAsync(long roleId, DateOnly effectiveFrom)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<(DateOnly EffectiveTo, bool IsActive)>(
            "SELECT effective_to AS EffectiveTo, isactive AS IsActive FROM role_version WHERE role_id = @roleId AND effective_from = @effectiveFrom",
            new { roleId, effectiveFrom });
    }

    private sealed class VersionFlags
    {
        public bool IsActive { get; init; }
        public bool Cancelled { get; init; }
    }

    private async Task<VersionFlags> GetRoleVersionFlagsAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<VersionFlags>(
            "SELECT isactive AS IsActive, cancelled AS Cancelled FROM role_version WHERE id = @versionId",
            new { versionId });
    }

    private async Task<long> CountRoleVersionRowsAsync(long roleId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM role_version WHERE role_id = @roleId", new { roleId });
    }

    private async Task<List<EffectivePeriod>> GetActiveRolePeriodsAsync(long roleId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            """
            SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo
            FROM role_version
            WHERE role_id = @roleId AND isactive = 1
            ORDER BY effective_from
            """,
            new { roleId });
        return rows.Select(r => new EffectivePeriod(r.EffectiveFrom, r.EffectiveTo)).ToList();
    }
}
