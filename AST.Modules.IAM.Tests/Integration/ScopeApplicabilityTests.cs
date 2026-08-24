using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;

namespace AST.Modules.IAM.Tests.Integration;

// Slice B.1 (#3) — blocks MEANINGLESS scope x entity combinations. role/function/role_permission belong to
// NO org unit and have NO owner -> calling Self/OwnOrgUnit/OwnOrgUnitAndDescendants must THROW a clear error
// (InvalidOperationException), NOT silently return empty (VersionedRepository.EnsureScopeApplicable).
// Global still works correctly. The guard runs BEFORE touching the DB, so the THROW cases need no seeded data.
public sealed class ScopeApplicabilityTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);

    public static IEnumerable<object[]> InapplicableScopeLevels =>
    [
        [ScopeLevel.Self],
        [ScopeLevel.OwnOrgUnit],
        [ScopeLevel.OwnOrgUnitAndDescendants],
    ];

    [Theory]
    [MemberData(nameof(InapplicableScopeLevels))]
    public async Task Roles_InapplicableScope_Throws(ScopeLevel level)
    {
        SkipUnlessDbAvailable();

        var scope = new DataScope(level, 1, "tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Roles.GetInScopeAsync(scope, Today));
    }

    [Theory]
    [MemberData(nameof(InapplicableScopeLevels))]
    public async Task Functions_InapplicableScope_Throws(ScopeLevel level)
    {
        SkipUnlessDbAvailable();

        var scope = new DataScope(level, 1, "tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Functions.GetInScopeAsync(scope, Today));
    }

    [Theory]
    [MemberData(nameof(InapplicableScopeLevels))]
    public async Task RolePermissions_InapplicableScope_Throws(ScopeLevel level)
    {
        SkipUnlessDbAvailable();

        var scope = new DataScope(level, 1, "tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RolePermissions.GetInScopeAsync(scope, Today));
    }

    [Fact]
    public async Task Roles_GlobalScope_StillReturnsData()
    {
        SkipUnlessDbAvailable();

        await CreateRoleAsync("G-ROLE", "Vai trò global", OpenFrom2020);

        var rows = await Roles.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);

        Assert.Contains(rows, r => r.RoleCode == "G-ROLE");
    }

    [Fact]
    public async Task Functions_GlobalScope_StillReturnsData()
    {
        SkipUnlessDbAvailable();

        await CreateFunctionAsync("Iam.Role.View", OpenFrom2020);

        var rows = await Functions.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);

        Assert.Contains(rows, f => f.FunctionKey == "Iam.Role.View");
    }

    [Fact]
    public async Task RolePermissions_GlobalScope_StillReturnsData()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("RP-G", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        var rpId = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var rows = await RolePermissions.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);

        Assert.Contains(rows, rp => rp.RolePermissionId == rpId);
    }
}
