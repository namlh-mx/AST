using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Modules.IAM;
using AST.Modules.IAM.Tests.TestSupport;
using ErrorOr;
using Period = AST.Core.EffectivePeriod.EffectivePeriod;

namespace AST.Modules.IAM.Tests.Integration;

// [Slice C3] Permission resolution as of TODAY (D5) against a real DB. Acceptance criteria (memory
// iam-code-phase-progress): granted -> correct scope_level + rootOrgUnitId; not-granted/unknown-user/
// unsynced-function -> Forbidden; break-glass -> Global; 2 active users with duplicate username -> fails clearly;
// IsFunctionOpen matches.
public sealed class AuthorizationServiceTests : IamRepositoryTestBase
{
    private static readonly Period OpenFrom2020 = new(new DateOnly(2020, 1, 1), Period.OpenEnd);

    // A fake break-glass implementation: configures a list of usernames treated as break-glass (standing in for the real §⑤).
    private sealed class FakeBreakGlassPolicy(params string[] admins) : IBreakGlassPolicy
    {
        private readonly HashSet<string> _admins = new(admins, StringComparer.Ordinal);
        public bool IsBreakGlassAdmin(string username) => _admins.Contains(username);
    }

    private AuthorizationService CreateService(IBreakGlassPolicy? breakGlass = null) =>
        new(Users, Functions, RolePermissions, breakGlass ?? new FakeBreakGlassPolicy(),
            new FixedBusinessDateProvider(Today));

    // Seeds: 1 org unit + 1 role + 1 user (belonging to that org unit/role) — returns (orgUnitId, roleId, username).
    private async Task<(long OrgUnitId, long RoleId, string Username)> SeedUserAsync(string username)
    {
        var orgCode = ("OU" + username).Length <= 8 ? "OU" + username : ("OU" + username)[..8];
        var orgUnitId = await CreateOrgUnitAsync(orgCode, "Đơn vị", orgCode, null, OpenFrom2020);
        var roleId = await CreateRoleAsync($"ROLE-{username}", "Vai trò", OpenFrom2020);
        var userId = await CreateUserHeaderAsync();
        var upsert = await Users.UpsertAsync(
            userId, OpenFrom2020, username, "Người dùng", orgUnitId, roleId, "tester", "seed");
        Assert.False(upsert.IsError, DescribeErrors(upsert.Errors));
        return (orgUnitId, roleId, username);
    }

    private async Task GrantAsync(long roleId, long functionId, ScopeLevel scope)
    {
        await CreateGrantAsync(roleId, functionId, OpenFrom2020, scope, "grant");
    }

    [Fact]
    public async Task AuthorizeAsync_GrantedUser_ReturnsScopeLevelAndRootOrgUnit()
    {
        SkipUnlessDbAvailable();

        var (orgUnitId, roleId, username) = await SeedUserAsync("granted");
        var functionId = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        await GrantAsync(roleId, functionId, ScopeLevel.OwnOrgUnitAndDescendants);

        var result = await CreateService().AuthorizeAsync(username, "Iam.User.View");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Equal(ScopeLevel.OwnOrgUnitAndDescendants, result.Value.Level);
        Assert.Equal(orgUnitId, result.Value.RootOrgUnitId);
        Assert.Equal(username, result.Value.Username);
    }

    [Fact]
    public async Task AuthorizeAsync_NoGrant_ReturnsForbiddenNotGranted()
    {
        SkipUnlessDbAvailable();

        var (_, _, username) = await SeedUserAsync("nogrant");
        await CreateFunctionAsync("Iam.User.Edit", OpenFrom2020); // function is open but NOT granted to the role

        var result = await CreateService().AuthorizeAsync(username, "Iam.User.Edit");

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
        Assert.Equal("Authz.NotGranted", result.FirstError.Code);
    }

    [Fact]
    public async Task AuthorizeAsync_UnknownUsername_ReturnsForbidden()
    {
        SkipUnlessDbAvailable();

        await CreateFunctionAsync("Iam.User.View", OpenFrom2020);

        var result = await CreateService().AuthorizeAsync("khong-ton-tai", "Iam.User.View");

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
    }

    [Fact]
    public async Task AuthorizeAsync_FunctionNotSynced_ReturnsForbidden()
    {
        SkipUnlessDbAvailable();

        var (_, _, username) = await SeedUserAsync("nofunc");

        var result = await CreateService().AuthorizeAsync(username, "Khong.Ton.Tai");

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
    }

    [Fact]
    public async Task AuthorizeAsync_BreakGlass_ReturnsGlobalBypassingGrants()
    {
        SkipUnlessDbAvailable();

        // Break-glass wins BEFORE any DB lookup: the user is not seeded, the function does not exist -> still Global.
        var service = CreateService(new FakeBreakGlassPolicy("rescue-admin"));

        var result = await service.AuthorizeAsync("rescue-admin", "Bat.Ky.Function");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Equal(ScopeLevel.Global, result.Value.Level);
        Assert.Null(result.Value.RootOrgUnitId);
        Assert.Equal("rescue-admin", result.Value.Username);
    }

    [Fact]
    public async Task AuthorizeAsync_DuplicateActiveUsername_FailsClearly()
    {
        SkipUnlessDbAvailable();

        // Two active in-period user identities with a duplicate username -> violates the §1.3 invariant -> fails CLEARLY (not Forbidden).
        await SeedUserAsync("dup");
        var orgUnitId = await CreateOrgUnitAsync("OU-dup2", "Đơn vị 2", "OU-dup2", null, OpenFrom2020);
        var roleId = await CreateRoleAsync("ROLE-dup2", "Vai trò 2", OpenFrom2020);
        var userId2 = await CreateUserHeaderAsync();
        var upsert2 = await Users.UpsertAsync(
            userId2, OpenFrom2020, "dup", "Trùng", orgUnitId, roleId, "tester", "seed-dup");
        Assert.False(upsert2.IsError, DescribeErrors(upsert2.Errors));

        var functionId = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        await GrantAsync(roleId, functionId, ScopeLevel.OwnOrgUnit);

        var result = await CreateService().AuthorizeAsync("dup", "Iam.User.View");

        Assert.True(result.IsError);
        Assert.NotEqual(ErrorType.Forbidden, result.FirstError.Type);
        Assert.Equal("User.DuplicateUsername", result.FirstError.Code);
    }

    [Fact]
    public async Task AuthorizeAsync_DuplicateActiveGrant_FailsClearly()
    {
        SkipUnlessDbAvailable();

        // Two role_permission identities for the same (role,function) with overlapping active periods -> violates the
        // §1.5 invariant -> fails CLEARLY (does NOT silently pick 1 grant, avoiding a non-deterministic scope grant).
        var (_, roleId, username) = await SeedUserAsync("dupgrant");
        var functionId = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        await GrantAsync(roleId, functionId, ScopeLevel.Self);
        await GrantAsync(roleId, functionId, ScopeLevel.Global);

        var result = await CreateService().AuthorizeAsync(username, "Iam.User.View");

        Assert.True(result.IsError);
        Assert.NotEqual(ErrorType.Forbidden, result.FirstError.Type);
        Assert.Equal("RolePermission.DuplicateGrant", result.FirstError.Code);
    }

    [Fact]
    public async Task AuthorizeAsync_GlobalGrant_RootOrgUnitIsNull()
    {
        SkipUnlessDbAvailable();

        // DataScope convention: RootOrgUnitId is null when Global (does not depend on the user's root org unit).
        var (_, roleId, username) = await SeedUserAsync("globalscope");
        var functionId = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        await GrantAsync(roleId, functionId, ScopeLevel.Global);

        var result = await CreateService().AuthorizeAsync(username, "Iam.User.View");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Equal(ScopeLevel.Global, result.Value.Level);
        Assert.Null(result.Value.RootOrgUnitId);
    }

    [Fact]
    public async Task IsFunctionOpenAsync_GrantedTrue_NotGrantedFalse()
    {
        SkipUnlessDbAvailable();

        var (_, roleId, username) = await SeedUserAsync("menu");
        var functionId = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        await GrantAsync(roleId, functionId, ScopeLevel.OwnOrgUnit);
        await CreateFunctionAsync("Iam.User.Edit", OpenFrom2020); // open but not granted

        var service = CreateService();

        Assert.True(await service.IsFunctionOpenAsync(username, "Iam.User.View"));
        Assert.False(await service.IsFunctionOpenAsync(username, "Iam.User.Edit"));
        Assert.False(await service.IsFunctionOpenAsync(username, "Khong.Ton.Tai"));
    }
}
