using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Seam-2 gap F3 (docs/shared-components.md VersionedRepository row / decision "extend P11 auto-cut to
// Cancel"): CancelVersionAsync used to call ValidateParentChange UNCONDITIONALLY and never consulted
// ExclusivelyOwnedDependents — a pending version with a pending exclusively-owned dependent always BLOCKed.
// This file pins the new behaviour, symmetric with CloseVersionAsync's existing P11 wiring
// (AutoCutDependentTests.cs is the shape template):
//   1. predecessor-restore branch — coverage-neutral, auto-cut is a genuine no-op, dependent untouched;
//   2. no-predecessor branch, a dependent whose coverage the cancelled plan itself provided from an
//      EARLIER date than the plan started — successfully auto-cut, same-transaction audit row, no
//      DELETE/UPDATE-over-business-columns;
//   3. no-predecessor branch, a dependent that genuinely CANNOT be gracefully shrunk (it needs the
//      cancelled plan's OWN coverage from its own start) — AutoCut fails and rolls the WHOLE cancel
//      back. Since B1 (2026-08-15) that outcome is CONDITIONAL, and this file is where the negative
//      condition is proven: `SelfOwningOrgUnitRepository` deliberately declares
//      `DependentSupportsCancellation: false`, so this edge keeps the pre-B1 BLOCK even for a
//      dependent that never took effect. The Role edge opts IN and therefore CANCELS the same shape
//      instead (`AutoCutDependentTests.CancelRole_SameDayRoleWithSameDayGrant_CancelsGrantWithRole`) —
//      these two files are the two halves of one capability gate, not mirrors of each other;
//   4. regression guard on the REAL production OrgUnitRepository (ExclusivelyOwnedDependents = [] by
//      default) — an unrelated dependent still BLOCKs via reverse-FK exactly as before this feature.
//
// Uses SelfOwningOrgUnitRepository (TestSupport) for cases 1-3 to exercise a SELF-REFERENCING
// (parent → child of the same entity) dependent shape against the shared VersionedRepository engine
// directly. That shape is distinct from Role's linear parent → grant shape, which is covered against the
// PRODUCTION RoleRepository by AutoCutDependentTests' `CancelRole_*` tests (see that file's header comment).
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class CancelAutoCutTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod FuturePlan2027 = new(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Bounded2020To2025Mid = new(new DateOnly(2020, 1, 1), new DateOnly(2025, 6, 30));
    private static readonly DateOnly DependentSeedFrom = new(2024, 1, 1);
    private static readonly EffectivePeriod TodayOpen = new(Today, EffectivePeriod.OpenEnd);

    // ---------------------------------------------------------------------------------------
    // 1 — predecessor found: coverage restored, auto-cut is a no-op, dependent untouched.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_WithPredecessor_CoverageRestored_ExclusivelyOwnedDependent_IsUntouched()
    {
        SkipUnlessDbAvailable();

        var repo = BuildRepo();
        var parent = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(parent, OpenFrom2020, "F3P1", null, "tester", "seed")).IsError.Should().BeFalse();
        // Overlap-cuts the OpenFrom2020 version down to [2020-01-01, 2026-12-31] and inserts a NEW
        // [2027-01-01, open) version — a genuine ADJACENT predecessor (same shape as CW-R1 in
        // CompositeWriteTests / role-rename tests), not a synthetic setup.
        var upsertFuture = await repo.UpsertAsync(parent, FuturePlan2027, "F3P1", null, "tester", "future plan");
        upsertFuture.IsError.Should().BeFalse(DescribeErrors(upsertFuture.Errors));

        var child = await repo.CreateIdentityAsync();
        var childPeriod = new EffectivePeriod(new DateOnly(2020, 6, 1), new DateOnly(2026, 12, 31));
        (await repo.UpsertAsync(child, childPeriod, "F3C1", parent, "tester", "seed")).IsError.Should().BeFalse();
        var childVersionIdBefore = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", child);

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent, FuturePlan2027.From);

        var result = await repo.CancelPlanAsync(parent, targetVersionId, Today, "canceller", "hủy kế hoạch");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        // Predecessor restored back to open-end (coverage-neutral).
        var restored = await GetOrgUnitRowAsync(parent);
        restored.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
        restored.IsActive.Should().BeTrue();

        // The dependent must be COMPLETELY untouched — same version row, never cut.
        var childVersionIdAfter = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", child);
        childVersionIdAfter.Should().Be(childVersionIdBefore, "auto-cut must be a no-op when coverage was restored, not shrunk");
        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", child)).Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------
    // 2 — no predecessor, dependent CAN be gracefully shrunk: successful auto-cut, audit row propagated.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_NoPredecessor_ExclusivelyOwnedDependent_IsAutoCutInSameTransaction_WithAuditRow()
    {
        SkipUnlessDbAvailable();

        var repo = BuildRepo();
        var parent = await repo.CreateIdentityAsync();
        // 2 DISJOINT active versions (a real gap between them, D7 warning only — GapIsBlocking is false
        // for this test double): [2020-01-01, 2025-06-30] then the future plan [2027-01-01, open). The
        // adjacency check (EffectiveTo == target.EffectiveFrom - 1 day) does NOT match here
        // (2025-06-30 != 2026-12-31) -> genuinely NO predecessor, yet `remaining` after cancelling the
        // future plan is NOT empty (it still has the older bounded version) — the shape needed for a
        // dependent to be gracefully, successfully shrunk rather than unconditionally blocked.
        (await repo.UpsertAsync(parent, Bounded2020To2025Mid, "F3P2", null, "tester", "seed")).IsError.Should().BeFalse();
        (await repo.UpsertAsync(parent, FuturePlan2027, "F3P2", null, "tester", "future plan")).IsError.Should().BeFalse();

        var child = await repo.CreateIdentityAsync();
        // Seeded directly (bypasses STRICT temporal-FK coverage validation): open-ended, so its own
        // continuous coverage could never have been validated through the normal Upsert path (the parent
        // itself has a gap from 2025-07-01 to 2026-12-31) — exactly the shape needed to exercise
        // AutoCut's remnant-cut arithmetic against the OLDER bounded version's own natural boundary.
        var seededVersionId = await repo.SeedDependentVersionAsync(child, parent, DependentSeedFrom, EffectivePeriod.OpenEnd, "seed");

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent, FuturePlan2027.From);

        var result = await repo.CancelPlanAsync(parent, targetVersionId, Today, "canceller", "hủy kế hoạch");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        // Parent: the future plan is cancelled; its own older, unrelated version is untouched.
        var parentRows = await ReadOrgUnitVersionsAsync(parent);
        parentRows.Should().ContainSingle(r => r.Id == targetVersionId).Subject.Cancelled.Should().BeTrue();
        parentRows.Should().ContainSingle(r => r.IsActive).Subject.EffectiveTo.Should().Be(new DateOnly(2025, 6, 30));

        // Dependent: original row soft-deactivated (never deleted), a NEW remnant row shrunk to the last
        // day the OLDER version actually covers, carrying the CANCEL's OWN recordedBy/reason.
        var childRows = await ReadOrgUnitVersionsAsync(child);
        childRows.Should().HaveCount(2);
        childRows.Should().Contain(r => r.Id == seededVersionId && !r.IsActive,
            "the pre-cut dependent version must be soft-deactivated, never physically deleted");

        var cutRow = childRows.Should().ContainSingle(r => r.IsActive).Subject;
        cutRow.Id.Should().NotBe(seededVersionId);
        cutRow.EffectiveFrom.Should().Be(DependentSeedFrom);
        cutRow.EffectiveTo.Should().Be(new DateOnly(2025, 6, 30));
        cutRow.RecordedBy.Should().Be("canceller");
        cutRow.Reason.Should().Be("hủy kế hoạch");
    }

    // ---------------------------------------------------------------------------------------
    // 3 — no predecessor, dependent CANNOT be gracefully shrunk (needs the cancelled plan's own coverage
    // from its own start): AutoCut fails, the WHOLE cancel rolls back.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_NoPredecessor_DependentCutFails_RollsBackWholeCancel()
    {
        SkipUnlessDbAvailable();

        var repo = BuildRepo();
        var parent = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(parent, FuturePlan2027, "F3P3", null, "tester", "seed")).IsError.Should().BeFalse();

        var child = await repo.CreateIdentityAsync();
        // Created via the NORMAL validated Upsert path -> its coverage can only have come from the
        // parent's future plan, so its EffectiveFrom is necessarily >= the plan's own start.
        var childPeriod = new EffectivePeriod(new DateOnly(2027, 6, 1), EffectivePeriod.OpenEnd);
        (await repo.UpsertAsync(child, childPeriod, "F3C3", parent, "tester", "seed")).IsError.Should().BeFalse();

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent);
        var parentRowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent);
        var childRowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", child);

        var result = await repo.CancelPlanAsync(parent, targetVersionId, Today, "canceller", "hủy kế hoạch");

        result.IsError.Should().BeTrue("the dependent needs the cancelled plan's own coverage from its own start and cannot be shrunk to before it");
        result.Errors.Should().Contain(e => e.Code == "VersionedRepository.BaseVersionRequired");

        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent)).Should().Be(parentRowsBefore);
        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", child)).Should().Be(childRowsBefore);
        var parentRow = await GetOrgUnitRowAsync(parent);
        parentRow.IsActive.Should().BeTrue("the cancel must have been rolled back, leaving the plan active");
        parentRow.Cancelled.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // 5 — same-day cancel (D1, 2026-08-10): target's EffectiveFrom == today (no completed effective
    // day, so it IS cancellable per the new `<` boundary), no adjacent predecessor, and an exclusively
    // owned dependent ALSO starts today. AutoCut's per-dependent cut point derives to today-1, which is
    // strictly before the dependent's own start (today) -> BLOCK (BaseVersionRequired), rolling back the
    // WHOLE cancel. Pins that both the D8 reverse-FK check AND the P11 auto-cut still run for a same-day
    // cancel, not only a strictly-future one.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_SameDayStart_ExclusivelyOwnedDependentAlsoStartsToday_RollsBackWholeCancel()
    {
        SkipUnlessDbAvailable();

        var repo = BuildRepo();
        var parent = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(parent, TodayOpen, "F3P5", null, "tester", "seed")).IsError.Should().BeFalse();

        var child = await repo.CreateIdentityAsync();
        // Created via the NORMAL validated Upsert path -> its coverage can only have come from the
        // parent's own version, so its EffectiveFrom is necessarily >= the parent's own start (today).
        (await repo.UpsertAsync(child, TodayOpen, "F3C5", parent, "tester", "seed")).IsError.Should().BeFalse();

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent);
        var parentRowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent);
        var childRowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", child);

        var result = await repo.CancelPlanAsync(parent, targetVersionId, Today, "canceller", "hủy kế hoạch");

        result.IsError.Should().BeTrue("the dependent needs the cancelled version's own coverage from today and cannot be shrunk to before it");
        result.Errors.Should().Contain(e => e.Code == "VersionedRepository.BaseVersionRequired");

        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent)).Should().Be(parentRowsBefore);
        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", child)).Should().Be(childRowsBefore);
        var parentRow = await GetOrgUnitRowAsync(parent);
        parentRow.IsActive.Should().BeTrue("the cancel must have been rolled back, leaving the plan active");
        parentRow.Cancelled.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // 4 — regression guard on the REAL production OrgUnitRepository: an unrelated (not exclusively
    // owned) dependent still BLOCKs via reverse-FK exactly as before this feature.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_OnRealOrgUnitRepository_NonExclusivelyOwnedUserDependent_StillBlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        // Brand-new org unit whose ONLY version is a future plan -> no predecessor.
        var org = await CreateOrgUnitAsync("F3-ORG", "Đơn vị", "F3-ORG", null, FuturePlan2027);
        var role = await CreateRoleAsync("F3-ROLE", "Vai trò", FuturePlan2027);
        var user = await CreateUserHeaderAsync();
        (await Users.UpsertAsync(user, FuturePlan2027, "f3.u", "U", org, role, "tester", "seed")).IsError.Should().BeFalse();

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", org);
        var userRowsBefore = await CountVersionRowsAsync("user_version", "user_id", user);

        var result = await OrgUnits.CancelPlanAsync(org, targetVersionId, Today, "canceller", "hủy đơn vị");

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");
        (await CountVersionRowsAsync("user_version", "user_id", user)).Should().Be(
            userRowsBefore, "a non-exclusively-owned dependent must not be touched at all");
    }

    // ---------------------------------------------------------------------------------------
    // 9 — V010/Task 2: the durable lifecycle marker moved from the dropped `cancelled` boolean to the
    // `status` column. Asserts the RAW column (not the DTO): the mapping (Task 2 Step 4/6) and the
    // write (this test) are two different claims, and a DTO-only assertion would pass even if the
    // write itself still wrote the old shape and only the mapping happened to compensate.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelVersionAsync_WritesCancelledStatusAndInactive()
    {
        SkipUnlessDbAvailable();

        // Same arrange shape as case 4 above: a brand-new org unit whose ONLY version is a future
        // plan -> no predecessor, so CancelPlanAsync cancels it outright.
        var org = await CreateOrgUnitAsync("F3-V010", "Đơn vị", "F3-V010", null, FuturePlan2027);
        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", org);

        var result = await OrgUnits.CancelPlanAsync(org, targetVersionId, Today, "canceller", "hủy kế hoạch");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var row = await connection.QuerySingleAsync<(bool IsActive, string Status)>(
            "SELECT isactive AS IsActive, status AS Status FROM org_unit_version WHERE id = @versionId",
            new { versionId = targetVersionId });

        row.IsActive.Should().BeFalse();
        row.Status.Should().Be("cancelled");
    }

    // ---------------------------------------------------------------------------------------

    private SelfOwningOrgUnitRepository BuildRepo() =>
        new(
            new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!)),
            new StandardScopeFilterBuilder(),
            new EffectivePeriodResolver(),
            new PeriodEditor(),
            FkValidator,
            IamTemporalFkEdges.CreateRegistry(),
            new FixedBusinessDateProvider(Today));

    private sealed class OrgUnitVersionRow
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public bool Cancelled { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly EffectiveTo { get; init; }
        public string RecordedBy { get; init; } = string.Empty;
        public string? Reason { get; init; }
    }

    private async Task<OrgUnitVersionRow> GetOrgUnitRowAsync(long orgUnitId)
    {
        var rows = await ReadOrgUnitVersionsAsync(orgUnitId);
        return rows.Single(r => r.IsActive);
    }

    private async Task<IReadOnlyList<OrgUnitVersionRow>> ReadOrgUnitVersionsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<OrgUnitVersionRow>(
            """
            SELECT id AS Id, isactive AS IsActive, (status = 'cancelled') AS Cancelled,
                   effective_from AS EffectiveFrom, effective_to AS EffectiveTo,
                   recorded_by AS RecordedBy, reason AS Reason
            FROM org_unit_version WHERE org_unit_id = @orgUnitId
            """,
            new { orgUnitId });
        return rows.ToList();
    }

    private async Task<long> GetActiveVersionIdAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT id FROM {versionTable} WHERE {identityColumn} = @identityId AND isactive = 1",
            new { identityId });
    }

    private async Task<long> GetActiveVersionIdAsync(string versionTable, string identityColumn, long identityId, DateOnly asOf)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT id FROM {versionTable} WHERE {identityColumn} = @identityId AND isactive = 1 " +
            "AND effective_from <= @asOf AND @asOf <= effective_to",
            new { identityId, asOf });
    }

    private async Task<long> CountVersionRowsAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {versionTable} WHERE {identityColumn} = @identityId",
            new { identityId });
    }
}
