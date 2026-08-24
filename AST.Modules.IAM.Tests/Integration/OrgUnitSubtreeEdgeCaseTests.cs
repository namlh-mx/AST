using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using Dapper;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// B3 -- subtree CTE edge cases (§4 docs/design-iam-schema.md) + settling the "close an org unit"
// semantics (R2) + confirming STRICT reverse-FK (Q1/D8) when closing a parent that still has coverage-dependent children.
public sealed class OrgUnitSubtreeEdgeCaseTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);

    // [R2 SETTLED — the data layer, 2026-07-03] "Closing an org unit" = keeping 1 in-period version with `isactive=0`
    // (does NOT shrink effective_to into the past). Simulated via raw SQL (there is no "close the whole entity"
    // API yet in Slice C) -- ONLY sets isactive=0 on the currently active version, KEEPING effective_to unchanged (open).
    //
    // Reason for settling on this approach (instead of "shrink effective_to to yesterday"): if the period were shrunk,
    // at @today there would be NO org_unit_version row for that node falling WITHIN the period (effective_from<=@today<=
    // effective_to) -> the "today_ou" branch in BuildSubtreeCte (StandardScopeFilterBuilder) would have no
    // row to return `parent_id` -> the subtree BREAKS at that node, disconnecting children/grandchildren even though they still
    // validly exist. Keeping 1 in-period isactive=0 version ensures `today_ou` (ORDER BY isactive DESC, id DESC)
    // can still SELECT exactly 1 representative row (fallback) carrying `parent_id` so the subtree CTE can "pass through" the
    // closed node -- matching §6/§4 of the canonical doc. The existing CTE (BuildSubtreeCte) ALREADY implements
    // this semantics since B2 -- NO code change needed, just confirming it with a test (this file).
    private async Task CloseOrgUnitInPeriodAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var affected = await connection.ExecuteAsync(
            "UPDATE org_unit_version SET isactive = 0 WHERE org_unit_id = @orgUnitId AND isactive = 1",
            new { orgUnitId });
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task Subtree_TraversesThroughClosedMiddleNode_DescendantsStillReachable()
    {
        SkipUnlessDbAvailable();

        // root -> mid (WILL CLOSE) -> child -> grandchild : 4 levels, the closed node is in the MIDDLE.
        var root = await CreateOrgUnitAsync("R2-ROOT", "Gốc", "R2-ROOT", null, OpenFrom2020);
        var mid = await CreateOrgUnitAsync("R2-MID", "Giữa (sẽ đóng)", "R2-MID", root, OpenFrom2020);
        var child = await CreateOrgUnitAsync("R2-CHILD", "Con", "R2-CHILD", mid, OpenFrom2020);
        var grandchild = await CreateOrgUnitAsync("R2GRAND", "Cháu", "R2GRAND", child, OpenFrom2020);

        await CloseOrgUnitInPeriodAsync(mid);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, root, "tester");
        var rows = await OrgUnits.GetInScopeAsync(scope, Today);
        var ids = rows.Select(r => r.OrgUnitId).ToHashSet();

        // "mid" is closed (isactive=0) -> does NOT appear in the list of active versions (correct --
        // this is the standard 3-condition filter in the outer SELECT), but the subtree CTE still passes
        // THROUGH it -> child/grandchild remain within "root"'s OwnOrgUnitAndDescendants scope.
        Assert.DoesNotContain(mid, ids);
        Assert.Contains(child, ids);
        Assert.Contains(grandchild, ids);
    }

    [Fact]
    public async Task Subtree_RootedAtClosedNode_StillReturnsItsDescendants()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2B-ROOT", "Gốc", "R2B-ROOT", null, OpenFrom2020);
        var mid = await CreateOrgUnitAsync("R2B-MID", "Giữa (sẽ đóng)", "R2B-MID", root, OpenFrom2020);
        var child = await CreateOrgUnitAsync("R2BCHLD", "Con", "R2BCHLD", mid, OpenFrom2020);

        await CloseOrgUnitInPeriodAsync(mid);

        // The scope is anchored AT "mid" itself (closed): BuildSubtreeCte always includes @rootOrgUnitId as an
        // unconditional anchor ("SELECT @rootOrgUnitId AS id" -- no isactive filter) -> child is still included.
        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, mid, "tester");
        var rows = await OrgUnits.GetInScopeAsync(scope, Today);
        var ids = rows.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Contains(child, ids);
    }

    [Fact]
    public async Task Subtree_LeafOrgUnit_ContainsOnlyItself()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2LEAF", "Gốc lá", "R2LEAF", null, OpenFrom2020);
        var leaf = await CreateOrgUnitAsync("R2-LEAF", "Lá", "R2-LEAF", root, OpenFrom2020);

        var scope = new DataScope(ScopeLevel.OwnOrgUnitAndDescendants, leaf, "tester");
        var rows = await OrgUnits.GetInScopeAsync(scope, Today);
        var ids = rows.Select(r => r.OrgUnitId).ToHashSet();

        Assert.Single(ids);
        Assert.Contains(leaf, ids);
        Assert.DoesNotContain(root, ids);
    }

    // rn=1 is stable (D6: no overlapping active periods) -- a closed node has EXACTLY 1 in-period row at
    // @today (its sole isactive=0 version) -> ROW_NUMBER() in today_ou has no ranking contention.
    [Fact]
    public async Task ClosedNode_HasExactlyOneInPeriodVersionRow_AtToday()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2C-ROOT", "Gốc", "R2C-ROOT", null, OpenFrom2020);
        var mid = await CreateOrgUnitAsync("R2C-MID", "Giữa", "R2C-MID", root, OpenFrom2020);
        await CloseOrgUnitInPeriodAsync(mid);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM org_unit_version WHERE org_unit_id = @mid " +
            "AND effective_from <= @today AND @today <= effective_to",
            new { mid, today = Today });

        Assert.Equal(1, count);
    }

    // [Q1/D8 — STRICT reverse-FK] Closing the parent (remaining coverage = EMPTY) while a child STILL depends on it for
    // coverage -> BLOCKED ("TemporalFk.DependentsUncovered"). The correct procedure: re-parent the child to the grandparent
    // first (the next test confirms that after re-parenting, the block goes away).
    [Fact]
    public async Task ValidateParentChange_ClosingParentWithDependentChild_IsBlocked()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2D-ROOT", "Gốc", "R2D-ROOT", null, OpenFrom2020);
        var parent = await CreateOrgUnitAsync("R2DPAR", "Cha sẽ đóng", "R2DPAR", root, OpenFrom2020);
        _ = await CreateOrgUnitAsync("R2DCHLD", "Con phụ thuộc", "R2DCHLD", parent, OpenFrom2020);

        var result = FkValidator.ValidateParentChange("org_unit_version", parent, remainingParentCoverage: [], ambientTransaction: null);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "TemporalFk.DependentsUncovered");
    }

    [Fact]
    public async Task ValidateParentChange_AfterReparentingChildAway_ClosingParentIsAllowed()
    {
        SkipUnlessDbAvailable();

        var root = await CreateOrgUnitAsync("R2E-ROOT", "Gốc", "R2E-ROOT", null, OpenFrom2020);
        var parent = await CreateOrgUnitAsync("R2EPAR", "Cha sẽ đóng", "R2EPAR", root, OpenFrom2020);
        var child = await CreateOrgUnitAsync("R2ECHLD", "Con sẽ re-parent", "R2ECHLD", parent, OpenFrom2020);

        // Re-parents the child to "root" BEFORE closing "parent" -- following the R2 procedure.
        var reparent = await OrgUnits.UpsertAsync(
            child, OpenFrom2020, "R2ECHLD", "Con sẽ re-parent", "R2ECHLD", root, VersionOperationKind.Edit, "tester",
            "reparent-before-close");
        Assert.False(reparent.IsError, DescribeErrors(reparent.Errors));

        var result = FkValidator.ValidateParentChange("org_unit_version", parent, remainingParentCoverage: [], ambientTransaction: null);

        Assert.False(result.IsError, DescribeErrors(result.Errors));
    }
}
