using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// B2 — STRICT temporal-FK (D8: role_permission_version ⊂ (role_version, function_version)).
// Brief 059 — Revoke (= Close) wrappers on RolePermissionRepository.
public sealed class RolePermissionRepositoryTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod Year2025 = new(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DateOnly EffectiveThrough = Today.AddDays(-1);

    private RolePermissionRepository RolePermissionRepo => (RolePermissionRepository)RolePermissions;

    private IDbConnectionFactory NewConnectionFactory() =>
        new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));

    [Fact]
    public async Task UpsertAsync_PeriodWithinRoleAndFunctionCoverage_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("RP-ROLE", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Iam.User.View", OpenFrom2020);
        var rpId = await CreateRolePermissionHeaderAsync();

        var result = await RolePermissions.UpsertAsync(
            rpId, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.OwnOrgUnit, VersionOperationKind.Edit, new OperationDate(Today), "tester", "grant");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    [Fact]
    public async Task UpsertAsync_PeriodExceedsRoleCoverage_ReturnsTemporalFkError()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("RP-ROLE2", "Vai trò hẹp", Year2025);
        var function = await CreateFunctionAsync("Iam.User.Edit", OpenFrom2020);
        var rpId = await CreateRolePermissionHeaderAsync();

        var result = await RolePermissions.UpsertAsync(
            rpId, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global, VersionOperationKind.Edit, new OperationDate(Today), "tester", "grant");

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");
    }

    [Fact]
    public async Task RevokeAsync_ActiveGrant_TruncatesAtEffectiveThrough()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("RP-REV", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Iam.Revoke.One", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var versionId = (await RolePermissions.GetByIdentityAsync(grant, Today)).Value.Id;
        var result = await RolePermissionRepo.RevokeAsync(
            grant, versionId, EffectiveThrough, new OperationDate(Today), "revoker", "revoke grant");

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var remnant = await RolePermissions.GetByIdentityAsync(grant, EffectiveThrough);
        remnant.IsError.Should().BeFalse(DescribeErrors(remnant.Errors));
        remnant.Value.EffectiveFrom.Should().Be(OpenFrom2020.From);
        remnant.Value.EffectiveTo.Should().Be(EffectiveThrough);

        (await RolePermissions.GetByIdentityAsync(grant, Today)).IsError.Should().BeTrue(
            "coverage from today must be gone (ceased from today)");
    }

    [Fact]
    public async Task RevokeAsync_Composite_TruncatesAtEffectiveThrough()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("RP-REV-C", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Iam.Revoke.Two", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var versionId = (await RolePermissions.GetByIdentityAsync(grant, Today)).Value.Id;
        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(RolePermissionRepo, grant);

        var result = await composite.ExecuteAsync(async context =>
        {
            var revoke = await RolePermissionRepo.RevokeAsync(
                context, grant, versionId, EffectiveThrough, new OperationDate(Today), "revoker", "revoke composite");
            return revoke.IsError ? revoke.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var remnant = await RolePermissions.GetByIdentityAsync(grant, EffectiveThrough);
        remnant.IsError.Should().BeFalse(DescribeErrors(remnant.Errors));
        remnant.Value.EffectiveTo.Should().Be(EffectiveThrough);
        (await RolePermissions.GetByIdentityAsync(grant, Today)).IsError.Should().BeTrue();
    }

    // =========================================================================================
    // 2026-08-12 — role_permission is Immediate: R2 (effective_to must stay
    // open, 9999-12-31) + R3 (a revoke date other than today - 1 is rejected). Forward-dated-start
    // (R1) for both UpsertAsync overloads is covered by RoleRepositoryTests'
    // No_role_or_grant_writer_can_persist_a_forward_dated_start Theory (GrantPlain/GrantComposite
    // cases) — not duplicated here.
    // =========================================================================================

    [Fact]
    public async Task UpsertAsync_Plain_BoundedEnd_ReturnsEffectiveToMustBeOpen_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-BE1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Be1", OpenFrom2020);
        var grantId = await CreateRolePermissionHeaderAsync();
        var boundedEnd = new EffectivePeriod(Today, Today.AddDays(30));

        var result = await RolePermissions.UpsertAsync(
            grantId, boundedEnd, role, function, ScopeLevel.Global, VersionOperationKind.Add, new OperationDate(Today), "tester", "seed");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.EffectiveToMustBeOpen");
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        var rows = await RolePermissionRepo.GetHistoryAsync(grantId);
        rows.Should().BeEmpty("a rejected bounded-end grant must write no version row");
    }

    [Fact]
    public async Task UpsertAsync_Composite_BoundedEnd_ReturnsEffectiveToMustBeOpen_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-BE2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Be2", OpenFrom2020);
        var grantId = await CreateRolePermissionHeaderAsync();
        var boundedEnd = new EffectivePeriod(Today, Today.AddDays(30));
        var roleRepo = (RoleRepository)Roles;
        var functionRepo = (FunctionRepository)Functions;
        // role_permission_version has 2 temporal-FK PARENTS (role_id, function_id) — both must be
        // Enlisted up front (CompositeWrite.NotEnlisted otherwise), same requirement
        // RoleDeclarationService documents on its own composite.
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grantId)
            .Enlist(roleRepo, role)
            .Enlist(functionRepo, function);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.UpsertAsync(
                context, grantId, boundedEnd, role, function, ScopeLevel.Global, VersionOperationKind.Add, new OperationDate(Today),
                "tester", "seed");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("RolePermission.EffectiveToMustBeOpen");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);

        var rows = await RolePermissionRepo.GetHistoryAsync(grantId);
        rows.Should().BeEmpty("a rejected bounded-end grant must write no version row");
    }

    // =========================================================================================
    // docs/design-iam-schema.md §1.5 (Model 2): a GRANT identity carries ONE version for life —
    // a scope change is revoke-old + grant-new, never a second version on the same identity.
    // A grant has nothing editable in place. Both writers must reject an Immediate-illegal period.
    // =========================================================================================

    [Fact]
    public async Task UpsertAsync_Plain_ExistingGrantSameStartDifferentScope_ReturnsEffectiveFromMustBeToday_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-SC1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Sc1", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);
        var rowsBefore = (await RolePermissionRepo.GetHistoryAsync(grant)).Count;

        // Same identity, same (past) start, DIFFERENT scope — an in-place scope change.
        var result = await RolePermissions.UpsertAsync(
            grant, OpenFrom2020, role, function, ScopeLevel.OwnOrgUnit, VersionOperationKind.Edit, new OperationDate(Today), "tester", "scope change");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.EffectiveFromMustBeToday");
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(
            rowsBefore, "a rejected in-place scope change must write no version row");
        var unchanged = await RolePermissions.GetByIdentityAsync(grant, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.ScopeLevel.Should().Be(ScopeLevel.Global, "the live grant's scope must be untouched");
    }

    [Fact]
    public async Task UpsertAsync_Composite_ExistingGrantSameStartDifferentScope_ReturnsEffectiveFromMustBeToday_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-SC2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Sc2", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);
        var rowsBefore = (await RolePermissionRepo.GetHistoryAsync(grant)).Count;
        var roleRepo = (RoleRepository)Roles;
        var functionRepo = (FunctionRepository)Functions;
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grant)
            .Enlist(roleRepo, role)
            .Enlist(functionRepo, function);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.UpsertAsync(
                context, grant, OpenFrom2020, role, function, ScopeLevel.OwnOrgUnit, VersionOperationKind.Edit, new OperationDate(Today),
                "tester", "scope change");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("RolePermission.EffectiveFromMustBeToday");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);

        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(
            rowsBefore, "a rejected in-place scope change must write no version row");
        var unchanged = await RolePermissions.GetByIdentityAsync(grant, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.ScopeLevel.Should().Be(ScopeLevel.Global, "the live grant's scope must be untouched");
    }

    // =========================================================================================
    // §1.5 identity half — `RolePermissionRepository.ValidateUpsertAsync` (the base's
    // `ValidateUpsertAsync` hook, run at the top of ApplyUpsertPlanAsync while the identity lock is
    // held, on BOTH writers). An identity that already has ANY version row may never be upserted
    // again. The probe is existence-ANY on purpose, and the two fixtures below fail DIFFERENT wrong
    // filters: `Revoked` has no version effective today (an as-of read calls it empty, though its
    // remnant is still `isactive = 1`), while `CancelledOnly` has no active row at all (an
    // `isactive = 1` read calls it empty). Together they pin both mistakes.
    // =========================================================================================

    public enum GrantHistory { Active, Revoked, CancelledOnly }

    private async Task<long> SeedGrantWithHistoryAsync(long role, long function, GrantHistory shape)
    {
        if (shape == GrantHistory.Active)
        {
            return await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);
        }

        if (shape == GrantHistory.Revoked)
        {
            var revoked = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);
            var versionId = (await RolePermissions.GetByIdentityAsync(revoked, Today)).Value.Id;
            var revoke = await RolePermissionRepo.RevokeAsync(
                revoked, versionId, EffectiveThrough, new OperationDate(Today), "revoker", "revoke");
            revoke.IsError.Should().BeFalse(DescribeErrors(revoke.Errors));
            return revoked;
        }

        var cancelled = await CreateGrantAsync(role, function, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), ScopeLevel.Global);
        var planId = (await RolePermissions.GetByIdentityAsync(cancelled, Today)).Value.Id;
        var cancel = await RolePermissionRepo.CancelPlanAsync(cancelled, planId, Today, "canceller", "cancel");
        cancel.IsError.Should().BeFalse(DescribeErrors(cancel.Errors));
        return cancelled;
    }

    [Theory]
    [InlineData(GrantHistory.Active)]
    [InlineData(GrantHistory.Revoked)]
    [InlineData(GrantHistory.CancelledOnly)]
    public async Task UpsertAsync_Plain_IdentityWithHistory_ReturnsIdentityAlreadyVersioned_WritesNothing(GrantHistory shape)
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync($"IMM-RP-ID1{(int)shape}", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync($"Imm.Rp.Id1.{(int)shape}", OpenFrom2020);
        var grant = await SeedGrantWithHistoryAsync(role, function, shape);
        var rowsBefore = (await RolePermissionRepo.GetHistoryAsync(grant)).Count;

        // A start ON the operation date — so the period guard passes and the identity hook is what rejects.
        var result = await RolePermissions.UpsertAsync(
            grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Self,
            VersionOperationKind.Edit, new OperationDate(Today), "tester", "re-grant in place");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.IdentityAlreadyVersioned");
        result.FirstError.Type.Should().Be(ErrorType.Validation);
        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(
            rowsBefore, "a rejected re-grant must write no version row");
    }

    [Theory]
    [InlineData(GrantHistory.Active)]
    [InlineData(GrantHistory.Revoked)]
    [InlineData(GrantHistory.CancelledOnly)]
    public async Task UpsertAsync_Composite_IdentityWithHistory_ReturnsIdentityAlreadyVersioned_WritesNothing(GrantHistory shape)
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync($"IMM-RP-ID2{(int)shape}", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync($"Imm.Rp.Id2.{(int)shape}", OpenFrom2020);
        var grant = await SeedGrantWithHistoryAsync(role, function, shape);
        var rowsBefore = (await RolePermissionRepo.GetHistoryAsync(grant)).Count;
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grant)
            .Enlist((RoleRepository)Roles, role)
            .Enlist((FunctionRepository)Functions, function);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Self,
                VersionOperationKind.Edit, new OperationDate(Today), "tester", "re-grant in place");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("RolePermission.IdentityAlreadyVersioned");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);
        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(
            rowsBefore, "a rejected re-grant must write no version row");
    }

    // The positive branch, so a future tightening of the hook cannot silently block the add path.
    [Fact]
    public async Task UpsertAsync_Composite_FreshIdentity_Succeeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-ID3", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Id3", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grant)
            .Enlist((RoleRepository)Roles, role)
            .Enlist((FunctionRepository)Functions, function);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "grant");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeFalse(DescribeErrors(compositeResult.Errors));
        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(1);
    }

    // The gate is only as good as WHERE it runs. Both writers race for one FRESH identity: the loser
    // must be rejected by the identity hook, not admitted — which is only true because the hook reads
    // under the identity's named lock, inside the same transaction that writes.
    [Fact]
    public async Task UpsertAsync_PlainAndCompositeRaceOnOneFreshIdentity_ExactlyOneSucceeds()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-ID4", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Id4", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();
        var period = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grant)
            .Enlist((RoleRepository)Roles, role)
            .Enlist((FunctionRepository)Functions, function);

        var compositeTask = Task.Run(async () => await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.UpsertAsync(
                context, grant, period, role, function, ScopeLevel.OwnOrgUnit, VersionOperationKind.Add,
                new OperationDate(Today), "tester", "composite");
            return write.IsError ? write.Errors : Result.Success;
        }));
        var plainTask = RolePermissions.UpsertAsync(
            grant, period, role, function, ScopeLevel.Self, VersionOperationKind.Add, new OperationDate(Today),
            "tester", "plain");

        var compositeResult = await compositeTask;
        var plainResult = await plainTask;

        new[] { compositeResult.IsError, plainResult.IsError }.Should().ContainSingle(
            e => !e, "exactly one of the two writers may create the identity's only version");
        var loser = compositeResult.IsError ? compositeResult.Errors : plainResult.Errors;
        loser.Should().Contain(e => e.Code == "RolePermission.IdentityAlreadyVersioned");
        (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(1);
    }

    // Discriminates lock placement of the identity probe: an unlocked pre-check would run at step 2
    // (before the harness row exists), wait for the lock, then write a second version. Green here
    // means the probe ran only after GET_LOCK was granted. Readiness is observed (PROCESSLIST
    // STATE='User lock' AND INFO contains GET_LOCK('<this key>', ...)), not a sleep.
    [Fact]
    public async Task UpsertAsync_PlainWriterBlockedOnIdentityLock_SeesHarnessRowInsertedWhileWaiting()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-RP-ID6", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rp.Id6", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();
        var period = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);
        var grantLockKey = LockKey("role_permission_version", grant);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var blocker = await HoldLockAsync(grantLockKey);
        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(cancellationToken);

        Task<ErrorOr<UpsertResult>>? writerTask = null;
        var released = false;
        try
        {
            // Task.Run so the plain writer's blocking GET_LOCK wait runs off the test thread while the
            // harness below still holds the grant identity lock. Observer is already open so handshake
            // does not eat the writer's 10s GET_LOCK budget.
            writerTask = Task.Run(async () => await RolePermissions.UpsertAsync(
                grant, period, role, function, ScopeLevel.Self, VersionOperationKind.Add, new OperationDate(Today),
                "tester", "plain-under-lock"), cancellationToken);

            await WaitUntilUserLockWaiterAsync(observer, grantLockKey, cancellationToken);

            writerTask.IsCompleted.Should().BeFalse(
                "the plain writer must still be waiting inside ExecuteWriteAsync's GET_LOCK, not have skipped past it");

            // WHILE blocked: insert a version row the identity must not gain a sibling of (CreateGrantAsync style).
            // Immediate grant: the running system can only create a version starting Today.
            await observer.ExecuteAsync(
                """
                INSERT INTO role_permission_version
                    (role_permission_id, role_id, function_id, scope_level, operation_kind, effective_from,
                     effective_to, isactive, recorded_by, reason)
                VALUES
                    (@id, @roleId, @functionId, @scopeLevel, 'Add', @from, @to, 1, 'tester', @reason)
                """,
                new
                {
                    id = grant,
                    roleId = role,
                    functionId = function,
                    scopeLevel = (int)ScopeLevel.Global,
                    from = Today,
                    to = EffectivePeriod.OpenEnd,
                    reason = "harness-while-blocked",
                });

            await ReleaseLockAsync(blocker, grantLockKey);
            released = true;

            var plainResult = await writerTask;
            plainResult.IsError.Should().BeTrue();
            plainResult.FirstError.Code.Should().Be("RolePermission.IdentityAlreadyVersioned");
            (await RolePermissionRepo.GetHistoryAsync(grant)).Count.Should().Be(1,
                "only the harness row may exist — an unlocked probe would have admitted a second version");
        }
        finally
        {
            if (!released)
            {
                await ReleaseLockAsync(blocker, grantLockKey);
            }

            if (writerTask is not null)
            {
                try
                {
                    await writerTask.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // Precedence: the identity hook runs BEFORE the period algebra and the temporal-FK check, so an
    // identity that may not be written at all says so in its own terms instead of surfacing an
    // engine-level TemporalFk.ParentGap the caller cannot act on.
    [Fact]
    public async Task UpsertAsync_IdentityWithHistoryAndUncoveredParent_ReturnsIdentityErrorNotTemporalFk()
    {
        SkipUnlessDbAvailable();

        // The role covers 2025 only — the requested [Today, open) period has no parent coverage at all.
        var role = await CreateRoleAsync("IMM-RP-ID5", "Vai trò hẹp", Year2025);
        var function = await CreateFunctionAsync("Imm.Rp.Id5", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, Year2025, ScopeLevel.Global);

        var result = await RolePermissions.UpsertAsync(
            grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Self,
            VersionOperationKind.Edit, new OperationDate(Today), "tester", "re-grant in place");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.IdentityAlreadyVersioned");
        result.Errors.Should().NotContain(e => e.Code == "TemporalFk.ParentGap");
    }

    // Stop path (R3): RevokeAsync delegates to the base close, which now runs
    // RolePermissionRepository.ValidateClose before writing anything — so the ONLY end a revoke may
    // record is `operationDate - 1`. Any other date, past or future, is rejected here. Note that
    // RevokeAsync_ActiveGrant_TruncatesAtEffectiveThrough above no longer pins a second, freer
    // behaviour: it passes precisely because its effectiveThrough IS `today - 1`.
    [Fact]
    public async Task RevokeAsync_EndOtherThanTodayMinusOne_ReturnsRevokeDateMustBeImmediate_TargetUnchanged()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-REV1", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rev1.Fn", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var versionId = (await RolePermissions.GetByIdentityAsync(grant, Today)).Value.Id;
        var result = await RolePermissionRepo.RevokeAsync(grant, versionId, Today.AddDays(5), new OperationDate(Today), "tester", "revoke");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("RolePermission.RevokeDateMustBeImmediate");
        result.FirstError.Type.Should().Be(ErrorType.Validation);

        var unchanged = await RolePermissions.GetByIdentityAsync(grant, Today);
        unchanged.IsError.Should().BeFalse(DescribeErrors(unchanged.Errors));
        unchanged.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a rejected revoke must not touch effective_to");
    }

    [Fact]
    public async Task RevokeAsync_Composite_EndOtherThanTodayMinusOne_ReturnsRevokeDateMustBeImmediate_TargetUnchanged()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("IMM-REV2", "Vai trò", OpenFrom2020);
        var function = await CreateFunctionAsync("Imm.Rev2.Fn", OpenFrom2020);
        var grant = await CreateGrantAsync(role, function, OpenFrom2020, ScopeLevel.Global);

        var versionId = (await RolePermissions.GetByIdentityAsync(grant, Today)).Value.Id;
        var roleRepo = (RoleRepository)Roles;
        var functionRepo = (FunctionRepository)Functions;
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RolePermissionRepo, grant)
            .Enlist(roleRepo, role)
            .Enlist(functionRepo, function);

        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            var write = await RolePermissionRepo.RevokeAsync(
                context, grant, versionId, Today.AddDays(5), new OperationDate(Today), "tester", "revoke");
            return write.IsError ? write.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeTrue();
        compositeResult.FirstError.Code.Should().Be("RolePermission.RevokeDateMustBeImmediate");
        compositeResult.FirstError.Type.Should().Be(ErrorType.Validation);

        var unchangedAfter = await RolePermissions.GetByIdentityAsync(grant, Today);
        unchangedAfter.IsError.Should().BeFalse(DescribeErrors(unchangedAfter.Errors));
        unchangedAfter.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd, "a rejected revoke must not touch effective_to");
    }
}
