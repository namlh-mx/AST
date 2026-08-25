using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Pins principle P11 "aggregate auto-cut" (§15.2 D-7; §16.1 capability 2, spec §16.2 acceptance items
// 4 and 5 — the IAM declaration-screens business analysis).
//
// Two halves, and BOTH must hold: a dependent declared in ExclusivelyOwnedDependents is cut in the
// SAME transaction as its parent's Close and always leaves a normal audit row (never a silent
// DELETE/UPDATE — hard invariant #1); a dependent that is NOT declared, or is shared with another
// aggregate, still BLOCKs on the reverse-FK check exactly as before. The two GUARD tests pin that
// second half — they are what stops the auto-cut from widening into data it does not own.
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class AutoCutDependentTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DateOnly CutAt = Today.AddDays(-1);
    private static readonly DateOnly DayAfterCut = Today;

    // CloseVersionAsync is public on VersionedRepository but only IOrgUnitRepository re-declares it, so the
    // role/function cases go through the concrete repositories (InternalsVisibleTo AST.Modules.IAM.Tests).
    private RoleRepository RoleRepo => (RoleRepository)Roles;
    private FunctionRepository FunctionRepo => (FunctionRepository)Functions;

    // ---------------------------------------------------------------------------------------
    // Item 4 — the parent Close auto-cuts a dependent it EXCLUSIVELY OWNS, in the same transaction,
    // with a normal audit row, and the reverse-FK check does NOT block that path.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CloseRole_ExclusivelyOwnedGrant_IsAutoCutInSameTransaction_WithAuditRow()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("AC-R1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Ac.Fn.One", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var originalGrantVersionId = (await RolePermissions.GetByIdentityAsync(grant, Today)).Value.Id;
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var result = await RoleRepo.CloseVersionAsync(role, roleVersionId, CutAt, new OperationDate(Today), "closer", "đóng vai trò");

        // P11: the reverse-FK check must NOT block here — the grant is cut first, in the SAME transaction.
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        result.Errors.Should().NotContain(e => e.Code == "TemporalFk.DependentsUncovered");

        // The role itself is cut.
        var roleAfter = await Roles.GetByIdentityAsync(role, new DateOnly(2022, 6, 1));
        roleAfter.IsError.Should().BeFalse(DescribeErrors(roleAfter.Errors));
        roleAfter.Value.EffectiveTo.Should().Be(CutAt);

        // The dependent grant is cut to the same day — and no longer resolves after it.
        var grantAtCut = await RolePermissions.GetByIdentityAsync(grant, CutAt);
        grantAtCut.IsError.Should().BeFalse(DescribeErrors(grantAtCut.Errors));
        grantAtCut.Value.EffectiveTo.Should().Be(CutAt);
        (await RolePermissions.GetByIdentityAsync(grant, DayAfterCut)).IsError.Should().BeTrue(
            "the grant must not survive the parent role it exclusively belongs to");

        // Audit trail, not a silent DELETE/UPDATE (hard invariant #1): the original row still exists,
        // soft-deactivated, and the cut produced a NEW row carrying the parent write's recorded_by/reason.
        var rows = await ReadGrantVersionsAsync(grant);
        rows.Should().HaveCountGreaterThan(1);
        rows.Should().Contain(r => r.Id == originalGrantVersionId && !r.IsActive,
            "the pre-cut grant version must be soft-deactivated, never physically deleted");

        var cutRow = rows.Should().ContainSingle(r => r.IsActive).Subject;
        cutRow.Id.Should().NotBe(originalGrantVersionId);
        cutRow.EffectiveTo.Should().Be(CutAt);
        cutRow.RecordedBy.Should().Be("closer");
        cutRow.Reason.Should().Be("đóng vai trò");
        cutRow.ScopeLevel.Should().Be((byte)ScopeLevel.Global, "the auto-cut copies the business columns verbatim");
    }

    // REWRITTEN (B1, 2026-08-15) from *_ParentCutRollsBackWhenDependentCutFails, which pinned the
    // pre-B1 answer (BaseVersionRequired) for a grant starting on the role's own cut boundary. Under
    // B1 that grant has not completed a single effective day when the role closes at CutAt (Today-1),
    // so it is now CANCELLED with the role instead of blocking the whole close — this test's every
    // assertion not about that changed behaviour (unchanged role row shape) survives, and "auto-cut is
    // entered, not bypassed" now reads directly off the durable cancelled marker rather than an error code (the
    // original test never pinned a code either — see Step 1's report).
    [Fact]
    public async Task CloseRole_ExclusivelyOwnedGrant_ThatNeverTookEffect_IsCancelledWithRole()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("AC-R2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Ac.Fn.Two", OpenFrom2020);
        var lateGrantPeriod = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var grant = await CreateGrantAsync(role, function, lateGrantPeriod, ScopeLevel.Global);

        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var result = await RoleRepo.CloseVersionAsync(role, roleVersionId, CutAt, new OperationDate(Today), "closer", "đóng vai trò");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "the grant never had an effective day under this role, so it is cancelled with it");
        (await CountActiveAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            0, "a cancelled grant is not left active");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "cancelling writes no remnant row");
    }

    // ---------------------------------------------------------------------------------------
    // Item 5 — the P11 exclusivity condition is enforced IN CODE: the same dependent, approached from a
    // parent that does NOT exclusively own it, is still BLOCKed by the reverse-FK check.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CloseFunction_SameGrantButNotExclusivelyOwned_StillBlockedByReverseFk_WhileRoleCloseCuts()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("AC-R3", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Ac.Fn.Three", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);
        var functionVersionId = (await Functions.GetByIdentityAsync(function, Today)).Value.Id;

        // (a) The function is a temporal-FK PARENT of the grant, but it does not OWN it — the grant belongs
        //     to the role. P11 must not fire here: BLOCK, exactly as before this feature existed.
        var functionClose = await FunctionRepo.CloseVersionAsync(function, functionVersionId, CutAt, new OperationDate(Today), "closer", "đóng chức năng");

        functionClose.IsError.Should().BeTrue();
        functionClose.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore, "a non-owning parent must not touch the dependent at all");
        (await RolePermissions.GetByIdentityAsync(grant, DayAfterCut)).IsError.Should().BeFalse(
            "the grant must still be fully intact after the blocked close");

        // (b) DISCRIMINATOR — on the very same fixture, the OWNING parent does cut it. Without this half,
        //     (a) would also pass with P11 switched off entirely, i.e. it would prove nothing.
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var roleClose = await RoleRepo.CloseVersionAsync(role, roleVersionId, CutAt, new OperationDate(Today), "closer", "đóng vai trò");

        roleClose.IsError.Should().BeFalse(DescribeErrors(roleClose.Errors));
        (await RolePermissions.GetByIdentityAsync(grant, DayAfterCut)).IsError.Should().BeTrue();
    }

    // GUARD (passes today, must KEEP passing after P11 lands) — the regression that catches an over-broad
    // implementation which auto-cuts every declared temporal-FK dependent instead of only owned ones.
    [Fact]
    public async Task CloseOrgUnit_UserIsNotExclusivelyOwned_StillBlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        SkipUnlessDbAvailable();

        // A user is its own aggregate (independently addressable, and also a child of a role) — P11 must
        // never auto-cut it just because its org unit is being closed. Guards against an over-broad
        // implementation that auto-cuts every declared temporal-FK dependent.
        var role = await CreateRoleAsync("AC-R4", "Vai trò", OpenFrom2020);
        var org = await CreateOrgUnitAsync("AC-ORG", "Đơn vị", "AC-ORG", null, OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        (await Users.UpsertAsync(user, OpenFrom2020, "ac.u", "U", org, role, "tester", "seed"))
            .IsError.Should().BeFalse();

        var userRowsBefore = await CountRowsAsync("user_version", "user_id", user);
        var orgVersionId = (await OrgUnits.GetByIdentityAsync(org, Today)).Value.Id;

        var result = await OrgUnits.CloseVersionAsync(org, orgVersionId, CutAt, new OperationDate(Today), "closer", "đóng đơn vị");

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");
        (await CountRowsAsync("user_version", "user_id", user)).Should().Be(userRowsBefore);
    }

    // Brief 058 FR1 — role→user reverse-FK BLOCK (user is NOT exclusively owned by role; P11
    // auto-cuts only role_permission grants). Mirrors CloseOrgUnit_UserIsNotExclusivelyOwned above.
    [Fact]
    public async Task CloseRole_UserDependentLosesCoverage_BlockedByReverseFk()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("AC-RU", "Role with user", OpenFrom2020);
        var org = await CreateOrgUnitAsync("ACRUORG", "Org for role-user TFK", "ACRUORG", null, OpenFrom2020);
        var user = await CreateUserHeaderAsync();
        (await Users.UpsertAsync(user, OpenFrom2020, "ac.ru", "U", org, role, "tester", "seed"))
            .IsError.Should().BeFalse();

        var userRowsBefore = await CountRowsAsync("user_version", "user_id", user);
        var roleVersionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var result = await RoleRepo.CloseVersionAsync(role, roleVersionId, CutAt, new OperationDate(Today), "closer", "close role");

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.DependentsUncovered");
        (await CountRowsAsync("user_version", "user_id", user)).Should().Be(userRowsBefore,
            "user_version must not be auto-cut or otherwise mutated when role Close is BLOCKed");
    }

    // ---------------------------------------------------------------------------------------
    // NEW — Cancel-plan + P11 together, first proven against the PRODUCTION RoleRepository (not the
    // TEST-ONLY SelfOwningOrgUnitRepository double CancelAutoCutTests.cs exercises for the org-tree self-
    // edge shape): mirrors this file's own Close+P11 pattern above (line 40), adapted to Cancel. Two
    // halves, because Cancel's own restore mechanism (VersionedRepository.CancelVersionAsync's class
    // comment) makes the two possible outcomes structurally different from Close's: a predecessor-restore
    // is ALWAYS coverage-neutral for whatever it restores (AutoCut is a genuine no-op -- first test below),
    // while a no-predecessor cancel that would strand an exclusively-owned dependent's OWN base coverage
    // cannot be gracefully shrunk and must BLOCK instead of silently losing data (second test below) --
    // there is no third "partial cut" outcome for Cancel (unlike Close, whose remnant-shrink IS the common
    // case: a dependent whose validly-written period falls anywhere inside the cancelled span either has
    // its full coverage restored via an adjacent predecessor, or none of it, by construction of D6
    // no-overlap + STRICT temporal-FK continuous-coverage).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelRole_FuturePlanOverlapCutsPredecessor_ExclusivelyOwnedGrantSurvives_AutoCutNoOp()
    {
        SkipUnlessDbAvailable();

        // Immediate writers cannot persist a bounded future plan, so SQL-seed the post-algebra shape
        // an Upsert subperiod-split would have produced: head remnant adjacent to the plan, plus tail.
        var head = new EffectivePeriod(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31));
        var role = await CreateRoleAsync("AC-CXR1", "Vai trò còn hiệu lực", head);
        var function = await CreateFunctionAsync("Ac.Cxr.One", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var futurePlan = new EffectivePeriod(new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var futureVersionId = await InsertRoleVersionAsync(role, "AC-CXR1", "Kế hoạch 2027", futurePlan);
        _ = await InsertRoleVersionAsync(
            role, "AC-CXR1", "Vai trò còn hiệu lực",
            new EffectivePeriod(new DateOnly(2028, 1, 1), EffectivePeriod.OpenEnd));

        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);

        var cancel = await RoleRepo.CancelPlanAsync(role, futureVersionId, Today, adminFlagChangeAuthorized: false, "tester", "bỏ kế hoạch");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));
        cancel.Errors.Should().NotContain(e => e.Code == "TemporalFk.DependentsUncovered");

        var history = await Roles.GetHistoryAsync(role);
        var targetRow = history.Should().ContainSingle(h => h.Id == futureVersionId).Subject;
        targetRow.IsActive.Should().BeFalse();
        targetRow.Status.Should().Be(VersionLifecycleStatus.Cancelled);

        // The grant's coverage spans the predecessor/plan boundary untouched -- proves
        // AutoCutExclusivelyOwnedAsync ran (in the same transaction as the restore + reverse-FK check)
        // against the REAL role_permission_version schema without wrongly cutting a still-fully-covered grant.
        (await RolePermissions.GetByIdentityAsync(grant, new DateOnly(2027, 6, 1))).IsError.Should().BeFalse(
            "the grant's coverage must be untouched by a coverage-neutral predecessor restore");

        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore,
            "AutoCut must be a genuine no-op here -- nothing to cut once the predecessor restore fully covers the grant again");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(
            roleRowsBefore + 1,
            "the predecessor restore inserts exactly one remnant row (target + predecessor are only soft-deactivated)");
    }

    // REWRITTEN (B1, 2026-08-15) from *_AutoCutBlocks_RollsBack, and rebuilt on a same-day role: the
    // 2027 future-plan scenario was never operator-reachable under Immediate
    // (RoleRepository.GuardImmediateUpsert:532 rejects any start but today; the old fixture only
    // reached it via CreateRoleAsync's raw-SQL seed) -- this covers the path an operator can actually
    // walk. A role whose FIRST-AND-ONLY version starts today, with a grant scheduled together with it,
    // has neither completed a single effective day when the plan is cancelled, so instead of BLOCKing
    // (the pre-B1 answer) the grant is now CANCELLED with the role. "Auto-cut is entered, not
    // bypassed" survives as the durable cancelled marker -- a stronger witness than the old error code: a plain,
    // non-P11 implementation (bare reverse-FK only) could never have let this cancel SUCCEED at all.
    [Fact]
    public async Task CancelRole_NoPredecessor_ExclusivelyOwnedGrantScheduledUnderPlan_IsCancelledWithRole()
    {
        SkipUnlessDbAvailable();

        var todayOnward = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-CXR2", "Kế hoạch hôm nay", todayOnward);
        var function = await CreateFunctionAsync("Ac.Cxr.Two", todayOnward);
        var grant = await CreateGrantAsync(role, function, todayOnward, ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var cancel = await RoleRepo.CancelPlanAsync(
            role, target.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "hủy kế hoạch");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "the grant never had an effective day under this plan -- a bare reverse-FK BLOCK could never have let this cancel succeed");
        (await CountActiveAsync("role_permission_version", "role_permission_id", grant)).Should().Be(0);
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "cancelling writes no remnant row");
    }

    // ---------------------------------------------------------------------------------------
    // Grow-only TOCTOU guard (brief 057 B): a dependent present in the in-transaction read but
    // absent from the probed/locked set must fail clear BEFORE any cut write. Calls the internal
    // AutoCutExclusivelyOwnedAsync directly (InternalsVisibleTo) with a hand-arranged empty locked
    // set — no flaky 2-connection race on CloseVersionAsync.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AutoCut_DependentPresentButNotInLockedSet_FailsDependentSetChanged_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("AC-R5", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Ac.Fn.Five", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);

        // Remaining coverage that WOULD gap the open grant — without the grow-guard AutoCut would
        // cut on Commit; empty locked set must fire DependentSetChanged first.
        var remaining = new List<EffectivePeriod> { new(OpenFrom2020.From, CutAt) };
        var emptyLocked = new HashSet<(string Table, long IdentityId)>();

        var connections = new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));
        using var connection = connections.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var result = await RoleRepo.AutoCutExclusivelyOwnedAsync(
            connection, transaction, role, remaining, Today, "closer", "probe-mismatch", emptyLocked);

        if (result.IsError)
        {
            transaction.Rollback();
        }
        else
        {
            transaction.Commit();
        }

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "VersionedRepository.DependentSetChanged");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore,
            "parent must stay uncut when the locked-set guard fires");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore, "dependent must not be written when it was missing from the locked set");
        (await RolePermissions.GetByIdentityAsync(grant, DayAfterCut)).IsError.Should().BeFalse(
            "the grant must remain open past the would-be cut date");
    }

    // ---------------------------------------------------------------------------------------
    // B1 (requester decision, 2026-08-15) — a dependent that never completed a single effective day is
    // CANCELLED with its parent instead of failing the parent's whole write. Covers both the cancel
    // branch and the close branch (the requester's extension of settled item 10), plus the two matrix
    // cases needed to discriminate the two wrong implementations that pass the simpler cases.
    // ---------------------------------------------------------------------------------------

    // B1 (requester decision, 2026-08-15): the first-day dead end. A role whose only version starts
    // today, with a grant that starts the same day, cannot shrink that grant when the role is
    // cancelled -- there is no coverage left to shrink into. The grant has not completed a single
    // effective day, so it is CANCELLED with its parent instead of failing the whole operation.
    [Fact]
    public async Task CancelRole_SameDayRoleWithSameDayGrant_CancelsGrantWithRole()
    {
        SkipUnlessDbAvailable();

        var todayOnward = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-B1-1", "Vai trò tạo hôm nay", todayOnward);
        var function = await CreateFunctionAsync("Ac.B1.One", todayOnward);
        var grant = await CreateGrantAsync(role, function, todayOnward, ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var cancel = await RoleRepo.CancelPlanAsync(
            role, target.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "hủy vai trò vừa tạo");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));

        (await CountActiveAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            0, "the grant never had an effective day, so it is cancelled with the role, not left active");
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "a cancelled grant carries the durable cancelled marker, not merely deactivated -- the journal must be able to say which it was");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "cancelling writes no remnant row -- there is no surviving period to remnant");
    }

    // The requester's extension of settled item 10 (2026-08-15): the same rule on the CLOSE branch.
    // A role that has run for a month is closed today (effective through yesterday); a grant added
    // this morning starts today and therefore can never take effect under it.
    [Fact]
    public async Task CloseRole_GrantStartingToday_CancelsGrantInsteadOfFailing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync(
            "AC-B1-2", "Vai trò đang chạy", new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync(
            "Ac.B1.Two", new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd));
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");

        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "a grant that starts today under a role that stops today can never take effect");
    }

    // GREEN REGRESSION GUARD, not a red-first test: today's engine already shrinks
    // this grant, so it passes before the implementation. It is here because "cancel when the shrink is
    // impossible" must not silently widen into "cancel whenever convenient" and destroy history.
    // Expect it green at Step 3 and keep it green.
    [Fact]
    public async Task CloseRole_GrantThatHasBeenInForce_IsShrunkNotCancelled()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-B1-3", "Vai trò đang chạy", monthAgo);
        var function = await CreateFunctionAsync("Ac.B1.Three", monthAgo);
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");

        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            0, "a grant with effective days behind it is history -- it is cut, never cancelled");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            2, "the shrink leaves the original deactivated plus exactly one remnant");
    }

    // CORRUPTION FIXTURE, and deliberately so (coordinator ruling, 2026-08-15, replacing
    // CloseRole_GrantInForceButUnshrinkable_StillFailsBaseVersionRequired -- that test could never
    // reach AutoCut: RoleRepository.ValidateClose pins Close to `today - 1` unconditionally, and on
    // that path `cutTo < from` coincides with `BranchFor == CancelPlan` for any LEGALLY-WRITTEN
    // dependent, so close-side BaseVersionRequired is unreachable for an Immediate entity's legal data
    // — but NOT unreachable outright, see CloseRole_GrantInCoverageGap_* below, which is the corrected
    // claim after review). "In force but unshrinkable"
    // needs a grant whose own start is not covered by what remains of the role -- i.e. a grant sitting
    // in one of the role's coverage gaps, which STRICT temporal-FK forbids and no supported writer can
    // create (this seeds it with raw SQL via CreateGrantAsync, the same way the F3 fixture always did).
    // It is kept because it is the ONLY discriminator left for the D1 boundary: on the Immediate CLOSE
    // path `cutTo` is always `today - 1`, so "the shrink is impossible" and "the dependent starts today
    // or later" coincide there and no close fixture can tell an implementation that consults BranchFor
    // from one that cancels every impossible shrink. Deleting the BranchFor conjunct must redden THIS
    // test (Step 8, proof iii).
    [Fact]
    public async Task CancelRole_GrantInForceInsideCoverageGap_StillFailsBaseVersionRequired()
    {
        SkipUnlessDbAvailable();

        // Earlier closed era + today's version on the SAME identity (reattached-code shape, same as
        // CancelRole_ReattachedIdentityWithEarlierEraAndGap_CancelsTodayGrant above): no adjacent
        // predecessor, because the earlier era ends Today-30, not Today-1.
        var role = await CreateRoleAsync(
            "AC-B1-7", "Vai trò tái khai báo", new EffectivePeriod(Today.AddDays(-90), Today.AddDays(-30)));
        await InsertRoleVersionAsync(
            role, "AC-B1-7", "Vai trò tái khai báo", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync("Ac.B1.Seven", new EffectivePeriod(Today.AddDays(-90), EffectivePeriod.OpenEnd));
        // Starts INSIDE the role's coverage gap [Today-30, Today) -- neither era covers its start, and
        // that start (Today-20) is strictly before today, so BranchFor says Retire, not CancelPlan.
        // The end is deliberately OPEN, not Today-10: with a past
        // end, the wrong predicate `dependentPeriod.To >= operationDate` ("it hasn't finished, so cancel
        // it") ALSO blocks here and the suite stayed green under it. An open end is in force TODAY, so
        // only a predicate that looks at the dependent's START -- which is what D1 means -- still blocks.
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today.AddDays(-20), EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);

        var cancel = await RoleRepo.CancelPlanAsync(
            role, target.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "hủy đợt tái khai báo");

        cancel.IsError.Should().BeTrue("the grant sits in a coverage gap the role's remaining era cannot reach, and it is not eligible for cancellation either");
        cancel.Errors.Should().Contain(e => e.Code == "VersionedRepository.BaseVersionRequired");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore, "the whole cancel rolls back");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore, "the grant is untouched -- neither cut nor cancelled");
    }

    // The CLOSE-route half of the same guard, restoring coverage the B1 rewrite lost: before this slice
    // CloseRole_ExclusivelyOwnedGrant_ParentCutRollsBackWhenDependentCutFails was the only test proving
    // the parent's version write rolls back when auto-cut refuses, and its scenario now legitimately
    // CANCELS instead. This is also the fixture that disproves "close-side BaseVersionRequired is
    // unreachable for an Immediate entity": `remaining` is a SET -- every OTHER
    // active version of the identity PLUS [target.From, newTo] -- so an identity holding an earlier era
    // can produce a first gap that is NOT the one at `today - 1`. Same corrupt seed as the cancel-route
    // guard above, and the same reason it is corrupt: STRICT temporal-FK forbids a grant in a parent's
    // coverage gap, so only raw-SQL seeding can build it.
    [Fact]
    public async Task CloseRole_GrantInCoverageGap_FailsBaseVersionRequired_AndRollsBackTheParentWrite()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync(
            "AC-B1-8", "Vai trò có kỳ cũ", new EffectivePeriod(Today.AddDays(-90), Today.AddDays(-40)));
        await InsertRoleVersionAsync(
            role, "AC-B1-8", "Vai trò có kỳ cũ", new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync("Ac.B1.Eight", new EffectivePeriod(Today.AddDays(-90), EffectivePeriod.OpenEnd));
        // Starts in the gap [Today-39, Today-31]: the first gap CoverageGap.TryFind reports is there,
        // NOT at today - 1, which is the whole point.
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today.AddDays(-35), EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var roleRowsBefore = await CountRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");

        close.IsError.Should().BeTrue("the grant's start sits in a gap no remaining era covers, and it has been in force since Today-35");
        close.Errors.Should().Contain(e => e.Code == "VersionedRepository.BaseVersionRequired");
        (await CountRowsAsync("role_version", "role_id", role)).Should().Be(
            roleRowsBefore, "the parent's own version write rolls back with the refused cut");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore, "and the dependent is untouched");
    }

    // ---------------------------------------------------------------------------------------
    // The two matrix cases review demanded (2026-08-15). Between them they discriminate the two
    // wrong implementations that pass every test above:
    //   - "cancel whenever the remaining coverage is EMPTY" passes the same-day fixtures and FAILS
    //     the reattached-era case below (remaining is non-empty there, yet the grant is uncovered);
    //   - "cancel every dependent that starts on/after today, before consulting CoverageGap" passes
    //     both and DESTROYS the predecessor-restore case below.
    // ---------------------------------------------------------------------------------------

    // B2 made this reachable: a role closed long ago and re-declared under the same code owns ONE
    // identity with an old era, a gap, and today's new version. Cancelling today's version leaves the
    // old era in `remaining` -- non-empty coverage that still does not reach a grant starting today.
    [Fact]
    public async Task CancelRole_ReattachedIdentityWithEarlierEraAndGap_CancelsTodayGrant()
    {
        SkipUnlessDbAvailable();

        // Seed the identity's earlier, already-closed era and then today's version, via the fixture
        // base's own second-version helper (InsertRoleVersionAsync -- AddRoleVersionAsync does not
        // exist, verified 2026-08-15). Passing the identity's own code and name so the second
        // version belongs to the same role.
        var role = await CreateRoleAsync(
            "AC-B1-5", "Vai trò tái khai báo", new EffectivePeriod(Today.AddDays(-90), Today.AddDays(-30)));
        await InsertRoleVersionAsync(
            role, "AC-B1-5", "Vai trò tái khai báo", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync("Ac.B1.Five", new EffectivePeriod(Today.AddDays(-90), EffectivePeriod.OpenEnd));
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var cancel = await RoleRepo.CancelPlanAsync(
            role, target.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "hủy đợt tái khai báo");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "the earlier era does not cover today, so the grant is still uncoverable and is cancelled");
    }

    // The mirror image: an adjacent predecessor is RESTORED through the cancelled version's end
    // (VersionedRepository.cs:581-591), so coverage never disappears and a same-day grant must be left
    // completely alone -- not cancelled, not cut.
    [Fact]
    public async Task CancelRole_AdjacentPredecessorRestored_LeavesSameDayGrantUntouched()
    {
        SkipUnlessDbAvailable();

        // An edit made today: the pre-edit version ends yesterday, today's version starts today.
        var role = await CreateRoleAsync(
            "AC-B1-6", "Vai trò vừa sửa hôm nay", new EffectivePeriod(Today.AddDays(-60), Today.AddDays(-1)));
        await InsertRoleVersionAsync(
            role, "AC-B1-6", "Vai trò vừa sửa hôm nay", new EffectivePeriod(Today, EffectivePeriod.OpenEnd));
        var function = await CreateFunctionAsync("Ac.B1.Six", new EffectivePeriod(Today.AddDays(-60), EffectivePeriod.OpenEnd));
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));
        var grantRowsBefore = await CountRowsAsync("role_permission_version", "role_permission_id", grant);

        var cancel = await RoleRepo.CancelPlanAsync(
            role, target.Value.Id, Today, adminFlagChangeAuthorized: false, "tester", "hủy lần sửa hôm nay");

        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));
        (await CountCancelledAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            0, "the predecessor restore keeps the role covering today, so the grant survives untouched");
        (await CountActiveAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            1, "and it is still active -- cancelling it here would destroy a grant the operator never touched");
        (await CountRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(
            grantRowsBefore, "no remnant either: nothing was cut");
    }

    // ---------------------------------------------------------------------------------------
    // Task 2 — auto-cut REPORTS what it did (Shrunk/Cancelled), and the remnant stops landing with a
    // NULL operation_kind (F11).
    // ---------------------------------------------------------------------------------------

    // F11: the auto-cut remnant used to land with operation_kind = NULL, so nothing downstream could
    // say why that row existed. It is a Close of the dependent, caused by the parent's own Close.
    [Fact]
    public async Task CloseRole_ShrunkGrant_RemnantCarriesCloseOperationKind()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-F11-1", "Vai trò đang chạy", monthAgo);
        var function = await CreateFunctionAsync("Ac.F11.One", monthAgo);
        var grant = await CreateGrantAsync(
            role, function, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        var kinds = await QueryOperationKindsAsync("role_permission_version", "role_permission_id", grant);
        kinds.Should().NotContainNulls("an auto-cut remnant with no operation_kind cannot explain itself (F11)");
        kinds.Should().Contain("Close", "the remnant records the dependent's own Close, caused by the parent's");
        kinds.Should().Contain("Add",
            "the SOURCE row keeps its own kind -- proving operation_kind is SET on the remnant, not copied " +
            "from the row it was cloned from (an 'always copy' implementation would show two Adds)");
    }

    // The report is what the journal is built from in Task 3 -- an empty report would silently produce
    // an empty journal, which looks exactly like "nothing happened".
    [Fact]
    public async Task CloseRole_ReportsOneOutcomePerAffectedGrant()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-F11-2", "Vai trò đang chạy", monthAgo);
        var shrunkFunction = await CreateFunctionAsync("Ac.F11.Shrunk", monthAgo);
        var cancelledFunction = await CreateFunctionAsync("Ac.F11.Cancelled", monthAgo);
        var shrunkGrant = await CreateGrantAsync(
            role, shrunkFunction, new EffectivePeriod(Today.AddDays(-10), EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var cancelledGrant = await CreateGrantAsync(
            role, cancelledFunction, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");
        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));

        close.Value.AutoCutOutcomes.Should().HaveCount(2);

        // Assert every field, not just the pair (identity, action): the journal renders these values,
        // so an outcome carrying the wrong period is a wrong journal line, and a count-only assertion
        // would never see it.
        var shrunk = close.Value.AutoCutOutcomes.Should().ContainSingle(
            o => o.DependentIdentityId == shrunkGrant).Subject;
        shrunk.Action.Should().Be(AutoCutAction.Shrunk, "it had effective days behind it");
        shrunk.DependentVersionTable.Should().Be("role_permission_version");
        shrunk.EffectiveFrom.Should().Be(Today.AddDays(-10));
        shrunk.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
        shrunk.CutTo.Should().Be(Today.AddDays(-1), "the cut lands where the role's remaining coverage ends");

        var cancelled = close.Value.AutoCutOutcomes.Should().ContainSingle(
            o => o.DependentIdentityId == cancelledGrant).Subject;
        cancelled.Action.Should().Be(AutoCutAction.Cancelled, "it never took effect");
        cancelled.EffectiveFrom.Should().Be(Today);
        cancelled.CutTo.Should().BeNull("a cancelled dependent has no surviving period, so there is no cut date");
    }

    // A write that touched no dependent must report an empty list, never a null -- Task 3 iterates it
    // unconditionally.
    [Fact]
    public async Task CloseRole_NoGrants_ReportsEmptyOutcomes()
    {
        SkipUnlessDbAvailable();

        var monthAgo = new EffectivePeriod(Today.AddDays(-30), EffectivePeriod.OpenEnd);
        var role = await CreateRoleAsync("AC-F11-3", "Vai trò không quyền", monthAgo);

        var target = await Roles.GetByIdentityAsync(role, Today);
        target.IsError.Should().BeFalse(DescribeErrors(target.Errors));

        var close = await RoleRepo.CloseVersionAsync(
            role, target.Value.Id, Today.AddDays(-1), new OperationDate(Today), "tester", "đóng vai trò");

        close.IsError.Should().BeFalse(DescribeErrors(close.Errors));
        close.Value.AutoCutOutcomes.Should().BeEmpty();
    }

    private async Task<IReadOnlyList<string?>> QueryOperationKindsAsync(
        string table, string identityColumn, long identityId)
    {
        // mirrors CountRowsAsync's connection/parameter style -- read it and match it exactly
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return (await connection.QueryAsync<string?>(
            $"SELECT operation_kind FROM {table} WHERE {identityColumn} = @identityId",
            new { identityId })).ToList();
    }

    // ---------------------------------------------------------------------------------------

    // Class + init properties, NOT a positional record: `id` is BIGINT UNSIGNED, which MySqlConnector
    // surfaces as ulong. Dapper's property-setter path converts it to long; its constructor-matching path
    // (DefaultTypeMap.FindConstructor) requires an exact reader-type match and would reject `long Id`.
    // Same reason OrgUnitRepositoryTests.RawVersionRow is a class.
    private sealed class GrantVersionRow
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly EffectiveTo { get; init; }
        public byte ScopeLevel { get; init; }
        public string RecordedBy { get; init; } = string.Empty;
        public string? Reason { get; init; }
    }

    private async Task<IReadOnlyList<GrantVersionRow>> ReadGrantVersionsAsync(long rolePermissionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<GrantVersionRow>(
            """
            SELECT id AS Id, isactive AS IsActive, effective_from AS EffectiveFrom, effective_to AS EffectiveTo,
                   scope_level AS ScopeLevel, recorded_by AS RecordedBy, reason AS Reason
            FROM role_permission_version
            WHERE role_permission_id = @rolePermissionId
            """,
            new { rolePermissionId });
        return rows.ToList();
    }

    private async Task<long> CountRowsAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {versionTable} WHERE {identityColumn} = @identityId",
            new { identityId });
    }

    // B1: same connection/parameter style as CountRowsAsync above, extended with a WHERE fragment so
    // CountActiveAsync/CountCancelledAsync below don't each open a second data-access style.
    private async Task<int> CountWhereAsync(string table, string identityColumn, long identityId, string whereFragment)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {table} WHERE {identityColumn} = @identityId AND {whereFragment}",
            new { identityId });
    }

    private Task<int> CountActiveAsync(string table, string identityColumn, long identityId) =>
        CountWhereAsync(table, identityColumn, identityId, "isactive = 1");

    private Task<int> CountCancelledAsync(string table, string identityColumn, long identityId) =>
        CountWhereAsync(table, identityColumn, identityId, "status = 'cancelled'");
}
