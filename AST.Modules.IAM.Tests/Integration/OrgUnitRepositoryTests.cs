using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// B2 — scope-based reads (§6) + writes using the 8-case algebra/remnant/soft-deactivate (§4) on org_unit_version.
// Concurrency/named-lock races + the integrity-check grid + closed-node CTE edge cases: DEFERRED to B3 (out of scope).
public sealed class OrgUnitRepositoryTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod ExistingBase = new(new DateOnly(2020, 3, 1), new DateOnly(2020, 6, 30));
    private static readonly DataScope GlobalScope = new(ScopeLevel.Global, null, "tester");

    [Fact]
    public async Task GetInScopeAsync_Global_ReturnsAllActiveVersionsInPeriod()
    {
        SkipUnlessDbAvailable();

        var a = await CreateOrgUnitAsync("A", "Đơn vị A", "A", null, OpenFrom2020);
        var b = await CreateOrgUnitAsync("B", "Đơn vị B", "B", null, OpenFrom2020);

        var rows = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);

        Assert.Contains(rows, r => r.OrgUnitId == a);
        Assert.Contains(rows, r => r.OrgUnitId == b);
    }

    [Fact]
    public async Task GetInScopeAsync_ExcludesVersionsOutOfPeriod()
    {
        SkipUnlessDbAvailable();

        var closedPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var id = await CreateOrgUnitAsync("G", "Đơn vị G", "G", null, closedPeriod);

        var within = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), new DateOnly(2020, 6, 1));
        Assert.Contains(within, r => r.OrgUnitId == id);

        var before = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), new DateOnly(2019, 12, 31));
        Assert.DoesNotContain(before, r => r.OrgUnitId == id);

        var after = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), new DateOnly(2021, 1, 1));
        Assert.DoesNotContain(after, r => r.OrgUnitId == id);
    }

    [Fact]
    public async Task GetInScopeAsync_ExcludesInactiveVersions_EvenWhenAsOfIsWithinItsPeriod()
    {
        SkipUnlessDbAvailable();

        // isactive=0 and "out of period" are 2 different concepts (hard invariant #2) -- directly simulates
        // 1 isactive=0 version STILL within its period (raw SQL, because the normal business flow through UpsertAsync
        // always preserves full coverage via a remnant) to confirm the repo filters BOTH conditions AT ONCE.
        var id = await CreateOrgUnitAsync("H", "Đơn vị H", "H", null, OpenFrom2020);

        await using (var connection = new MySqlConnection(ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync(
                "UPDATE org_unit_version SET isactive = 0 WHERE org_unit_id = @id", new { id });
        }

        var rows = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);
        Assert.DoesNotContain(rows, r => r.OrgUnitId == id);
    }

    [Fact]
    public async Task GetInScopeAsync_OwnOrgUnitAndDescendants_ReturnsSubtreeOnly()
    {
        SkipUnlessDbAvailable();

        // root -> child -> grandchild ; root -> sibling (outside child's subtree)
        var root = await CreateOrgUnitAsync("ROOT", "Gốc", "ROOT", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("CHILD", "Con", "CHILD", root, OpenFrom2020);
        var grandchild = await CreateOrgUnitAsync("GCHILD", "Cháu", "GCHILD", child, OpenFrom2020);
        var sibling = await CreateOrgUnitAsync("SIBLING", "Anh em", "SIBLING", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, child, "tester");
        var rows = await OrgUnits.GetInScopeAsync(scope, Today);
        var ids = rows.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(child, ids);
        Assert.Contains(grandchild, ids);
        Assert.DoesNotContain(root, ids);
        Assert.DoesNotContain(sibling, ids);
    }

    [Fact]
    public async Task GetInScopeAsync_OwnOrgUnit_ReturnsExactUnitOnly()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("ROOT2", "Gốc 2", "ROOT2", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("CHILD2", "Con 2", "CHILD2", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnit, child, "tester");
        var rows = await OrgUnits.GetInScopeAsync(scope, Today);
        var ids = rows.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(child, ids);
        Assert.DoesNotContain(root, ids);
    }

    // Phase 4d (history-grid read) — GetHistoryInScopeAsync has NO isactive/period filter, unlike
    // every Get* method above.
    [Fact]
    public async Task GetHistoryInScopeAsync_IncludesInactiveVersions_UnlikeGetInScopeAsync()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("HIST1", "Đơn vị History 1", "HIST1", null, OpenFrom2020);
        var version = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(version.IsError);

        // Cut end-date stays AFTER Today so the active remnant still covers Today -- this test is about
        // GetHistoryInScopeAsync including the now-inactive original, not about GetInScopeAsync losing coverage.
        var closeResult = await OrgUnits.CloseVersionAsync(
            id, version.Value.Id, Today.AddYears(1), new OperationDate(Today), "tester", "close for history test");
        Assert.False(closeResult.IsError, DescribeErrors(closeResult.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, r => !r.IsActive);
        Assert.Contains(history, r => r.IsActive);

        var scoped = await OrgUnits.GetInScopeAsync(new DataScope(ScopeLevel.Global, null, "tester"), Today);
        Assert.Single(scoped, r => r.OrgUnitId == id);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_ExcludesOtherIdentities()
    {
        SkipUnlessDbAvailable();

        var a = await CreateOrgUnitAsync("HISTA", "Đơn vị History A", "HISTA", null, OpenFrom2020);
        var b = await CreateOrgUnitAsync("HISTB", "Đơn vị History B", "HISTB", null, OpenFrom2020);

        var historyA = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, a);
        Assert.Single(historyA);
        Assert.All(historyA, r => Assert.Equal(a, r.OrgUnitId));
        Assert.DoesNotContain(historyA, r => r.OrgUnitId == b);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_IncludesCancelledVersions()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("HISTCXL", "Đơn vị History Cancel", "HISTCXL", null, OpenFrom2020);
        var future = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            "HISTCXL", "Kế hoạch tương lai", "KHTL", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        Assert.Contains(history, r => r.Id == future.Value.NewVersionId && r.Status == VersionLifecycleStatus.Cancelled && !r.IsActive);
    }

    // Ordering changed (requester-approved) from EffectiveFrom-descending to RecordedAt (audit
    // timestamp) descending, Id as a deterministic tiebreaker. RecordedAt and EffectiveFrom order
    // are made to DISAGREE here on purpose: the LATER-effective version (2021-01-01..open) is
    // recorded FIRST, then an EARLIER-effective, adjacent version (2020-01-01..2020-12-31 --
    // adjacency avoids tripping the org-unit gap-block rule) is recorded SECOND as a backfill.
    // If the ORDER BY ever regressed to "effective_from DESC", r2 (2021) would sort first --
    // the opposite of the asserted order -- so this test fails on that exact regression.
    [Fact]
    public async Task GetHistoryInScopeAsync_OrdersByRecordedAtDescending()
    {
        SkipUnlessDbAvailable();

        var id = await InsertHeaderAsync("org_unit");
        var laterEffective = new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd);
        var r2 = await OrgUnits.UpsertAsync(
            id, laterEffective, "HISTORD", "Đơn vị Ord v2", "HISTORD", null, VersionOperationKind.Add, "tester", "v2-recorded-first");
        Assert.False(r2.IsError, DescribeErrors(r2.Errors));

        var earlierEffective = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var r1 = await OrgUnits.UpsertAsync(
            id, earlierEffective, "HISTORD", "Đơn vị Ord v1 (backfill)", "HISTORD", null, VersionOperationKind.Edit, "tester", "v1-recorded-second-backfill");
        Assert.False(r1.IsError, DescribeErrors(r1.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        Assert.Equal(2, history.Count);
        // r1 was RECORDED second (chronologically later recorded_at) even though its EffectiveFrom
        // (2020) is earlier than r2's (2021) -- RecordedAt DESC must place it first.
        Assert.Equal(r1.Value.NewVersionId, history[0].Id);
        Assert.Equal(r2.Value.NewVersionId, history[1].Id);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_OrgUnitIdNull_ReturnsEveryIdentityInScope()
    {
        SkipUnlessDbAvailable();

        var a = await CreateOrgUnitAsync("HISTALLA", "Đơn vị History All A", "HISTALLA", null, OpenFrom2020);
        var b = await CreateOrgUnitAsync("HISTALLB", "Đơn vị History All B", "HISTALLB", null, OpenFrom2020);

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, null);
        var ids = history.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_OwnOrgUnitAndDescendants_ReturnsSubtreeOnly()
    {
        SkipUnlessDbAvailable();

        // root -> child -> grandchild ; root -> sibling (outside child's subtree). Mirrors
        // GetInScopeAsync_OwnOrgUnitAndDescendants_ReturnsSubtreeOnly's shape on purpose: the
        // scoped identity (`child`) needs an actual descendant (`grandchild`) reachable ONLY
        // through the CTE's recursive arm -- a leaf-only fixture (scope = a node with no
        // children) would still pass even with the recursive arm deleted entirely.
        var root = await CreateOrgUnitAsync("HROOT", "Gốc lịch sử", "HROOT", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("HCHILD", "Con lịch sử", "HCHILD", root, OpenFrom2020);
        var grandchild = await CreateOrgUnitAsync("HGCHILD", "Cháu lịch sử", "HGCHILD", child, OpenFrom2020);
        var sibling = await CreateOrgUnitAsync("HSIB", "Anh em lịch sử", "HSIB", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, child, "tester");
        var history = await OrgUnits.GetHistoryInScopeAsync(scope, null);
        var ids = history.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(child, ids);
        Assert.Contains(grandchild, ids);
        Assert.DoesNotContain(root, ids);
        Assert.DoesNotContain(sibling, ids);
    }

    // The CTE's entire reason for existing (no @today/isactive filter on the recursive join) --
    // seeds root -> mid -> leaf where `mid`'s only version is FUTURE-DATED (not yet effective), so
    // it never appears in StandardScopeFilterBuilder.BuildSubtreeCte's `today_ou` CTE (which
    // requires `effective_from <= @today`). GetHistoryInScopeAsync must still reach `leaf` through
    // `mid` because HistorySubtreeCte's recursive join carries no such filter. If HistorySubtreeCte
    // were ever "simplified" into a call to BuildSubtreeCte, `leaf` would silently disappear from
    // both calls below -- this is exactly the regression this custom CTE exists to prevent.
    [Fact]
    public async Task GetHistoryInScopeAsync_ReachesDescendantsBehindAFutureIntermediateNode_UnlikeGetInScopeAsync()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("HFROOT", "Gốc tương lai", "HFROOT", null, OpenFrom2020);
        var futurePeriod = new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
        var mid = await CreateOrgUnitAsync("HFMID", "Trung gian tương lai", "HFMID", root, futurePeriod);
        var leaf = await CreateOrgUnitAsync("HFLEAF", "Lá tương lai", "HFLEAF", mid, futurePeriod);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");

        var history = await OrgUnits.GetHistoryInScopeAsync(scope, null);
        Assert.Contains(history, r => r.OrgUnitId == leaf);

        var inScope = await OrgUnits.GetInScopeAsync(scope, Today);
        Assert.DoesNotContain(inScope, r => r.OrgUnitId == mid);
        Assert.DoesNotContain(inScope, r => r.OrgUnitId == leaf);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_OwnOrgUnit_ReturnsExactUnitOnly()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("HROOT2", "Gốc lịch sử 2", "HROOT2", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("HCHILD2", "Con lịch sử 2", "HCHILD2", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnit, child, "tester");
        var history = await OrgUnits.GetHistoryInScopeAsync(scope, null);
        var ids = history.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(child, ids);
        Assert.DoesNotContain(root, ids);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_Self_Throws()
    {
        SkipUnlessDbAvailable();

        var scope = new DataScope(ScopeLevel.Self, null, "tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => OrgUnits.GetHistoryInScopeAsync(scope, null));
    }

    // Scope-checked-write membership primitive (2026-08-05 security fix). Reuses the SAME
    // per-ScopeLevel predicate as GetHistoryInScopeAsync -- these tests mirror its scope-level
    // coverage above but assert the boolean membership answer, not the row list.
    [Fact]
    public async Task IsWithinScopeAsync_Global_AnyUnit_ReturnsTrue()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("SCG1", "Đơn vị Scope Global", "SCG1", null, OpenFrom2020);

        var result = await OrgUnits.IsWithinScopeAsync(GlobalScope, id);

        Assert.True(result);
    }

    [Fact]
    public async Task IsWithinScopeAsync_OwnOrgUnit_TheUnitItself_ReturnsTrue()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("SCOUROOT", "Gốc Scope OwnOrgUnit", "SCOUROOT", null, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnit, root, "tester");
        var result = await OrgUnits.IsWithinScopeAsync(scope, root);

        Assert.True(result);
    }

    [Fact]
    public async Task IsWithinScopeAsync_OwnOrgUnitAndDescendants_DescendantOfRoot_ReturnsTrue()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("SCDROOT", "Gốc Scope Descendants", "SCDROOT", null, OpenFrom2020);
        var child = await CreateOrgUnitAsync("SCDCHILD", "Con Scope Descendants", "SCDCHILD", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");
        var result = await OrgUnits.IsWithinScopeAsync(scope, child);

        Assert.True(result);
    }

    // The exact vulnerability this repository-side check exists to close: a unit outside the
    // caller's OwnOrgUnit/OwnOrgUnitAndDescendants root must be rejected.
    [Fact]
    public async Task IsWithinScopeAsync_UnitOutsideScopeRoot_ReturnsFalse()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("SCOTROOT", "Gốc Scope Outside", "SCOTROOT", null, OpenFrom2020);
        var sibling = await CreateOrgUnitAsync("SCOTSIB", "Anh em Scope Outside", "SCOTSIB", null, OpenFrom2020);

        var ownUnitScope = new DataScope(ScopeLevel.OwnOrgUnit, root, "tester");
        Assert.False(await OrgUnits.IsWithinScopeAsync(ownUnitScope, sibling));

        var descendantsScope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");
        Assert.False(await OrgUnits.IsWithinScopeAsync(descendantsScope, sibling));
    }

    // Q2 (2026-08-05): "in scope" means the unit was within the caller's scope at ANY
    // point in its FULL version history, not just today -- a unit being edited/closed may be
    // entirely past- or future-dated (spec 2.7.6). This is the exact case a future maintainer is
    // most likely to "simplify" back into an as-of-today-only check -- must stay a dedicated test.
    [Fact]
    public async Task IsWithinScopeAsync_UnitNotEffectiveToday_ButWasInScopeHistorically_ReturnsTrue()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("SHPROOT", "Gốc Scope Lịch sử quá khứ", "SHPROOT", null, OpenFrom2020);

        // Fully PAST-dated: effective period ends well before Today (2026-07-03) -- not resolvable
        // "as of today" by GetInScopeAsync/GetByIdentityAsync, yet its history is entirely inside root.
        var pastPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var pastUnit = await CreateOrgUnitAsync("SHPPAST", "Đơn vị đã hết hiệu lực", "SHPPAST", root, pastPeriod);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");
        Assert.True(await OrgUnits.IsWithinScopeAsync(scope, pastUnit));

        // Fully FUTURE-dated: effective period starts well after Today.
        var futurePeriod = new EffectivePeriod(new DateOnly(2030, 1, 1), EffectivePeriod.OpenEnd);
        var futureUnit = await CreateOrgUnitAsync("SHPFUT", "Đơn vị kế hoạch tương lai", "SHPFUT", root, futurePeriod);

        Assert.True(await OrgUnits.IsWithinScopeAsync(scope, futureUnit));
    }

    [Fact]
    public async Task IsWithinScopeAsync_UnitHistoryEntirelyOutsideScopeRoot_ReturnsFalse()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("SHOROOT", "Gốc Scope Lịch sử ngoài", "SHOROOT", null, OpenFrom2020);
        var outsideUnit = await CreateOrgUnitAsync(
            "SHOOUT", "Đơn vị hoàn toàn ngoài phạm vi", "SHOOUT", null,
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");
        Assert.False(await OrgUnits.IsWithinScopeAsync(scope, outsideUnit));
    }

    [Fact]
    public async Task IsWithinScopeAsync_Self_Throws()
    {
        SkipUnlessDbAvailable();

        var scope = new DataScope(ScopeLevel.Self, null, "tester");
        await Assert.ThrowsAsync<InvalidOperationException>(() => OrgUnits.IsWithinScopeAsync(scope, 1));
    }

    [Fact]
    public async Task UpsertAsync_InsertsNewVersion_ForFreshIdentity()
    {
        SkipUnlessDbAvailable();

        var id = await InsertHeaderAsync("org_unit");
        var result = await OrgUnits.UpsertAsync(
            id, OpenFrom2020, "D", "Đơn vị D", "D", null, VersionOperationKind.Add, "tester", "create");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.True(result.Value.NewVersionId > 0);
        Assert.Empty(result.Value.Warnings);

        var fetched = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(fetched.IsError);
        Assert.Equal("D", fetched.Value.OrgCode);
    }

    [Fact]
    public async Task UpsertAsync_CuttingExistingPeriod_CreatesRemnant_WithCopiedBusinessData_AndSoftDeactivatesOld()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("ROOT3", "Gốc 3", "ROOT3", null, OpenFrom2020);
        var id = await CreateOrgUnitAsync("E", "Đơn vị E ban đầu", "E", root, OpenFrom2020);

        // Gets the base version's id (currently active) to compare against the remnant after cutting.
        var beforeCut = await GetVersionRowsAsync(id);
        Assert.Single(beforeCut);
        var originalVersionId = beforeCut[0].Id;

        // Cuts into the head (case 3): new period [2025-06-01, open], NEW business data ("Đơn vị E sau đổi tên").
        var cut = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2025, 6, 1), EffectivePeriod.OpenEnd),
            "E", "Đơn vị E sau đổi tên", "E", root, VersionOperationKind.Edit, "tester", "rename-2025");
        Assert.False(cut.IsError, DescribeErrors(cut.Errors));

        var rowsAfter = await GetVersionRowsAsync(id);
        // Expects 3 rows: the base version (isactive=0), a remnant [2020-01-01,2025-05-31] (COPIES the original data,
        // isactive=1), and the new version [2025-06-01, open] (isactive=1).
        Assert.Equal(3, rowsAfter.Count);

        var original = rowsAfter.Single(r => r.Id == originalVersionId);
        Assert.False(original.IsActive);

        var remnant = rowsAfter.Single(r => r.IsActive && r.EffectiveTo == new DateOnly(2025, 5, 31));
        Assert.Equal(new DateOnly(2020, 1, 1), remnant.EffectiveFrom);
        // The remnant MUST copy the exact business data of the SOURCE version (original OrgCode/OrgNameFullVn/ParentId), not the
        // new values passed into UpsertAsync.
        Assert.Equal("E", remnant.OrgCode);
        Assert.Equal("Đơn vị E ban đầu", remnant.OrgNameFullVn);
        Assert.Equal(root, remnant.ParentId);

        var fresh = rowsAfter.Single(r => r.IsActive && r.EffectiveFrom == new DateOnly(2025, 6, 1));
        Assert.Equal("Đơn vị E sau đổi tên", fresh.OrgNameFullVn);
        Assert.Equal(cut.Value.NewVersionId, fresh.Id);
    }

    // P4: org-unit gaps BLOCK (unlike the base default, which only warns).
    [Fact]
    public async Task UpsertAsync_DisjointPeriod_ForOrgUnit_BLOCKS()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync(
            "F", "Đơn vị F", "F", null, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));

        // The new period is entirely disjoint (from 2022 onward) -> a gap [2021-01-01,2021-12-31] => BLOCKS for org-unit (P4).
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2022, 1, 1), EffectivePeriod.OpenEnd),
            "F", "Đơn vị F (2022)", "F", null, VersionOperationKind.Edit, "tester", "resume-2022");

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Type == ErrorOr.ErrorType.Validation && e.Code == "OrgUnit.GapNotAllowed");
    }

    // The base's default gap behavior (warn, not block) for the other 4 IAM repos is pinned by
    // RoleRepositoryTests.UpsertAsync_AlgebraCase1_Disjoint_WarnsNotBlocked.

    [Fact]
    public async Task CreateIdentityAsync_ReturnsDistinctIds_UsableImmediatelyByUpsertAsync()
    {
        SkipUnlessDbAvailable();

        var first = await OrgUnits.CreateIdentityAsync();
        var second = await OrgUnits.CreateIdentityAsync();

        Assert.NotEqual(first, second);

        var result = await OrgUnits.UpsertAsync(
            first, OpenFrom2020, "NEWID", "Đơn vị mới", "Mới", null, VersionOperationKind.Add, "tester", "seed");

        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task DeleteEmptyIdentityAsync_RemovesAHeaderWithZeroVersions()
    {
        SkipUnlessDbAvailable();

        var id = await OrgUnits.CreateIdentityAsync();

        await OrgUnits.DeleteEmptyIdentityAsync(id);

        var result = await OrgUnits.GetByIdentityAsync(id, DateOnly.FromDateTime(DateTime.Today));
        Assert.True(result.IsError); // no header, no version -> NotFound either way; the row is gone
    }

    [Fact]
    public async Task DeleteEmptyIdentityAsync_NeverDeletesAHeaderThatHasAVersion()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("KEEPME", "Đơn vị giữ lại", "Giữ lại", null, OpenFrom2020);

        await OrgUnits.DeleteEmptyIdentityAsync(id);

        var result = await OrgUnits.GetByIdentityAsync(id, DateOnly.FromDateTime(DateTime.Today));
        Assert.False(result.IsError, DescribeErrors(result.Errors));
        Assert.Equal("KEEPME", result.Value.OrgCode);
    }

    // Moved from RoleRepositoryTests (brief 064): role is Immediate so historical algebra cannot be
    // the SUT there. Org unit may write historical/bounded periods. Duplicate of OrgUnitEditAlgebraTests
    // coverage is intentional.
    [Fact]
    public async Task UpsertAsync_AlgebraCase2_Adjacent_BothStayActive()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG2", "Case2", "Case2", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd),
            "RALG2", "Case2 adj", "Case2", null, VersionOperationKind.Edit, "tester", "case2");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.OrderBy(p => p.From).Should().Equal(
            ExistingBase,
            new EffectivePeriod(new DateOnly(2020, 7, 1), EffectivePeriod.OpenEnd));
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase3_OverlapsHead_TailRemnantPlusNew()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG3", "Case3", "Case3", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 4, 30)),
            "RALG3", "Case3 head", "Case3", null, VersionOperationKind.Edit, "tester", "case3");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.OrderBy(p => p.From).Should().Equal(
            new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 4, 30)),
            new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2020, 6, 30)));
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase4_OverlapsTail_HeadRemnantPlusNew()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG4", "Case4", "Case4", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2021, 12, 31)),
            "RALG4", "Case4 tail", "Case4", null, VersionOperationKind.Edit, "tester", "case4");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.OrderBy(p => p.From).Should().Equal(
            new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 4, 30)),
            new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2021, 12, 31)));
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase5_SubPeriod_SplitsIntoHeadAndTailRemnants()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG5", "Case5", "Case5", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 4, 1), new DateOnly(2020, 5, 31)),
            "RALG5", "Case5 sub", "Case5", null, VersionOperationKind.Edit, "tester", "case5");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.OrderBy(p => p.From).Should().Equal(
            new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 3, 31)),
            new EffectivePeriod(new DateOnly(2020, 4, 1), new DateOnly(2020, 5, 31)),
            new EffectivePeriod(new DateOnly(2020, 6, 1), new DateOnly(2020, 6, 30)));
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase6_Superset_AbsorbsExisting()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG6", "Case6", "Case6", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)),
            "RALG6", "Case6 super", "Case6", null, VersionOperationKind.Edit, "tester", "case6");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.Should().Equal(new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)));
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase7_ExactMatch_CorrectionSamePeriod()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG7", "Case7", "Case7", null, ExistingBase);
        var result = await OrgUnits.UpsertAsync(
            id, ExistingBase, "RALG7", "Case7 corrected", "Case7", null, VersionOperationKind.Edit, "tester", "case7");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var asOf = new DateOnly(2020, 4, 15);
        var row = await OrgUnits.GetByIdentityAsync(id, asOf);
        row.IsError.Should().BeFalse(DescribeErrors(row.Errors));
        row.Value.OrgNameFullVn.Should().Be("Case7 corrected");
        row.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_AlgebraCase8_SpanningMultiple_TwoRemnantsPlusNew()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("RALG8", "Case8", "Case8", null, ExistingBase);
        var adjacent = new EffectivePeriod(new DateOnly(2020, 7, 1), new DateOnly(2020, 9, 30));
        (await OrgUnits.UpsertAsync(
            id, adjacent, "RALG8", "Case8 mid", "Case8", null, VersionOperationKind.Edit, "tester", "seed-adj"))
            .IsError.Should().BeFalse();

        var spanning = new EffectivePeriod(new DateOnly(2020, 5, 1), new DateOnly(2020, 8, 31));
        var result = await OrgUnits.UpsertAsync(
            id, spanning, "RALG8", "Case8 span", "Case8", null, VersionOperationKind.Edit, "tester", "case8");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        var active = await GetActiveOrgUnitPeriodsAsync(id);
        active.OrderBy(p => p.From).Should().Equal(
            new EffectivePeriod(new DateOnly(2020, 3, 1), new DateOnly(2020, 4, 30)),
            spanning,
            new EffectivePeriod(new DateOnly(2020, 9, 1), new DateOnly(2020, 9, 30)));
    }

    // Class (not a record) + init property: Dapper 2.1.79 materializes via reflection/property-setter
    // more reliably for this shape than a record (Dapper's constructor-mapping does not cooperate well with a custom
    // TypeHandler for DateOnly when matching a constructor -- see DapperDateOnlyTypeHandler).
    private sealed class RawVersionRow
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly EffectiveTo { get; init; }
        public string OrgCode { get; init; } = string.Empty;
        public string OrgNameFullVn { get; init; } = string.Empty;
        public long? ParentId { get; init; }
    }

    private async Task<List<RawVersionRow>> GetVersionRowsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<RawVersionRow>(
            """
            SELECT id AS Id, isactive AS IsActive, effective_from AS EffectiveFrom, effective_to AS EffectiveTo,
                   org_code AS OrgCode, org_name_full_vn AS OrgNameFullVn, parent_id AS ParentId
            FROM org_unit_version WHERE org_unit_id = @orgUnitId
            """, new { orgUnitId });
        return rows.ToList();
    }

    private async Task<List<EffectivePeriod>> GetActiveOrgUnitPeriodsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<(DateOnly EffectiveFrom, DateOnly EffectiveTo)>(
            """
            SELECT effective_from AS EffectiveFrom, effective_to AS EffectiveTo
            FROM org_unit_version
            WHERE org_unit_id = @orgUnitId AND isactive = 1
            ORDER BY effective_from
            """,
            new { orgUnitId });
        return rows.Select(r => new EffectivePeriod(r.EffectiveFrom, r.EffectiveTo)).ToList();
    }
}
