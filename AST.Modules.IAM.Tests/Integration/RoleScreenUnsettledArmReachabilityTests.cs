using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using AST.Core.Time;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Brief 167-C (+ fix round 1): production-path probes for six unsettled role-screen Format-map arms
// (operator-messages ┬º1.5j). Every test carries at least one POSITIVE assertion about what happened.
public sealed class RoleScreenUnsettledArmReachabilityTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2021 = new(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));

    private RoleDeclarationService Service => new(
        (RoleRepository)Roles,
        (RolePermissionRepository)RolePermissions,
        (FunctionRepository)Functions,
        Connections,
        new AuditLogWriter(),
        new FakeAuthorizationService(new DataScope(ScopeLevel.Global, null, "tester")),
        new FakeBreakGlassPolicy(),
        new FakeCurrentWindowsUser("tester"),
        new FixedBusinessDateProvider(Today));

    [Fact]
    public async Task ClosePath_InvalidShrink_IsUnreachable_VersionCloseRulesPreEmpts()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("167C-CLOSE-SHRINK", "Vai tr├▓", Year2021);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, new DateOnly(2021, 6, 1))).Value.Id;

        var result = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, roleVersionId, "retire outside period"));

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be(VersionCloseRules.Codes.VersionAlreadyEnded);
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.InvalidShrink");
    }

    [Fact]
    public async Task ClosePath_NotAFuturePlan_InForceClosePersistsRetireThroughYesterday()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("167C-CLOSE-NFP", "Vai tr├▓", OpenFrom2020);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var result = await Service.CloseRoleDeclarationAsync(
            new CloseRoleDeclarationRequest(role, roleVersionId, "retire in-force"));

        result.IsError.Should().BeFalse();
        (await Roles.GetByIdentityAsync(role, Today)).IsError.Should().BeTrue(
            "VersionCloseRules.BranchFor chose Retire ΓåÆ role has no coverage from today");
        var atYesterday = await Roles.GetByIdentityAsync(role, Today.AddDays(-1));
        atYesterday.IsError.Should().BeFalse();
        atYesterday.Value.EffectiveTo.Should().Be(Today.AddDays(-1));
    }

    [Fact]
    public async Task SavePath_InvalidRange_IsUnreachable_NoDateFieldOnRequest()
    {
        SkipUnlessDbAvailable();

        // Fix round 1: SaveRoleDeclarationRequest carries no effective dates; the service always builds
        // EffectivePeriod(today, OpenEnd) before any repository call ΓÇö no production input surface to
        // construct InvalidRange on this path.
        var role = await CreateRoleAsync("167C-SAVE-RANGE", "Vai tr├▓", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var request = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, versionId, "167C-SAVE-RANGE"),
            "167C-SAVE-RANGE",
            "Vai tr├▓ ─æ├ú sß╗¡a",
            false,
            "edit",
            [],
            []);

        var result = await Service.SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse();
        var written = await Roles.GetByIdentityAsync(role, Today);
        written.IsError.Should().BeFalse();
        written.Value.EffectiveFrom.Should().Be(Today);
        written.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
    }

    [Fact]
    public async Task SavePath_NotAFuturePlan_IsUnreachable_StaleSaveSurfacesRoleVersionOutOfDate()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("167C-SAVE-NFP", "Vai tr├▓", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var stale = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, versionId, "167C-SAVE-NFP"),
            "167C-SAVE-NFP",
            "Lß║ºn mß╗Öt",
            false,
            "first",
            [],
            []);
        (await Service.SaveRoleDeclarationAsync(stale)).IsError.Should().BeFalse();

        var freshVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var second = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, versionId, "167C-SAVE-NFP"),
            "167C-SAVE-NFP",
            "Lß║ºn hai vß╗¢i id c┼⌐",
            false,
            "stale",
            [],
            []);

        var result = await Service.SaveRoleDeclarationAsync(second);

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Role.VersionOutOfDate");
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.NotAFuturePlan");
        freshVersionId.Should().NotBe(versionId);
    }

    [Fact]
    public async Task SavePath_InvalidShrink_TodayStartingGrantRevokeTakesCancelPlanBranch()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("167C-SAVE-SHRINK", "Vai tr├▓", OpenFrom2020);
        var function = await CreateFunctionAsync("167C.Fn.One", OpenFrom2020);
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var request = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, versionId, "167C-SAVE-SHRINK"),
            "167C-SAVE-SHRINK",
            "Vai tr├▓",
            false,
            "cancel today grant",
            [grant],
            []);

        var result = await Service.SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse();
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1,
            "VersionCloseRules.BranchFor chose CancelPlan ΓåÆ grant was cancelled, not shrunk");
    }

    [Fact]
    public async Task SavePath_BaseVersionRequired_UNRESOLVED_GapGrantRevokeSucceedsAtGrantLevelOnly()
    {
        SkipUnlessDbAvailable();

        // UNRESOLVED: AutoCutExclusivelyOwnedAsync (parent-shrink) is not exercised by save-revoke;
        // this probe only witnesses grant-level shrink succeeding, not that parent auto-cut was skipped.
        var role = await CreateRoleAsync(
            "167C-SAVE-BVR", "Vai tr├▓ t├íi khai b├ío", new EffectivePeriod(Today.AddDays(-90), Today.AddDays(-30)));
        await InsertRoleVersionAsync(
            role, "167C-SAVE-BVR", "Vai tr├▓ t├íi khai b├ío", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync("167C.Fn.Gap", new EffectivePeriod(Today.AddDays(-90), EffectivePeriod.OpenEnd));
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today.AddDays(-20), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var request = new SaveRoleDeclarationRequest(
            new RoleSaveTarget.ExistingRole(role, versionId, "167C-SAVE-BVR"),
            "167C-SAVE-BVR",
            "Vai tr├▓",
            false,
            "revoke gap grant",
            [grant],
            []);

        var result = await Service.SaveRoleDeclarationAsync(request);

        result.IsError.Should().BeFalse();
        var revoked = await RolePermissions.GetByIdentityAsync(grant, Today.AddDays(-1));
        revoked.IsError.Should().BeFalse();
        revoked.Value.EffectiveTo.Should().Be(Today.AddDays(-1));
    }

    private async Task<int> CountCancelledAsync(string table, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {table} WHERE {identityColumn} = @identityId AND status = 'cancelled'",
            new { identityId });
    }

    private sealed class FakeAuthorizationService(ErrorOr<DataScope> outcome) : IAuthorizationService
    {
        public Task<ErrorOr<DataScope>> AuthorizeAsync(string username, string functionKey) => Task.FromResult(outcome);
        public Task<bool> IsFunctionOpenAsync(string username, string functionKey) => Task.FromResult(!outcome.IsError);
    }

    private sealed class FakeBreakGlassPolicy : IBreakGlassPolicy
    {
        public bool IsBreakGlassAdmin(string username) => false;
    }

    private sealed class FakeCurrentWindowsUser(string? username) : ICurrentWindowsUser
    {
        public string? Username => username;
    }
}
