using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam.Repositories;
using Dapper;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// B1 T3+ — org-unit edit-algebra additions beyond the base 8-case suite already covered in
// OrgUnitRepositoryTests.cs: the optional §2.4 supplemental catalog (this file), P6 code uniqueness,
// cancel-plan, and the H2/temporal-FK matrix land here in later tasks (T4-T6).
public sealed class OrgUnitEditAlgebraTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);

    [Fact]
    public async Task UpsertAsync_PersistsSupplemental_AndCopiesToRemnantOnCut()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("ROOT-X", "Gốc X", "Gốc X", null, OpenFrom2020);
        var supp = new OrgUnitSupplementalDto(
            BusinessNumber: "0101234567", AddrLineVn: "12 Lê Lợi", AddrLineEn: "12 Le Loi",
            AddrWardVn: "P.Bến Nghé", AddrProvinceVn: "TP.HCM", AdminDivisionLevel: 2,
            NameFullEn: "Unit X", NameShortEn: "X", Phone: "0281234567", Email: "x@example.vn");

        var id = await InsertHeaderAsync("org_unit");
        var r = await OrgUnits.UpsertAsync(
            id, OpenFrom2020, "UX", "Đơn vị X", "X", root, VersionOperationKind.Add, "tester", "create",
            supplemental: supp);
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var back = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(back.IsError, DescribeErrors(back.Errors));
        Assert.Equal("0101234567", back.Value.Supplemental.BusinessNumber);
        Assert.Equal("12 Lê Lợi", back.Value.Supplemental.AddrLineVn);
        Assert.Equal("x@example.vn", back.Value.Supplemental.Email);
        Assert.Equal((byte)2, back.Value.Supplemental.AdminDivisionLevel);

        // Cuts into the head with NEW core business data but WITHOUT re-supplying supplemental --
        // the remnant [2020-01-01, cut-1] must still carry the ORIGINAL supplemental data verbatim,
        // proving the plain BusinessColumns INSERT...SELECT remnant-copy path covers these columns too.
        var cut = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2025, 6, 1), EffectivePeriod.OpenEnd),
            "UX", "Đơn vị X (2025)", "X2", root, VersionOperationKind.Edit, "tester", "rename-2025");
        Assert.False(cut.IsError, DescribeErrors(cut.Errors));

        var remnant = await OrgUnits.GetByIdentityAsync(id, new DateOnly(2020, 6, 1));
        Assert.False(remnant.IsError, DescribeErrors(remnant.Errors));
        Assert.Equal("0101234567", remnant.Value.Supplemental.BusinessNumber);
        Assert.Equal("x@example.vn", remnant.Value.Supplemental.Email);
    }

    [Fact]
    public async Task UpsertAsync_NoSupplemental_DefaultsToEmpty_WithAdminDivisionLevelTwo()
    {
        SkipUnlessDbAvailable();

        var id = await InsertHeaderAsync("org_unit");
        var r = await OrgUnits.UpsertAsync(
            id, OpenFrom2020, "NOSUP", "Không phụ", "Không phụ", null, VersionOperationKind.Add, "tester", "create");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var back = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(back.IsError, DescribeErrors(back.Errors));
        Assert.Null(back.Value.Supplemental.BusinessNumber);
        Assert.Null(back.Value.Supplemental.Email);
        Assert.Equal((byte)2, back.Value.Supplemental.AdminDivisionLevel);
    }

    // P6: a code may not be shared by two DIFFERENT identities while their periods overlap.
    [Fact]
    public async Task UpsertAsync_DuplicateCode_OverlappingEffective_BLOCKS()
    {
        SkipUnlessDbAvailable();

        await CreateOrgUnitAsync("DUP", "Đơn vị 1", "1", null, OpenFrom2020);
        var other = await InsertHeaderAsync("org_unit");

        var r = await OrgUnits.UpsertAsync(
            other, OpenFrom2020, "DUP", "Đơn vị 2", "2", null, VersionOperationKind.Add, "tester", "create");

        Assert.True(r.IsError);
        Assert.Contains(r.Errors, e => e.Type == ErrorOr.ErrorType.Validation && e.Code == "OrgUnit.CodeInUse");
    }

    // P6: reuse of a code IS allowed once the prior identity's version no longer overlaps (periods disjoint).
    [Fact]
    public async Task UpsertAsync_ReuseCode_AfterPriorEnded_ALLOWED()
    {
        SkipUnlessDbAvailable();

        await CreateOrgUnitAsync(
            "REU", "Cũ", "Cũ", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));
        var next = await InsertHeaderAsync("org_unit");

        var r = await OrgUnits.UpsertAsync(
            next, new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd),
            "REU", "Mới", "Mới", null, VersionOperationKind.Add, "tester", "reuse");

        Assert.False(r.IsError, DescribeErrors(r.Errors));
    }

    // P6: the check must exclude the identity's OWN other versions (editing the same identity is
    // never a "duplicate" against itself), so extending the SAME identity's coverage with its own code succeeds.
    [Fact]
    public async Task UpsertAsync_SameIdentity_SameCode_ExtendingOwnPeriod_ALLOWED()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync(
            "SELF", "Chính nó", "Chính nó", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 6, 1), EffectivePeriod.OpenEnd),
            "SELF", "Chính nó (sửa)", "Chính nó", null, VersionOperationKind.Edit, "tester", "extend");

        Assert.False(r.IsError, DescribeErrors(r.Errors));
    }

    // N6/§8 #10: closing a FUTURE (not-yet-effective) version cancels that plan -- isactive=0 AND status='cancelled' --
    // while the identity's currently-EFFECTIVE version is untouched and still resolves normally.
    [Fact]
    public async Task CancelPlanAsync_FutureVersion_DeactivatesAndMarksCancelled_IdentitySurvives()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("PLAN", "Hiện tại", "HT", null, OpenFrom2020);
        // A future phase (From > business today = 2026-07-03, per IamRepositoryTestBase.Today).
        var future = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            "PLAN", "Kế hoạch 2027", "KH27", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        var flags = await GetVersionFlagsAsync(future.Value.NewVersionId);
        Assert.False(flags.IsActive);
        Assert.True(flags.Cancelled);

        // The identity is still resolvable today via its (untouched) effective version.
        var today = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(today.IsError, DescribeErrors(today.Errors));
        Assert.Equal("HT", today.Value.OrgNameShortVn);
    }

    // Guard: cancel-plan is only for a version with NO completed effective day -- a version that has been
    // effective since 2020 is retired via CloseVersionAsync, not cancelled. Cancelling it here must BLOCK,
    // not silently deactivate live data. (The exact one-day boundary is pinned by the D1 tests below.)
    [Fact]
    public async Task CancelPlanAsync_AlreadyEffectiveVersion_BLOCKS()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("PLANB", "Hiện tại B", "HTB", null, OpenFrom2020);
        var current = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(current.IsError, DescribeErrors(current.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, current.Value.Id, Today, "tester", "thử hủy bản đang hiệu lực");

        Assert.True(cancel.IsError);
        Assert.Contains(cancel.Errors, e =>
            e.Type == ErrorOr.ErrorType.Validation && e.Code == "VersionedRepository.NotAFuturePlan");
    }

    // review Finding 1 (2026-07-22): cancelling a future plan that OVERLAP-CUT the previously
    // open-ended effective version must RESTORE that version's original coverage -- otherwise the
    // identity is left silently truncated (e.g. expiring end-2026) even though nobody asked to shrink it.
    // Requester decision 2026-07-22: auto-restore (not block).
    [Fact]
    public async Task CancelPlanAsync_FutureVersion_OverlapCut_RestoresPredecessorCoverage()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RESTORE", "Khôi phục", "KP", null, OpenFrom2020);
        // This future plan OVERLAPS the open-ended base -> cuts it to [2020-01-01, 2026-12-31] (case 4).
        var future = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            "RESTORE", "Kế hoạch 2027", "KH27", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        var beforeCancel = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [
                new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)),
                new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            ],
            beforeCancel.OrderBy(p => p.From));

        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        // The predecessor's coverage must be restored to its ORIGINAL open end, not left truncated at 2026.
        var afterCancel = await GetActivePeriodsAsync(id);
        Assert.Equal([OpenFrom2020], afterCancel);

        var farFuture = await OrgUnits.GetByIdentityAsync(id, new DateOnly(3000, 1, 1));
        Assert.False(farFuture.IsError, DescribeErrors(farFuture.Errors));
        Assert.Equal("KP", farFuture.Value.OrgNameShortVn);
    }

    // Note (2026-07-22): a "child still covered after parent cancel-plan" regression was considered
    // here but dropped -- GetByIdentityAsync resolves ONLY the queried identity's own row (no parent
    // join), so it would pass unconditionally regardless of whether the parent's coverage was
    // restored, giving false confidence. The predecessor-coverage assertion above is the real check:
    // once the parent's own coverage is provably restored to its original extent, no child can be
    // under-covered by construction, without needing a read-time cross-entity check that doesn't exist.

    // 8-case algebra matrix (docs/design-effective-period.md §4) locked down explicitly for org-unit,
    // beyond the case-1 (disjoint -> BLOCK, P4) and the fresh-insert/head-cut cases already covered in
    // OrgUnitRepositoryTests.cs. Each case starts from its own fresh identity (no cross-case state).
    private static readonly EffectivePeriod ExistingBase = new(new DateOnly(2020, 3, 1), new DateOnly(2020, 6, 30));

    // Case 2 — Adjacent (F=b+1): both periods stay active, seamless, no gap.
    [Fact]
    public async Task UpsertAsync_AlgebraCase2_Adjacent_BothPeriodsStayActive()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG2", "Case 2", "Case 2", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "ALG2", "Case 2 (edited)", "Case 2", null, VersionOperationKind.Edit, "tester", "case2");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [ExistingBase, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd)],
            active.OrderBy(p => p.From));
    }

    // Case 3 — Overlaps head (F<=a<=T<b): old soft-deleted; tail remnant [T+1,b]; new inserted.
    [Fact]
    public async Task UpsertAsync_AlgebraCase3_OverlapsHead_TailRemnantPlusNew()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG3", "Case 3", "Case 3", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 4, 30)),
            "ALG3", "Case 3 (edited)", "Case 3", null, VersionOperationKind.Edit, "tester", "case3");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [
                new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 4, 30)),
                new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2020, 6, 30)), // tail remnant
            ],
            active.OrderBy(p => p.From));
    }

    // Case 4 — Overlaps tail (a<F<=b<=T): old soft-deleted; head remnant [a,F-1]; new inserted.
    [Fact]
    public async Task UpsertAsync_AlgebraCase4_OverlapsTail_HeadRemnantPlusNew()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG4", "Case 4", "Case 4", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2021, 12, 31)),
            "ALG4", "Case 4 (edited)", "Case 4", null, VersionOperationKind.Edit, "tester", "case4");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [
                new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 4, 30)), // head remnant
                new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2021, 12, 31)),
            ],
            active.OrderBy(p => p.From));
    }

    // Case 5 — Sub-period (a<F and T<b): old soft-deleted; splits into head + tail remnants + new (3 active periods).
    [Fact]
    public async Task UpsertAsync_AlgebraCase5_SubPeriod_SplitsIntoHeadAndTailRemnants()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG5", "Case 5", "Case 5", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 4, 1), new DateOnly(2020, 5, 31)),
            "ALG5", "Case 5 (edited)", "Case 5", null, VersionOperationKind.Edit, "tester", "case5");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [
                new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 3, 31)), // head remnant
                new EffectivePeriod(new DateOnly(2020, 4, 1), new DateOnly(2020, 5, 31)),
                new EffectivePeriod(new DateOnly(2020, 6, 1), new DateOnly(2020, 6, 30)), // tail remnant
            ],
            active.OrderBy(p => p.From));
    }

    // Case 6 — New engulfs old (F<=a and b<=T): old soft-deleted with NO remnant; new inserted alone.
    [Fact]
    public async Task UpsertAsync_AlgebraCase6_NewEngulfsOld_NoRemnant()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG6", "Case 6", "Case 6", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31)),
            "ALG6", "Case 6 (edited)", "Case 6", null, VersionOperationKind.Edit, "tester", "case6");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal([new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2021, 12, 31))], active);
    }

    // Case 7 — Exact match (F=a and T=b): a correction -- soft-delete + insert the SAME period, new business data.
    [Fact]
    public async Task UpsertAsync_AlgebraCase7_ExactMatch_CorrectionSamePeriod()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("ALG7", "Case 7", "Case 7", null, ExistingBase);
        var r = await OrgUnits.UpsertAsync(
            id, ExistingBase, "ALG7", "Case 7 (corrected)", "Case 7", null, VersionOperationKind.Edit, "tester", "case7");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal([ExistingBase], active);

        var dto = await OrgUnits.GetByIdentityAsync(id, new DateOnly(2020, 4, 1));
        Assert.Equal("Case 7 (corrected)", dto.Value.OrgNameFullVn);
    }

    // Case 8 — Overlaps multiple periods: 2 pre-existing ADJACENT active periods, new period spans across both
    // (tail-overlap of the first + head-overlap of the second) -> 2 remnants + 1 new insert, 3 active periods total.
    [Fact]
    public async Task UpsertAsync_AlgebraCase8_OverlapsMultiplePeriods_TwoRemnantsPlusNew()
    {
        SkipUnlessDbAvailable();

        var p1 = new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 6, 30));
        var p2 = new EffectivePeriod(new DateOnly(2020, 7, 1), new DateOnly(2020, 10, 31)); // adjacent to p1

        var id = await CreateOrgUnitAsync("ALG8", "Case 8", "Case 8", null, p1);
        var seedAdjacent = await OrgUnits.UpsertAsync(
            id, p2, "ALG8", "Case 8 (2nd phase)", "Case 8", null, VersionOperationKind.Edit, "tester", "seed-p2");
        Assert.False(seedAdjacent.IsError, DescribeErrors(seedAdjacent.Errors));

        var r = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2020, 9, 30)),
            "ALG8", "Case 8 (spanning edit)", "Case 8", null, VersionOperationKind.Edit, "tester", "case8");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var active = await GetActivePeriodsAsync(id);
        Assert.Equal(
            [
                new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 4, 30)), // p1 head remnant
                new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2020, 9, 30)),
                new EffectivePeriod(new DateOnly(2020, 10, 1), new DateOnly(2020, 10, 31)), // p2 tail remnant
            ],
            active.OrderBy(p => p.From));
    }

    // H2 (N9): the warn-before-save affected list -- isactive=1 versions whose period OVERLAPS the
    // candidate save period. Disjoint/gap-adjacent -> empty (clean append); any overlap -> non-empty.
    [Theory]
    [InlineData("2019-01-01", "2019-12-31", 0)] // disjoint before -> clean
    [InlineData("2021-01-01", "2021-12-31", 0)] // disjoint after (gap-free adjacency not tested here) -> clean
    [InlineData("2020-06-01", "2020-12-31", 1)] // overlaps tail -> affected
    [InlineData("2020-01-01", "2020-12-31", 1)] // exact match -> affected
    public async Task PreviewUpsertAsync_OverlapPredicate(string from, string to, int expectedAffected)
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync(
            "H2", "Đơn vị H2", "H2", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        var affected = await OrgUnits.PreviewUpsertAsync(id, new EffectivePeriod(DateOnly.Parse(from), DateOnly.Parse(to)));

        Assert.Equal(expectedAffected, affected.Count);
    }

    // N2: an open-ended child requires its parent to cover all the way to the open end -- a parent that
    // ends earlier leaves the tail uncovered -> STRICT temporal-FK BLOCKS (Failure, not Validation).
    [Fact]
    public async Task UpsertAsync_OpenEndedChild_ParentEndsEarly_BLOCKS_TemporalFk()
    {
        SkipUnlessDbAvailable();

        var parent = await CreateOrgUnitAsync(
            "PAR", "Cha", "Cha", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2030, 12, 31)));
        var child = await InsertHeaderAsync("org_unit");

        var r = await OrgUnits.UpsertAsync(
            child, new EffectivePeriod(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd),
            "CH", "Con", "Con", parent, VersionOperationKind.Add, "tester", "create");

        Assert.True(r.IsError);
        Assert.Contains(r.Errors, e => e.Type == ErrorOr.ErrorType.Failure && e.Code == "TemporalFk.ParentGap");
    }

    // 2026-08-05 reverse-FK-on-cancel fix: CancelVersionAsync's no-predecessor
    // branch reduces coverage exactly like DeleteVersionAsync -- a dependent child must BLOCK the cancel.

    // A1: a future-dated org unit whose ONLY version is the one being cancelled (no adjacent predecessor) --
    // a child org unit's parent_id self-edge depends on it -> reverse-FK BLOCKS, no row written.
    [Fact]
    public async Task CancelPlanAsync_NoPredecessor_ChildOrgUnitDepends_BLOCKS_TemporalFk()
    {
        SkipUnlessDbAvailable();

        var futurePeriod = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);

        var parentId = await OrgUnits.CreateIdentityAsync();
        var createParent = await OrgUnits.UpsertAsync(
            parentId, futurePeriod, "A1PAR", "Cha A1", "Cha A1", null, VersionOperationKind.Add, "tester", "create");
        Assert.False(createParent.IsError, DescribeErrors(createParent.Errors));

        var childId = await InsertHeaderAsync("org_unit");
        var createChild = await OrgUnits.UpsertAsync(
            childId, futurePeriod, "A1CHI", "Con A1", "Con A1", parentId, VersionOperationKind.Add, "tester", "create");
        Assert.False(createChild.IsError, DescribeErrors(createChild.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(parentId, createParent.Value.NewVersionId, Today, "tester", "cancel sole future plan");

        Assert.True(cancel.IsError);
        Assert.Contains(cancel.Errors, e =>
            e.Type == ErrorOr.ErrorType.Failure && e.Code == "TemporalFk.DependentsUncovered");

        // No row written: the target version is still active, not cancelled.
        var flags = await GetVersionFlagsAsync(createParent.Value.NewVersionId);
        Assert.True(flags.IsActive);
        Assert.False(flags.Cancelled);

        // The child is untouched -- verified via a fresh read (proves the transaction rolled back).
        var childBack = await OrgUnits.GetByIdentityAsync(childId, new DateOnly(2027, 6, 1));
        Assert.False(childBack.IsError, DescribeErrors(childBack.Errors));
        Assert.Equal(parentId, childBack.Value.ParentId);
        Assert.Equal("A1CHI", childBack.Value.OrgCode);
    }

    // A2: same shape as A1 but the dependent is a `user_version` row (org_unit_id edge) -- proves the check
    // walks BOTH edges declared for org_unit_version as a parent (self-edge AND user_version), not just the self-edge.
    [Fact]
    public async Task CancelPlanAsync_NoPredecessor_UserDepends_BLOCKS_TemporalFk()
    {
        SkipUnlessDbAvailable();

        var futurePeriod = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);

        var orgUnitId = await OrgUnits.CreateIdentityAsync();
        var createOrgUnit = await OrgUnits.UpsertAsync(
            orgUnitId, futurePeriod, "A2ORG", "Đơn vị A2", "A2", null, VersionOperationKind.Add, "tester", "create");
        Assert.False(createOrgUnit.IsError, DescribeErrors(createOrgUnit.Errors));

        var roleId = await CreateRoleAsync("A2ROLE", "Vai trò A2", OpenFrom2020);

        var userId = await CreateUserHeaderAsync();
        var createUser = await Users.UpsertAsync(
            userId, futurePeriod, "a2user", "Người dùng A2", orgUnitId, roleId, "tester", "create");
        Assert.False(createUser.IsError, DescribeErrors(createUser.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(orgUnitId, createOrgUnit.Value.NewVersionId, Today, "tester", "cancel sole future plan");

        Assert.True(cancel.IsError);
        Assert.Contains(cancel.Errors, e =>
            e.Type == ErrorOr.ErrorType.Failure && e.Code == "TemporalFk.DependentsUncovered");

        var flags = await GetVersionFlagsAsync(createOrgUnit.Value.NewVersionId);
        Assert.True(flags.IsActive);
        Assert.False(flags.Cancelled);

        var userBack = await Users.GetByIdentityAsync(userId, new DateOnly(2027, 6, 1));
        Assert.False(userBack.IsError, DescribeErrors(userBack.Errors));
        Assert.Equal(orgUnitId, userBack.Value.OrgUnitId);
    }

    // A3 (no false positive): the predecessor-restore scenario, now WITH a dependent child spanning the join --
    // must still succeed, proving the now-unconditional reverse-FK check does not block the coverage-neutral
    // restore path (it only ever EXTENDS coverage back to what it was).
    [Fact]
    public async Task CancelPlanAsync_OverlapCutRestore_WithDependentChildSpanningJoin_SUCCEEDS()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("A3PAR", "Cha A3", "Cha A3", null, OpenFrom2020);

        var future = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            "A3PAR", "Kế hoạch A3", "KH A3", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        // The child's period spans the join point (2026-12-31 / 2027-01-01) -- continuously covered ONLY
        // because the parent's 2 (adjacent, no-gap) active pieces together cover it.
        var childId = await InsertHeaderAsync("org_unit");
        var createChild = await OrgUnits.UpsertAsync(
            childId, OpenFrom2020, "A3CHI", "Con A3", "Con A3", id, VersionOperationKind.Add, "tester", "create");
        Assert.False(createChild.IsError, DescribeErrors(createChild.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        var afterCancel = await GetActivePeriodsAsync(id);
        Assert.Equal([OpenFrom2020], afterCancel);

        var childBack = await OrgUnits.GetByIdentityAsync(childId, Today);
        Assert.False(childBack.IsError, DescribeErrors(childBack.Errors));
        Assert.Equal(id, childBack.Value.ParentId);
    }

    // A4: cancelling a future plan can leave a day GAP between the remaining active periods elsewhere on the
    // same identity (here, pre-existing via a prior DeleteVersionAsync) -- D7 says WARN, never BLOCK.
    [Fact]
    public async Task CancelPlanAsync_LeavesGapBetweenRemainingPeriods_ReturnsWarning_DoesNotError()
    {
        SkipUnlessDbAvailable();

        // OBSOLETE WORKAROUND, kept only until this fixture is rewritten (plan 2026-08-25-orgunit-phantom-gap
        // Task 4). It read: "Built as a fully ADJACENT chain (case 2 at every step, never an overlap-cut) so
        // GapIsBlocking's nearest-untouched-neighbor check never sees a phantom gap." That check no longer
        // reads `untouched` alone and the phantom gap no longer exists, so the adjacency is EXPECTED to be no
        // longer forced -- see docs/design-effective-period.md section 4a. EXPECTED, not demonstrated: no
        // integration test on this path has been run against a non-adjacent chain yet, and proving it is part
        // of Task 4's job, not a fact this comment may assert. The chain below is left untouched on purpose:
        // rewriting it with a real overlap-cut is Task 4's job, and this test's assertions must not move in
        // the same commit that changed the mechanism they are meant to hold still against.
        // x/mid/y/target all directly abut.
        var x = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 30));
        var mid = new EffectivePeriod(new DateOnly(2020, 7, 1), new DateOnly(2020, 12, 31));
        var y = new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2026, 12, 31));
        var target = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);

        var id = await CreateOrgUnitAsync("A4GAP", "Khoảng trống A4", "A4", null, x);
        var seedMid = await OrgUnits.UpsertAsync(
            id, mid, "A4GAP", "Khoảng trống A4", "A4", null, VersionOperationKind.Edit, "tester", "seed-mid");
        Assert.False(seedMid.IsError, DescribeErrors(seedMid.Errors));
        var seedY = await OrgUnits.UpsertAsync(
            id, y, "A4GAP", "Khoảng trống A4", "A4", null, VersionOperationKind.Edit, "tester", "seed-y");
        Assert.False(seedY.IsError, DescribeErrors(seedY.Errors));
        var future = await OrgUnits.UpsertAsync(
            id, target, "A4GAP", "Kế hoạch A4", "A4", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        // Deletes "mid" outright (DeleteVersionAsync does NOT enforce gap=BLOCK, unlike UpsertVersionAsync) --
        // opens a gap [2020-07-01, 2020-12-31] between x and y, present BEFORE the cancel below.
        var deleteMid = await OrgUnits.DeleteVersionAsync(id, seedMid.Value.NewVersionId);
        Assert.False(deleteMid.IsError, DescribeErrors(deleteMid.Errors));

        // Cancelling the future plan restores "y" (its immediately-adjacent predecessor) forward to the
        // plan's own open end (coverage-neutral for that join) -- but the x..y gap left by the delete above
        // is still present in the remaining coverage.
        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        var warning = Assert.Single(cancel.Value.Warnings);
        Assert.Equal(new DateOnly(2020, 7, 1), warning.GapFrom);
        Assert.Equal(new DateOnly(2020, 12, 31), warning.GapTo);
    }

    // ---- D1 (requester decision 2026-08-10): the cancel boundary is "has NOT completed a single effective
    // day", so a version whose coverage STARTS TODAY is cancellable; one that started yesterday is not.
    // These 3 tests pin the boundary day-by-day around business today (IamRepositoryTestBase.Today).

    // D1-a: EffectiveFrom == today -> cancel SUCCEEDS (isactive=0, status='cancelled').
    [Fact]
    public async Task CancelPlanAsync_VersionStartingToday_SUCCEEDS()
    {
        SkipUnlessDbAvailable();

        var id = await OrgUnits.CreateIdentityAsync();
        var create = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd),
            "D1TODAY", "Bắt đầu hôm nay", "HN", null, VersionOperationKind.Add, "tester", "create");
        create.IsError.Should().BeFalse(DescribeErrors(create.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, create.Value.NewVersionId, Today, "tester", "hủy ngay trong ngày");
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var flags = await GetVersionFlagsAsync(create.Value.NewVersionId);
        flags.IsActive.Should().BeFalse();
        flags.Cancelled.Should().BeTrue();
    }

    // D1-b: EffectiveFrom == today - 1 -> still BLOCKED. The boundary moved exactly ONE day, no further.
    [Fact]
    public async Task CancelPlanAsync_VersionStartedYesterday_BLOCKS()
    {
        SkipUnlessDbAvailable();

        var id = await OrgUnits.CreateIdentityAsync();
        var create = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(Today.AddDays(-1), EffectivePeriod.OpenEnd),
            "D1YDAY", "Bắt đầu hôm qua", "HQ", null, VersionOperationKind.Add, "tester", "create");
        create.IsError.Should().BeFalse(DescribeErrors(create.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, create.Value.NewVersionId, Today, "tester", "thử hủy");

        cancel.IsError.Should().BeTrue();
        cancel.Errors.Should().Contain(e =>
            e.Type == ErrorOr.ErrorType.Validation && e.Code == "VersionedRepository.NotAFuturePlan");

        var flags = await GetVersionFlagsAsync(create.Value.NewVersionId);
        flags.IsActive.Should().BeTrue();
        flags.Cancelled.Should().BeFalse();
    }

    // D1-c: EffectiveFrom == today + 1 -> unchanged behaviour, still SUCCEEDS.
    [Fact]
    public async Task CancelPlanAsync_VersionStartingTomorrow_SUCCEEDS()
    {
        SkipUnlessDbAvailable();

        var id = await OrgUnits.CreateIdentityAsync();
        var create = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(Today.AddDays(1), EffectivePeriod.OpenEnd),
            "D1TMRW", "Bắt đầu ngày mai", "NM", null, VersionOperationKind.Add, "tester", "create");
        create.IsError.Should().BeFalse(DescribeErrors(create.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, create.Value.NewVersionId, Today, "tester", "hủy kế hoạch");
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var flags = await GetVersionFlagsAsync(create.Value.NewVersionId);
        flags.IsActive.Should().BeFalse();
        flags.Cancelled.Should().BeTrue();
    }

    // D1-d (dependent behaviour, the case a strictly-FUTURE plan could never produce): a version starting
    // TODAY can already have a dependent relying on it TODAY. With no adjacent predecessor, cancelling it
    // removes the parent's coverage from under that live child -> the D8 reverse temporal-FK check must
    // BLOCK and roll everything back, exactly as it does in the future-dated A1 case above. Nothing about a
    // same-day cancel bypasses that guard.
    [Fact]
    public async Task CancelPlanAsync_StartingToday_NoPredecessor_ChildDependsToday_BLOCKS_TemporalFk()
    {
        SkipUnlessDbAvailable();

        var todayOnwards = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);

        var parentId = await OrgUnits.CreateIdentityAsync();
        var createParent = await OrgUnits.UpsertAsync(
            parentId, todayOnwards, "D1PAR", "Cha D1", "Cha D1", null, VersionOperationKind.Add, "tester", "create");
        createParent.IsError.Should().BeFalse(DescribeErrors(createParent.Errors));

        var childId = await InsertHeaderAsync("org_unit");
        var createChild = await OrgUnits.UpsertAsync(
            childId, todayOnwards, "D1CHI", "Con D1", "Con D1", parentId, VersionOperationKind.Add, "tester", "create");
        createChild.IsError.Should().BeFalse(DescribeErrors(createChild.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(parentId, createParent.Value.NewVersionId, Today, "tester", "hủy trong ngày");

        cancel.IsError.Should().BeTrue();
        cancel.Errors.Should().Contain(e =>
            e.Type == ErrorOr.ErrorType.Failure && e.Code == "TemporalFk.DependentsUncovered");

        var flags = await GetVersionFlagsAsync(createParent.Value.NewVersionId);
        flags.IsActive.Should().BeTrue();
        flags.Cancelled.Should().BeFalse();

        var childBack = await OrgUnits.GetByIdentityAsync(childId, Today);
        childBack.IsError.Should().BeFalse(DescribeErrors(childBack.Errors));
        childBack.Value.ParentId.Should().Be(parentId);
    }

    // D1-e (the benign half of D1-d): when the same-day version overlap-CUT a predecessor, cancelling it
    // restores that predecessor's original coverage -- including TODAY, the one day the cancelled version had
    // actually been live -- so a dependent that spans the join is never stranded and the identity still
    // resolves today (to the PREDECESSOR's business data, proving the cancelled day was rolled back too).
    [Fact]
    public async Task CancelPlanAsync_StartingToday_RestoresPredecessorCoverageOverToday()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("D1REST", "Bản gốc", "Gốc", null, OpenFrom2020);

        // Overlap-cuts the open-ended base down to [2020-01-01, today-1] (case 4).
        var sameDay = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(Today, EffectivePeriod.OpenEnd),
            "D1REST", "Bản hôm nay", "HN", null, VersionOperationKind.Edit, "tester", "edit");
        sameDay.IsError.Should().BeFalse(DescribeErrors(sameDay.Errors));

        var childId = await InsertHeaderAsync("org_unit");
        var createChild = await OrgUnits.UpsertAsync(
            childId, OpenFrom2020, "D1RCHI", "Con", "Con", id, VersionOperationKind.Add, "tester", "create");
        createChild.IsError.Should().BeFalse(DescribeErrors(createChild.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, sameDay.Value.NewVersionId, Today, "tester", "hủy trong ngày");
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        var afterCancel = await GetActivePeriodsAsync(id);
        afterCancel.Should().Equal([OpenFrom2020]);

        var todayBack = await OrgUnits.GetByIdentityAsync(id, Today);
        todayBack.IsError.Should().BeFalse(DescribeErrors(todayBack.Errors));
        todayBack.Value.OrgNameShortVn.Should().Be("Gốc");

        var childBack = await OrgUnits.GetByIdentityAsync(childId, Today);
        childBack.IsError.Should().BeFalse(DescribeErrors(childBack.Errors));
        childBack.Value.ParentId.Should().Be(id);
    }

    private async Task<List<EffectivePeriod>> GetActivePeriodsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            "SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo FROM org_unit_version WHERE org_unit_id = @orgUnitId AND isactive = 1",
            new { orgUnitId });
        return rows.Select(row => new EffectivePeriod(row.EffectiveFrom, row.EffectiveTo)).ToList();
    }

    private sealed class VersionFlags
    {
        public bool IsActive { get; init; }
        public bool Cancelled { get; init; }
    }

    private async Task<VersionFlags> GetVersionFlagsAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<VersionFlags>(
            "SELECT isactive AS IsActive, (status = 'cancelled') AS Cancelled FROM org_unit_version WHERE id = @versionId",
            new { versionId });
    }
}
