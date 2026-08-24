using AST.Core.EffectivePeriod;
using AST.Core.Iam;

namespace AST.Modules.IAM.Tests.Integration;

// B2 — STRICT temporal-FK (D8: user_version ⊂ (org_unit_version, role_version)) + Self/
// OwnOrgUnit scope on user_version + sid write-once (Q2/R4). Real concurrency (named-lock race) deferred to B3.
public sealed class UserRepositoryTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod Year2025 = new(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);

    [Fact]
    public async Task UpsertAsync_PeriodWithinParentCoverage_Succeeds()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("U-ORG", "Đơn vị user", "U-ORG", null, OpenFrom2020);
        var role = await CreateRoleAsync("U-ROLE", "Vai trò user", OpenFrom2020);
        var userId = await CreateUserHeaderAsync();

        var result = await Users.UpsertAsync(
            userId, Year2025, "nguyen.van.a", "Nguyễn Văn A", org, role, "tester", "create");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task UpsertAsync_PeriodExceedsOrgUnitCoverage_ReturnsTemporalFkError_AndDoesNotSave()
    {
        SkipUnlessDbAvailable();

        // org_unit is only valid in 2025, but the user is declared valid FROM 2020 (exceeding coverage) -> BLOCKED.
        var org = await CreateOrgUnitAsync("U-ORG2", "Đơn vị hẹp", "U-ORG2", null, Year2025);
        var role = await CreateRoleAsync("U-ROLE2", "Vai trò rộng", OpenFrom2020);
        var userId = await CreateUserHeaderAsync();

        var result = await Users.UpsertAsync(
            userId, OpenFrom2020, "nguyen.van.b", "Nguyễn Văn B", org, role, "tester", "create");

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.ParentGap");

        var fetched = await Users.GetByIdentityAsync(userId, Today);
        Assert.True(fetched.IsError, "Temporal-FK chặn -> KHÔNG được lưu bất kỳ phiên bản nào.");
    }

    [Fact]
    public async Task UpsertAsync_PeriodExceedsRoleCoverage_ReturnsTemporalFkError()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("U-ORG3", "Đơn vị rộng", "U-ORG3", null, OpenFrom2020);
        var role = await CreateRoleAsync("U-ROLE3", "Vai trò hẹp", Year2025);
        var userId = await CreateUserHeaderAsync();

        var result = await Users.UpsertAsync(
            userId, OpenFrom2020, "nguyen.van.c", "Nguyễn Văn C", org, role, "tester", "create");

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.ParentGap");
    }

    [Fact]
    public async Task GetInScopeAsync_Self_ReturnsOwnRecordOnly()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("S-ORG", "Đơn vị self", "S-ORG", null, OpenFrom2020);
        var role = await CreateRoleAsync("S-ROLE", "Vai trò self", OpenFrom2020);
        var userA = await CreateUserHeaderAsync();
        var userB = await CreateUserHeaderAsync();

        Assert.False((await Users.UpsertAsync(userA, OpenFrom2020, "self.a", "A", org, role, "tester", "seed")).IsError);
        Assert.False((await Users.UpsertAsync(userB, OpenFrom2020, "self.b", "B", org, role, "tester", "seed")).IsError);

        var scope = new DataScope(ScopeLevel.Self, null, "self.a");
        var rows = await Users.GetInScopeAsync(scope, Today);

        Assert.Single(rows);
        Assert.Equal("self.a", rows[0].Username);
    }

    [Fact]
    public async Task GetInScopeAsync_OwnOrgUnit_FiltersByExactOrgUnit()
    {
        SkipUnlessDbAvailable();

        var orgX = await CreateOrgUnitAsync("OU-X", "Đơn vị X", "OU-X", null, OpenFrom2020);
        var orgY = await CreateOrgUnitAsync("OU-Y", "Đơn vị Y", "OU-Y", null, OpenFrom2020);
        var role = await CreateRoleAsync("OU-ROLE", "Vai trò", OpenFrom2020);
        var userX = await CreateUserHeaderAsync();
        var userY = await CreateUserHeaderAsync();

        Assert.False((await Users.UpsertAsync(userX, OpenFrom2020, "user.x", "X", orgX, role, "tester", "seed")).IsError);
        Assert.False((await Users.UpsertAsync(userY, OpenFrom2020, "user.y", "Y", orgY, role, "tester", "seed")).IsError);

        var scope = new DataScope(ScopeLevel.OwnOrgUnit, orgX, "tester");
        var rows = await Users.GetInScopeAsync(scope, Today);

        Assert.Contains(rows, r => r.UserId == userX);
        Assert.DoesNotContain(rows, r => r.UserId == userY);
    }

    [Fact]
    public async Task TrySetSidOnceAsync_SetsOnce_AndGuardsAgainstOverwrite()
    {
        SkipUnlessDbAvailable();

        var userId = await CreateUserHeaderAsync();

        var first = await Users.TrySetSidOnceAsync(userId, "S-1-1-11-111");
        Assert.True(first);

        var second = await Users.TrySetSidOnceAsync(userId, "S-1-1-22-222");
        Assert.False(second, "sid write-once: KHÔNG được ghi đè sid đã có.");
    }
}
