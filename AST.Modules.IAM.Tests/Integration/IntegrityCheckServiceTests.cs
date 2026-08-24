using AST.Core.EffectivePeriod;
using FluentAssertions;
using AST.Core.Iam;
using Dapper;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// B3 -- the integrity-check grid (§12 docs/design-effective-period.md, C1 + [R3] duplicate-natural-key
// addition). For EACH violation type: INSERT bad data via direct SQL (bypassing the repo -- because the
// repo/effective-period engine CORRECTLY blocks these cases, so we must work around it to build the bad
// state we need to detect), then assert that IIntegrityCheckService.RunAllChecksAsync() DETECTS the right kind + identity.
public sealed class IntegrityCheckServiceTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2025 = new(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

    [Fact]
    public async Task RunAllChecksAsync_CleanDatabase_ReturnsEmpty()
    {
        SkipUnlessDbAvailable();

        // Valid data built through the normal repo (the 8-case algebra guarantees no overlap/coverage gap).
        var root = await CreateOrgUnitAsync("CLNROOT", "Gốc sạch", "CLNROOT", null, OpenFrom2020);
        _ = await CreateOrgUnitAsync("CLNCHILD", "Con sạch", "CLNCHILD", root, OpenFrom2020);
        _ = await CreateRoleAsync("CLEAN-ROLE", "Vai trò sạch", OpenFrom2020);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Empty(violations);
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsOverlappingActivePeriods()
    {
        SkipUnlessDbAvailable();

        // Built through the repo (1 valid active version), then INSERTS ONE MORE active version with an
        // intersecting period via raw SQL -- bypassing the repo (the repo/PeriodEditor never creates this case on its own).
        var id = await CreateOrgUnitAsync("OVL", "Đơn vị chồng lấn", "OVL", null, OpenFrom2020);
        await InsertRawOrgUnitVersionAsync(id, "OVL", "Đơn vị chồng lấn (bản ma)", null, Year2025, isActive: true);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Contains(violations, v =>
            v.Kind == IntegrityViolationKind.OverlappingActivePeriods &&
            v.Table == "org_unit_version" &&
            v.IdentityId == id);
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsParentCoverageGap()
    {
        SkipUnlessDbAvailable();

        // The parent is only valid in 2025; INSERTS the child DIRECTLY with a WIDER period (2020->open) via raw SQL --
        // bypassing ValidateChildCoverage (a normal UpsertAsync would BLOCK this case, as designed per D8).
        var narrowParent = await CreateOrgUnitAsync("GAPPAR", "Cha hẹp", "GAPPAR", null, Year2025);
        var childHeaderId = await InsertHeaderAsync("org_unit");
        await InsertRawOrgUnitVersionAsync(childHeaderId, "GAPCHILD", "Con hở phủ", narrowParent, OpenFrom2020, isActive: true);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Contains(violations, v =>
            v.Kind == IntegrityViolationKind.ParentCoverageGap &&
            v.Table == "org_unit_version" &&
            v.IdentityId == childHeaderId);
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsOrphanedChild()
    {
        SkipUnlessDbAvailable();

        // The child points to a parent_id that does NOT exist -- a normal DB FK would block this (fk_ouv_parent), so we
        // must temporarily disable FOREIGN_KEY_CHECKS to build this case (simulating directly tampered-with data).
        const long nonExistentParentId = 999_999_999;
        var childHeaderId = await InsertHeaderAsync("org_unit");
        await InsertRawOrgUnitVersionBypassingFkAsync(childHeaderId, "ORPHAN", "Con mồ côi", nonExistentParentId, OpenFrom2020);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Contains(violations, v =>
            v.Kind == IntegrityViolationKind.OrphanedChild &&
            v.Table == "org_unit_version" &&
            v.IdentityId == childHeaderId);
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsDuplicateNaturalKey_OrgUnitCode()
    {
        SkipUnlessDbAvailable();

        // The repo DOES check org_code uniqueness at the app layer now (P6, B1 T4) -- so this violation can no
        // longer be built through the normal repo (it BLOCKS with OrgUnit.CodeInUse). The integrity check remains
        // defense-in-depth for tampered/legacy rows that bypassed the app layer -- built here via raw SQL, same as
        // the other violation kinds in this file (per the R3 note: "org_code" only has a regular index, not UNIQUE,
        // so MySQL itself cannot enforce this).
        var first = await CreateOrgUnitAsync("DUP-MA", "Đơn vị A", "DUP-MA", null, OpenFrom2020);
        var second = await InsertHeaderAsync("org_unit");
        await InsertRawOrgUnitVersionAsync(second, "DUP-MA", "Đơn vị B (trùng mã)", null, OpenFrom2020, isActive: true);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Contains(violations, v =>
            v.Kind == IntegrityViolationKind.DuplicateNaturalKey &&
            v.Table == "org_unit_version" &&
            (v.IdentityId == first || v.IdentityId == second));
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsDuplicateNaturalKey_UsernameCaseInsensitive()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("DUPUORG", "Đơn vị", "DUPUORG", null, OpenFrom2020);
        var role = await CreateRoleAsync("DUP-U-ROLE", "Vai trò", OpenFrom2020);
        var userA = await CreateUserHeaderAsync();
        var userB = await CreateUserHeaderAsync();

        Assert.False((await Users.UpsertAsync(userA, OpenFrom2020, "dup.user", "A", org, role, "tester", "seed")).IsError);
        // Different case -- collation utf8mb4_0900_ai_ci is case-insensitive => still counted as a duplicate.
        Assert.False((await Users.UpsertAsync(userB, OpenFrom2020, "DUP.USER", "B", org, role, "tester", "seed")).IsError);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        Assert.Contains(violations, v =>
            v.Kind == IntegrityViolationKind.DuplicateNaturalKey &&
            v.Table == "user_version" &&
            (v.IdentityId == userA || v.IdentityId == userB));
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsDuplicateNaturalKey_RoleCode()
    {
        SkipUnlessDbAvailable();

        var first = await CreateRoleAsync("DUP-RC", "Role A", OpenFrom2020);
        var second = await Roles.CreateIdentityAsync();
        await InsertRawRoleVersionAsync(second, "DUP-RC", "Role B", OpenFrom2020, isAdminRole: false);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        violations.Should().Contain(v =>
            v.Kind == IntegrityViolationKind.DuplicateNaturalKey &&
            v.Table == "role_version" &&
            (v.IdentityId == first || v.IdentityId == second));
    }

    [Fact]
    public async Task RunAllChecksAsync_DetectsDuplicateAdminFlagRoles_OnSameDay()
    {
        SkipUnlessDbAvailable();

        var first = await CreateRoleAsync("ADM-A", "Admin A", OpenFrom2020, isAdminRole: true);
        var second = await Roles.CreateIdentityAsync();
        await InsertRawRoleVersionAsync(second, "ADM-B", "Admin B", OpenFrom2020, isAdminRole: true);

        var violations = await IntegrityChecks.RunAllChecksAsync();

        violations.Should().Contain(v =>
            v.Kind == IntegrityViolationKind.DuplicateNaturalKey &&
            v.Table == "role_version" &&
            v.Detail.Contains("is_admin_role") &&
            (v.IdentityId == first || v.IdentityId == second));
    }

    private async Task InsertRawRoleVersionAsync(
        long roleId, string roleCode, string roleName, EffectivePeriod period, bool isAdminRole)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO role_version
                (role_id, role_code, role_name, is_admin_role, effective_from, effective_to, isactive, recorded_by, reason)
            VALUES
                (@roleId, @roleCode, @roleName, @isAdminRole, @from, @to, 1, 'tester-raw', 'integrity-test-seed')
            """,
            new { roleId, roleCode, roleName, isAdminRole, from = period.From, to = period.To });
    }
    private async Task InsertRawOrgUnitVersionAsync(
        long orgUnitId, string orgCode, string orgNameFullVn, long? parentId, EffectivePeriod period, bool isActive)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, parent_id, effective_from, effective_to, isactive, recorded_by, reason)
            VALUES
                (@orgUnitId, @orgCode, @orgNameFullVn, @orgCode, @parentId, @from, @to, @isActive, 'tester-raw', 'integrity-test-seed')
            """,
            new { orgUnitId, orgCode, orgNameFullVn, parentId, from = period.From, to = period.To, isActive });
    }

    private async Task InsertRawOrgUnitVersionBypassingFkAsync(
        long orgUnitId, string orgCode, string orgNameFullVn, long? parentId, EffectivePeriod period)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS=0;");
        await connection.ExecuteAsync(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, parent_id, effective_from, effective_to, isactive, recorded_by, reason)
            VALUES
                (@orgUnitId, @orgCode, @orgNameFullVn, @orgCode, @parentId, @from, @to, 1, 'tester-raw', 'integrity-test-seed')
            """,
            new { orgUnitId, orgCode, orgNameFullVn, parentId, from = period.From, to = period.To });
        await connection.ExecuteAsync("SET FOREIGN_KEY_CHECKS=1;");
    }
}
