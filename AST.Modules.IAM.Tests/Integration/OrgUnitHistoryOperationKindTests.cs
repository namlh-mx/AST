using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Phase 4d task 1 — per-row `operation_kind` recording (VersionOperationKind: Add/Edit/Close/Cancel) +
// GetHistoryInScopeAsync's parent-as-of JOIN.
public sealed class OrgUnitHistoryOperationKindTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DataScope GlobalScope = new(ScopeLevel.Global, null, "tester");

    [Fact]
    public async Task UpsertAsync_Add_RecordsOperationKindAdd()
    {
        SkipUnlessDbAvailable();

        var id = await InsertHeaderAsync("org_unit");
        var r = await OrgUnits.UpsertAsync(
            id, OpenFrom2020, "OPKADD", "Đơn vị Add", "Add", null, VersionOperationKind.Add, "tester", "create");
        Assert.False(r.IsError, DescribeErrors(r.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        var row = Assert.Single(history);
        Assert.Equal(VersionOperationKind.Add, row.OperationKind);
    }

    [Fact]
    public async Task UpsertAsync_Edit_RecordsOperationKindEdit()
    {
        SkipUnlessDbAvailable();

        // A CLOSED base period so the follow-up upsert can be genuinely adjacent (case 2: F=b+1, no overlap,
        // no remnant) -- isolates the assertion to exactly 2 rows: the original Add row + the new Edit row.
        var basePeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));
        var id = await CreateOrgUnitAsync("OPKEDIT", "Đơn vị Edit", "Edit", null, basePeriod);

        var edit = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2021, 1, 1), EffectivePeriod.OpenEnd),
            "OPKEDIT2", "Đơn vị Edit (2021)", "Edit2", null, VersionOperationKind.Edit, "tester", "extend");
        Assert.False(edit.IsError, DescribeErrors(edit.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        var editedRow = history.Single(r => r.Id == edit.Value.NewVersionId);
        Assert.Equal(VersionOperationKind.Edit, editedRow.OperationKind);

        // The original Add row is untouched by this Edit action -- still records its own original kind.
        var originalRow = history.Single(r => r.Id != edit.Value.NewVersionId);
        Assert.Equal(VersionOperationKind.Add, originalRow.OperationKind);
    }

    [Fact]
    public async Task CloseVersionAsync_RecordsOperationKindClose()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("OPKCLOSE", "Đơn vị Close", "Close", null, OpenFrom2020);
        var current = await OrgUnits.GetByIdentityAsync(id, Today);
        Assert.False(current.IsError, DescribeErrors(current.Errors));

        var close = await OrgUnits.CloseVersionAsync(id, current.Value.Id, Today.AddYears(1), new OperationDate(Today), "tester", "close");
        Assert.False(close.IsError, DescribeErrors(close.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        // The remnant produced by CloseVersionAsync (the still-active row after the cut) records Close.
        var remnant = history.Single(r => r.Id == close.Value.NewVersionId);
        Assert.Equal(VersionOperationKind.Close, remnant.OperationKind);

        // The original (now-inactive) row keeps its ORIGINAL kind -- Close does not rewrite it.
        var original = history.Single(r => r.Id == current.Value.Id);
        Assert.Equal(VersionOperationKind.Add, original.OperationKind);
    }

    [Fact]
    public async Task CancelPlanAsync_RecordsOperationKindCancel()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("OPKCXL", "Đơn vị Cancel", "Cancel", null, OpenFrom2020);

        // Overlaps the open-ended base (case 4) -> cuts it to a head remnant [2020-01-01,2026-12-31],
        // mirroring the qa-fix scenario (2026-07-22) CancelPlanAsync_FutureVersion_OverlapCut_RestoresPredecessorCoverage.
        var future = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd),
            "OPKCXL", "Kế hoạch 2027", "KH27", null, VersionOperationKind.Edit, "tester", "plan");
        Assert.False(future.IsError, DescribeErrors(future.Errors));

        var cancel = await OrgUnits.CancelPlanAsync(id, future.Value.NewVersionId, Today, "tester", "bỏ kế hoạch");
        Assert.False(cancel.IsError, DescribeErrors(cancel.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);

        // The target row (the cancelled future plan) keeps whatever kind it was CREATED as (Edit) -- the
        // cancel UPDATE (isactive=0, status='cancelled') never overwrites operation_kind.
        var targetRow = history.Single(r => r.Id == future.Value.NewVersionId);
        Assert.Equal(VersionOperationKind.Edit, targetRow.OperationKind);

        // The restored predecessor coverage (a NEW remnant row extending back to the original open end) is
        // recorded as produced by the Cancel action.
        var restored = history.Single(r => r.IsActive && r.EffectiveTo == EffectivePeriod.OpenEnd);
        Assert.Equal(VersionOperationKind.Cancel, restored.OperationKind);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_ResolvesParentAsOfEachVersionsEffectiveFrom()
    {
        SkipUnlessDbAvailable();

        // Parent: 2 adjacent (no-gap) versions under DIFFERENT org_code/org_name_full_vn, covering 2 distinct periods.
        var parentPeriod1 = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2022, 12, 31));
        var parentPeriod2 = new EffectivePeriod(new DateOnly(2023, 1, 1), EffectivePeriod.OpenEnd);
        var parent = await CreateOrgUnitAsync("PARA", "Cha A", "Cha A", null, parentPeriod1);
        var parentEdit = await OrgUnits.UpsertAsync(
            parent, parentPeriod2, "PARB", "Cha B", "Cha B", null, VersionOperationKind.Edit, "tester", "phase2");
        Assert.False(parentEdit.IsError, DescribeErrors(parentEdit.Errors));

        // Child: 1st version falls inside the parent's period-1, 2nd (adjacent, no gap) falls inside period-2.
        var childPeriod1 = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2022, 12, 31));
        var childPeriod2 = new EffectivePeriod(new DateOnly(2023, 1, 1), EffectivePeriod.OpenEnd);
        var child = await CreateOrgUnitAsync("CHA", "Con", "Con", parent, childPeriod1);
        var childEdit = await OrgUnits.UpsertAsync(
            child, childPeriod2, "CHA", "Con", "Con", parent, VersionOperationKind.Edit, "tester", "phase2");
        Assert.False(childEdit.IsError, DescribeErrors(childEdit.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, child);
        Assert.Equal(2, history.Count);

        var row1 = history.Single(r => r.EffectiveFrom == childPeriod1.From);
        Assert.Equal("PARA", row1.ParentOrgCodeAsOf);
        Assert.Equal("Cha A", row1.ParentOrgNameFullVnAsOf);

        var row2 = history.Single(r => r.EffectiveFrom == childPeriod2.From);
        Assert.Equal("PARB", row2.ParentOrgCodeAsOf);
        Assert.Equal("Cha B", row2.ParentOrgNameFullVnAsOf);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_RootIdentity_ParentAsOfColumnsAreNull()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("OPKROOT", "Đơn vị gốc", "Gốc", null, OpenFrom2020);

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);
        var row = Assert.Single(history);
        Assert.Null(row.ParentOrgCodeAsOf);
        Assert.Null(row.ParentOrgNameFullVnAsOf);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_ParentWasEdited_NoDuplicateHistoryRows()
    {
        SkipUnlessDbAvailable();

        // Parent EDITED via an exact-match upsert (case 7, PeriodEditor): the old parent row is fully
        // superseded (isactive=0) but its period STILL fully overlaps the new isactive=1 row's period.
        // Without `AND p.isactive = 1` on the parent side of GetHistoryInScopeAsync's JOIN, BOTH parent rows
        // would match a child version whose EffectiveFrom falls in that shared span, duplicating the
        // child's history row (Critical 1 -- the earlier tests only ever built a parent via 2 adjacent,
        // non-overlapping upserts, so they never produced this fanout).
        var parentPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2025, 12, 31));
        var parent = await CreateOrgUnitAsync("EDPA", "Cha gốc", "Cha gốc", null, parentPeriod);
        var parentEdit = await OrgUnits.UpsertAsync(
            parent, parentPeriod, "EDPB", "Cha sửa", "Cha sửa", null, VersionOperationKind.Edit, "tester", "rename");
        Assert.False(parentEdit.IsError, DescribeErrors(parentEdit.Errors));

        var child = await CreateOrgUnitAsync("EDCA", "Con", "Con", parent, parentPeriod);

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, child);

        // Exactly one history row for the child's single version -- not duplicated by parent JOIN fanout.
        var row = Assert.Single(history);
        Assert.Equal("EDPB", row.ParentOrgCodeAsOf);
    }

    [Fact]
    public async Task GetHistoryInScopeAsync_ChildEffectiveFromEqualsParentEffectiveTo_ResolvesParent()
    {
        SkipUnlessDbAvailable();

        // The parent's period ENDS exactly on the day the child version BEGINS -- closed-closed
        // containment (h.effective_from >= p.effective_from AND h.effective_from <= p.effective_to) must
        // still resolve the parent for that boundary date. A half-open `<` on p.effective_to (the
        // pre-fix form) would wrongly leave this child version parentless (Important 2).
        var parentPeriod = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2022, 12, 31));
        var parent = await CreateOrgUnitAsync("BNDA", "Cha biên", "Cha biên", null, parentPeriod);

        var childPeriod = new EffectivePeriod(new DateOnly(2022, 12, 31), new DateOnly(2022, 12, 31));
        var child = await CreateOrgUnitAsync("BNDC", "Con biên", "Con biên", parent, childPeriod);

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, child);
        var row = Assert.Single(history);
        Assert.Equal("BNDA", row.ParentOrgCodeAsOf);
        Assert.Equal("Cha biên", row.ParentOrgNameFullVnAsOf);
    }

    [Fact]
    public async Task UpsertAsync_OverlapCutProducesRemnant_RemnantRecordsSameOperationKind()
    {
        SkipUnlessDbAvailable();

        // Case 5 (subperiod, split into 2, PeriodEditor): the base period is split into a HEAD + TAIL
        // remnant by a REGULAR Upsert -- these remnants are produced by InsertRemnantAsync, not by
        // CloseVersionAsync/CancelVersionAsync, so they must carry the SAME operation kind passed to the
        // Upsert call that produced them (Important 3c -- only Close/Cancel remnants were covered before).
        var id = await CreateOrgUnitAsync("RMNA", "Đơn vị Remnant", "Remnant", null, OpenFrom2020);

        var middle = await OrgUnits.UpsertAsync(
            id, new EffectivePeriod(new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31)),
            "RMNB", "Đơn vị Remnant giữa", "RemnantB", null, VersionOperationKind.Edit, "tester", "carve-out");
        Assert.False(middle.IsError, DescribeErrors(middle.Errors));

        var history = await OrgUnits.GetHistoryInScopeAsync(GlobalScope, id);

        var headRemnant = history.Single(r =>
            r.EffectiveFrom == new DateOnly(2020, 1, 1) && r.EffectiveTo == new DateOnly(2021, 12, 31));
        Assert.Equal(VersionOperationKind.Edit, headRemnant.OperationKind);

        var tailRemnant = history.Single(r =>
            r.EffectiveFrom == new DateOnly(2023, 1, 1) && r.EffectiveTo == EffectivePeriod.OpenEnd);
        Assert.Equal(VersionOperationKind.Edit, tailRemnant.OperationKind);
    }
}
