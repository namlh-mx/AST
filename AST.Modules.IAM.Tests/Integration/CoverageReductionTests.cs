using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Slice C1 — coverage-REDUCING operations (close/shrink a period + delete a version) on the base repo: reverse-FK
// (D8) BLOCKS when a child loses coverage; A BASE VERSION MUST ALWAYS EXIST; gap warnings (D7). Uses org_unit (which
// is a PARENT of both user + its own child org_unit) as the entity under test.
public sealed class CoverageReductionTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DateOnly In2021 = new(2021, 1, 1);
    private static readonly DateOnly In2023 = new(2023, 1, 1);

    // --- Shrink/close a period (Close) ---

    [Fact]
    public async Task CloseVersion_ChildLosesCoverage_BlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CR-ROLE", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("CR-ORG", "Đơn vị", "CR-ORG", null, OpenFrom2020);  // [2020, open]
        var user = await CreateUserHeaderAsync();
        Assert.False((await Users.UpsertAsync(user, OpenFrom2020, "cr.u", "U", org, role, "tester", "seed")).IsError);

        var orgVersionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        // Shrinks the org unit to end at 2022 -> the user still needs coverage for [2023, open] -> BLOCKED.
        var result = await OrgUnits.CloseVersionAsync(org, orgVersionId, new DateOnly(2022, 12, 31), new OperationDate(Today), "tester", "close");

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.DependentsUncovered");
    }

    [Fact]
    public async Task CloseVersion_ChildStillCovered_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CS-ROLE", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("CS-ORG", "Đơn vị", "CS-ORG", null, OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        var userPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31));
        Assert.False((await Users.UpsertAsync(user, userPeriod, "cs.u", "U", org, role, "tester", "seed")).IsError);

        var orgVersionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        // Shrinks the org unit to end at 2022 -> user [2020,2021] is still ⊆ [2020,2022] -> OK.
        var result = await OrgUnits.CloseVersionAsync(org, orgVersionId, new DateOnly(2022, 12, 31), new OperationDate(Today), "tester", "close");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        var afterClose = await OrgUnits.GetByIdentityAsync(org, In2021);
        Assert.False(afterClose.IsError, DescribeErrors(afterClose.Errors));
        Assert.Equal(new DateOnly(2022, 12, 31), afterClose.Value.EffectiveTo);
    }

    [Fact]
    public async Task CloseVersion_LeavesGap_ReturnsGapWarning()
    {
        SkipUnlessDbAvailable();

        // 2 adjacent periods [2020,2021] + [2022, open]; shrinks the first to end at 30/06/2020 -> gap [01/07/2020, 2021].
        var org = await CreateOrgUnitAsync("GAP-ORG", "Đơn vị", "GAP-ORG", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31)));
        Assert.False((await OrgUnits.UpsertAsync(
            org, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd), "GAP-ORG", "Đơn vị", "GAP-ORG",
            null, VersionOperationKind.Edit, "tester", "seed")).IsError);

        var firstVersionId = (await OrgUnits.GetByIdentityAsync(org, In2021)).Value.Id;

        var result = await OrgUnits.CloseVersionAsync(org, firstVersionId, new DateOnly(2020, 6, 30), new OperationDate(Today), "tester", "close");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.NotEmpty(result.Value.Warnings);
    }

    // --- Delete a single period (Delete) ---

    [Fact]
    public async Task DeleteVersion_OnlyActiveVersion_BlockedByBaseVersionRequired()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("DB-ORG", "Đơn vị", "DB-ORG", null, OpenFrom2020);
        var versionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var result = await OrgUnits.DeleteVersionAsync(org, versionId);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "VersionedRepository.BaseVersionRequired");
    }

    [Fact]
    public async Task DeleteVersion_ChildLosesCoverage_BlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("DR-ROLE", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("DR-ORG", "Đơn vị", "DR-ORG", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31)));
        Assert.False((await OrgUnits.UpsertAsync(
            org, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd), "DR-ORG", "Đơn vị", "DR-ORG",
            null, VersionOperationKind.Edit, "tester", "seed")).IsError);

        var user = await CreateUserHeaderAsync();
        Assert.False((await Users.UpsertAsync(user, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd), "dr.u", "U", org, role, "tester", "seed")).IsError);

        var version2022 = (await OrgUnits.GetByIdentityAsync(org, In2023)).Value.Id;
        var result = await OrgUnits.DeleteVersionAsync(org, version2022);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.DependentsUncovered");
    }

    [Fact]
    public async Task DeleteVersion_NoDependents_Succeeds()
    {
        SkipUnlessDbAvailable();

        var org = await CreateOrgUnitAsync("DS-ORG", "Đơn vị", "DS-ORG", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31)));
        Assert.False((await OrgUnits.UpsertAsync(
            org, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd), "DS-ORG", "Đơn vị", "DS-ORG",
            null, VersionOperationKind.Edit, "tester", "seed")).IsError);

        var version2022 = (await OrgUnits.GetByIdentityAsync(org, In2023)).Value.Id;
        var result = await OrgUnits.DeleteVersionAsync(org, version2022);

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        // What remains is [2020,2021]; at 2023 there is no more coverage.
        Assert.True((await OrgUnits.GetByIdentityAsync(org, In2023)).IsError);
        Assert.False((await OrgUnits.GetByIdentityAsync(org, In2021)).IsError);
    }

    // --- Self-edge org_unit(parent_id): closing/deleting a period on the PARENT org unit causes the CHILD org unit
    //     to lose coverage (C1's headline scenario — self-referencing temporal-FK). ---

    [Fact]
    public async Task CloseVersion_ChildOrgUnitLosesCoverage_BlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("SEC-P", "Đơn vị cha", "SEC-P", null, OpenFrom2020);   // [2020, open]
        await CreateOrgUnitAsync("SEC-C", "Đơn vị con", "SEC-C", parent, OpenFrom2020);              // child [2020, open] under the parent
        var parentVersionId = (await OrgUnits.GetByIdentityAsync(parent, Today)).Value.Id;

        // Shrinks the parent to end at 2022 -> the child org unit still needs coverage for [2023, open] -> BLOCKED.
        var result = await OrgUnits.CloseVersionAsync(parent, parentVersionId, new DateOnly(2022, 12, 31), new OperationDate(Today), "tester", "close");

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.DependentsUncovered");
    }

    [Fact]
    public async Task DeleteVersion_ChildOrgUnitLosesCoverage_BlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync("SED-P", "Đơn vị cha", "SED-P", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31)));
        Assert.False((await OrgUnits.UpsertAsync(
            parent, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd), "SED-P", "Đơn vị cha",
            "SED-P", null, VersionOperationKind.Edit, "tester", "seed")).IsError);
        await CreateOrgUnitAsync("SED-C", "Đơn vị con", "SED-C", parent, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd));

        var parentVersion2022 = (await OrgUnits.GetByIdentityAsync(parent, In2023)).Value.Id;
        var result = await OrgUnits.DeleteVersionAsync(parent, parentVersion2022);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.DependentsUncovered");
    }
}
