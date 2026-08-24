using System.Data;
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

// Pins the composite-write seam (§16.1 capability 1, spec §16.2 acceptance items 2 and 3 —
// the IAM declaration-screens business analysis): ONE connection,
// every §7 lock key acquired up front in the fixed order, ONE READ COMMITTED transaction, and an
// all-or-nothing rollback whether the failure is a returned Error or a thrown exception.
//
// A write inside the composite must also see the composite's OWN uncommitted rows — that is what
// CompositeWrite_ChildCoveredOnlyByParentWrittenEarlierInSameTransaction_Commits pins, and it is why
// ITemporalFkValidator takes a required ambientTransaction.
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class CompositeWriteTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod TodayOpen = new(Today, EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod Year2021 = new(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31));

    private RoleRepository RoleRepo => (RoleRepository)Roles;
    private RolePermissionRepository GrantRepo => (RolePermissionRepository)RolePermissions;
    private FunctionRepository FunctionRepo => (FunctionRepository)Functions;

    private IDbConnectionFactory NewConnectionFactory() =>
        new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));

    // ---------------------------------------------------------------------------------------
    // Item 2 — a failure on the 2nd write must roll the WHOLE composite back (no partial state).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositeWrite_SecondWriteReturnsError_FirstWriteIsRolledBack()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CW-R1", "Vai trò gốc", OpenFrom2020);
        // Function coverage is deliberately NARROWER than the grant period written below, so the SECOND
        // write fails for a REAL business reason (STRICT temporal-FK, D8) rather than a synthetic one.
        var function = await CreateFunctionAsync("Cw.Fn.One", Year2021);
        var grant = await CreateRolePermissionHeaderAsync();

        var roleRowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountVersionRowsAsync("role_permission_version", "role_permission_id", grant);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(GrantRepo, grant)
            .Enlist(FunctionRepo, function);

        var firstWrite = default(ErrorOr<UpsertResult>);
        var result = await composite.ExecuteAsync(async context =>
        {
            firstWrite = await RoleRepo.UpsertAsync(
                context, role, TodayOpen, "CW-R1", "Vai trò đã đổi tên", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            if (firstWrite.IsError)
            {
                return firstWrite.Errors;
            }

            var secondWrite = await GrantRepo.UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return secondWrite.IsError ? secondWrite.Errors : Result.Success;
        });

        // The FIRST write must genuinely have succeeded inside the transaction — otherwise this test would
        // pass for the wrong reason (nothing to roll back).
        firstWrite.IsError.Should().BeFalse(DescribeErrors(firstWrite.Errors));

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");

        // Nothing persisted: neither the role edit (soft-deactivate + remnant + new row) nor the grant.
        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        (await CountVersionRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(grantRowsBefore);

        var stillOriginal = await Roles.GetByIdentityAsync(role, Today);
        stillOriginal.IsError.Should().BeFalse(DescribeErrors(stillOriginal.Errors));
        stillOriginal.Value.RoleName.Should().Be("Vai trò gốc");
        stillOriginal.Value.EffectiveTo.Should().Be(EffectivePeriod.OpenEnd);
    }

    [Fact]
    public async Task CompositeWrite_SecondWriteThrows_FirstWriteIsRolledBack()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CW-R2", "Vai trò gốc", OpenFrom2020);
        var function = await CreateFunctionAsync("Cw.Fn.Two", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();

        var roleRowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(GrantRepo, grant)
            .Enlist(FunctionRepo, function);

        // An EXCEPTION mid-composite (an infra failure, not a validation error) must roll back too — the
        // transaction may not be left committed or open.
        var act = async () => await composite.ExecuteAsync(async context =>
        {
            var first = await RoleRepo.UpsertAsync(
                context, role, TodayOpen, "CW-R2", "Vai trò đã đổi tên", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            first.IsError.Should().BeFalse(DescribeErrors(first.Errors));

            throw new InvalidOperationException("injected failure on the 2nd write");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*injected failure*");

        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        var stillOriginal = await Roles.GetByIdentityAsync(role, Today);
        stillOriginal.Value.RoleName.Should().Be("Vai trò gốc");
    }

    // ---------------------------------------------------------------------------------------
    // Ambient-transaction defect (docs/shared-components.md ITemporalFkValidator row): the coverage
    // providers used to open their OWN connection, so a parent version written EARLIER in the SAME
    // composite transaction was invisible (uncommitted) to the child's ValidateChildCoverage check —
    // a false TemporalFk.ParentGap that rolled back a legitimate composite write.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositeWrite_ChildCoveredOnlyByParentWrittenEarlierInSameTransaction_Commits()
    {
        SkipUnlessDbAvailable();

        // Role initially covers ONLY 2021 — committed state has NO coverage for 2025 onward.
        var role = await CreateRoleAsync("CW-R5", "Vai trò gốc", Year2021);
        // Function has full open-ended coverage from 2020 — never the blocking factor here.
        var function = await CreateFunctionAsync("Cw.Fn.Five", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(GrantRepo, grant)
            .Enlist(FunctionRepo, function);

        var result = await composite.ExecuteAsync(async context =>
        {
            // Adds a DISJOINT new role version [2025-01-01, open) alongside the existing 2021 version —
            // still UNCOMMITTED at this point in the transaction.
            var roleWrite = await RoleRepo.UpsertAsync(
                context, role, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "CW-R5", "Vai trò gốc", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            if (roleWrite.IsError)
            {
                return roleWrite.Errors;
            }

            // The grant's period [2025-01-01, open) is covered ONLY by the role version just written
            // above, in this same transaction — the pre-existing committed role coverage (2021 only)
            // does NOT cover it. Must see the uncommitted role write to pass.
            var grantWrite = await GrantRepo.UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return grantWrite.IsError ? grantWrite.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var grantDto = await RolePermissions.GetByIdentityAsync(grant, Today);
        grantDto.IsError.Should().BeFalse(DescribeErrors(grantDto.Errors));
        grantDto.Value.RoleId.Should().Be(role);
    }

    // ---------------------------------------------------------------------------------------
    // Item 3 — every lock key acquired UP FRONT, in the §7 fixed order, BEFORE any write.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositeWrite_AcquiresAllLockKeysUpFrontInFixedOrder_BeforeAnyWrite()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CW-R3", "Vai trò gốc", OpenFrom2020);
        var function = await CreateFunctionAsync("Cw.Fn.Three", OpenFrom2020);
        var grant = await CreateRolePermissionHeaderAsync();

        // §7 fixed order = ordinal by version-table name, then identity id. Ordinally:
        //   "function_version" < "role_permission_version" < "role_version"
        // so the composite must take the FUNCTION key first and the ROLE key last.
        var firstKey = LockKey("function_version", function);
        var lastKey = LockKey("role_version", role);

        var roleRowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);
        var grantRowsBefore = await CountVersionRowsAsync("role_permission_version", "role_permission_id", grant);

        await using var blocker = await HoldLockAsync(firstKey);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(GrantRepo, grant)
            .Enlist(FunctionRepo, function);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(cancellationToken);

        // Task.Run so the composite's blocking GET_LOCK wait runs off the test thread while the blocker
        // below still holds firstKey — otherwise the test thread parks and never reaches the assertions.
        var compositeTask = Task.Run(async () => await composite.ExecuteAsync(async context =>
        {
            var first = await RoleRepo.UpsertAsync(
                context, role, TodayOpen, "CW-R3", "Vai trò đã đổi tên", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            if (first.IsError)
            {
                return first.Errors;
            }

            var second = await GrantRepo.UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, function, ScopeLevel.Global, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return second.IsError ? second.Errors : Result.Success;
        }));

        await WaitUntilUserLockWaiterAsync(observer, firstKey, cancellationToken);

        compositeTask.IsCompleted.Should().BeFalse(
            "the composite must still be waiting on the first lock key, not have skipped past it");

        // THE DISCRIMINATOR: while blocked on the FIRST key, the composite must not already hold the LAST
        // key. If it acquired keys out of order (or lazily, per write), this probe would fail — and that is
        // exactly the deadlock surface §7 exists to prevent.
        (await CanAcquireAsync(lastKey)).Should().BeTrue(
            $"the composite must not hold '{lastKey}' while it is still waiting for '{firstKey}' (§7 fixed order)");

        // And no write may have happened yet — locks come UP FRONT, before any write.
        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(roleRowsBefore);
        (await CountVersionRowsAsync("role_permission_version", "role_permission_id", grant)).Should().Be(grantRowsBefore);

        await ReleaseLockAsync(blocker, firstKey);

        var result = await compositeTask;
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountVersionRowsAsync("role_permission_version", "role_permission_id", grant))
            .Should().BeGreaterThan(grantRowsBefore);
    }

    [Fact]
    public async Task CompositeWrite_ConcurrentWithSingleIdentityWrites_NoLockTimeoutForAnyone()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("CW-R4", "Vai trò gốc", OpenFrom2020);
        // Distinct functions so the two grants do not share a (role, function) natural key — §1.5
        // forbids two active grants for the same pair; the overlap gate is service-only, so a shared
        // function would leave a domain-invalid fixture in ast_test.
        var functionComposite = await CreateFunctionAsync("Cw.Fn.Four", OpenFrom2020);
        var functionSingle = await CreateFunctionAsync("Cw.Fn.Four.Single", OpenFrom2020);
        // Two fresh grant identities: each concurrent writer owns one. Do not share one identity at
        // Today — that leaned on the identity-blind From==today hole (second version on one id).
        var grantComposite = await CreateRolePermissionHeaderAsync();
        var grantSingle = await CreateRolePermissionHeaderAsync();
        var grantPeriod = new EffectivePeriod(Today, EffectivePeriod.OpenEnd);

        // Both grants AND both functions Enlist-ed so the composite's up-front §7 key set contains
        // the single grant writer's full BuildLockKeys set (grantSingle + role + functionSingle).
        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(GrantRepo, grantComposite)
            .Enlist(GrantRepo, grantSingle)
            .Enlist(FunctionRepo, functionComposite)
            .Enlist(FunctionRepo, functionSingle);

        var compositeTask = Task.Run(async () => await composite.ExecuteAsync(async context =>
        {
            var first = await RoleRepo.UpsertAsync(
                context, role, TodayOpen, "CW-R4", "Vai trò composite", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            if (first.IsError)
            {
                return first.Errors;
            }

            var second = await GrantRepo.UpsertAsync(
                context, grantComposite, grantPeriod, role, functionComposite, ScopeLevel.OwnOrgUnit, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return second.IsError ? second.Errors : Result.Success;
        }));

        // One concurrent SINGLE-identity grant write touching overlapping keys from the other direction.
        // The plain role UpsertAsync path is gone — this test no longer discriminates composite-versus-
        // single-identity *role* writers. It still discriminates composite-versus-plain *grant* writers
        // on the overlapping grant+role+function lock set, which is the remaining single-identity half.
        // Because both paths sort their keys identically (§7), nobody may hit VersionedRepository.LockTimeout.
        // Fixed-order discrimination lives in CompositeWrite_AcquiresAllLockKeysUpFrontInFixedOrder_BeforeAnyWrite
        // (hold firstKey, probe lastKey, assert zero rows) — untouched by this round.
        var singleGrantWrite = RolePermissions.UpsertAsync(
            grantSingle, grantPeriod, role, functionSingle, ScopeLevel.Self, VersionOperationKind.Edit, new OperationDate(Today), "tester-single", "single");

        var singleResult = await singleGrantWrite;
        var compositeResult = await compositeTask;

        compositeResult.IsError.Should().BeFalse(DescribeErrors(compositeResult.Errors));
        singleResult.IsError.Should().BeFalse(DescribeErrors(singleResult.Errors));
        singleResult.Errors.Should().NotContain(e => e.Code == "VersionedRepository.LockTimeout");
    }

    // ---------------------------------------------------------------------------------------
    // §7 (amended 2026-08-16) — an identity minted INSIDE the transaction commits with its first
    // version or not at all, and its created-here mark covers nothing but itself.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositeWrite_MintsInsideTransactionThenThrows_LeavesNoHeaderAndNoVersion()
    {
        SkipUnlessDbAvailable();

        // THE DISCRIMINATOR: the pre-existing rollback tests all start from headers seeded BEFORE the
        // composite, so they stay green whether the mint happens inside the transaction or on a second
        // connection. This one mints through the seam and counts HEADER rows — under the old pre-mint
        // design both headers survive this rollback (that was the orphan the compensation step chased).
        var function = await CreateFunctionAsync("Cw.Fn.Mint", OpenFrom2020);

        long mintedRoleId = 0;
        long mintedGrantId = 0;

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(FunctionRepo, function);

        var act = async () => await composite.ExecuteAsync(async context =>
        {
            mintedRoleId = await RoleRepo.CreateIdentityAsync(context);
            var roleWrite = await RoleRepo.UpsertAsync(
                context, mintedRoleId, TodayOpen, "CW-MINT", "Vai trò mới", false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Add, new OperationDate(Today), "tester",
                "composite");
            roleWrite.IsError.Should().BeFalse(DescribeErrors(roleWrite.Errors));

            mintedGrantId = await GrantRepo.CreateIdentityAsync(context);
            var grantWrite = await GrantRepo.UpsertAsync(
                context, mintedGrantId, TodayOpen, mintedRoleId, function, ScopeLevel.Global,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "composite");
            grantWrite.IsError.Should().BeFalse(DescribeErrors(grantWrite.Errors));

            throw new InvalidOperationException("injected failure after minting inside the transaction");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*injected failure*");

        mintedRoleId.Should().BeGreaterThan(0, "the mint must actually have run — otherwise this test is vacuous");
        mintedGrantId.Should().BeGreaterThan(0, "the mint must actually have run — otherwise this test is vacuous");

        (await CountHeaderRowsAsync("role", mintedRoleId)).Should().Be(0, "the header rolls back with its version");
        (await CountHeaderRowsAsync("role_permission", mintedGrantId)).Should().Be(0, "the header rolls back with its version");
        (await CountVersionRowsAsync("role_version", "role_id", mintedRoleId)).Should().Be(0);
        (await CountVersionRowsAsync("role_permission_version", "role_permission_id", mintedGrantId)).Should().Be(0);
    }

    [Fact]
    public async Task CompositeWrite_MintedIdentity_DoesNotVouchForASameNumberedIdentityOfAnotherTable()
    {
        SkipUnlessDbAvailable();

        // THE DISCRIMINATOR: the grant below names the SAME NUMBER as both its parents — the role minted
        // inside this transaction (legitimately unlocked, §7) and a function identity nobody Enlisted.
        // A created-here registry keyed by id alone would let the role's mark answer for the function and
        // the write would get past the enlistment gate; keyed by (table, id) it cannot.
        long mintedRoleId = 0;

        var result = await new CompositeWrite(NewConnectionFactory()).ExecuteAsync(async context =>
        {
            mintedRoleId = await RoleRepo.CreateIdentityAsync(context);
            var roleWrite = await RoleRepo.UpsertAsync(
                context, mintedRoleId, TodayOpen, "CW-COLLIDE", "Vai trò mới", false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Add, new OperationDate(Today), "tester",
                "composite");
            roleWrite.IsError.Should().BeFalse(DescribeErrors(roleWrite.Errors));

            var grantId = await GrantRepo.CreateIdentityAsync(context);
            var grantWrite = await GrantRepo.UpsertAsync(
                context, grantId, TodayOpen, mintedRoleId, functionId: mintedRoleId, ScopeLevel.Global,
                VersionOperationKind.Add, new OperationDate(Today), "tester", "composite");
            return grantWrite.IsError ? grantWrite.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(
            e => e.Code == "CompositeWrite.NotEnlisted",
            "the function parent was never Enlisted; a same-numbered role minted here may not stand in for it");
        result.Errors.Should().Contain(
            e => e.Description.Contains("function_version"),
            "the refusal must name the FUNCTION parent — naming the role would mean the tables were conflated");

        // The refusal is only half of AC4: the composite must also have rolled back, so the role written
        // before it leaves nothing behind either.
        (await CountHeaderRowsAsync("role", mintedRoleId)).Should().Be(0);
        (await CountVersionRowsAsync("role_version", "role_id", mintedRoleId)).Should().Be(0);
    }

    [Fact]
    public async Task CompositeWrite_EnlistingInsideTheDelegate_DoesNotAuthoriseAnUnlockedWrite()
    {
        SkipUnlessDbAvailable();

        // THE DISCRIMINATOR: the delegate closes over the CompositeWrite, so a caller can call Enlist() after
        // the lock batch has already been acquired. If the context read the LIVE enlistment list, that late
        // Enlist would answer IsEnlisted for a key whose GET_LOCK was never taken — an unlocked write of a
        // pre-existing identity, approved by the very check meant to forbid it (§7 all-locks-up-front).
        var role = await CreateRoleAsync("CW-LATE", "Vai trò gốc", OpenFrom2020);

        var composite = new CompositeWrite(NewConnectionFactory());

        var result = await composite.ExecuteAsync(async context =>
        {
            composite.Enlist(RoleRepo, role);

            var write = await RoleRepo.UpsertAsync(
                context, role, TodayOpen, "CW-LATE", "Vai trò đã đổi tên", false,
                adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester",
                "composite");
            return write.IsError ? write.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(
            e => e.Code == "CompositeWrite.NotEnlisted",
            "enlistment is frozen when the transaction opens — enlisting later must simply not register");
    }

    [Fact]
    public async Task CompositeWrite_MintSeam_RefusesAContextItDidNotCreate()
    {
        SkipUnlessDbAvailable();

        // ICompositeWriteContext is public, so an implementation can exist outside AST.Infrastructure — but
        // the created-here sink is internal, so such a context cannot carry the mark. Minting against it
        // would hand back an id that no write in that transaction could then use: a clear failure is the
        // only honest answer (clear failure over silent ambiguity).
        var act = async () => await RoleRepo.CreateIdentityAsync(new ForeignCompositeWriteContext(null, null));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompositeWrite_ForeignContext_CannotReachTheCreatedHereCarveOut()
    {
        SkipUnlessDbAvailable();

        // The carve-out's containment, from the outside: `ICompositeWriteContext` is public, so anyone can
        // hand a repository their own context — but the created-here registry is internal, so such a context
        // has no way to answer the provenance question at all, and the write falls back on enlistment alone.
        // Scope of the proof: it pins that the PUBLIC contract carries no provenance claim. It does not (and
        // cannot) prove anything about a friend assembly, which by definition can implement the registry.
        var role = await CreateRoleAsync("CW-FOREIGN", "Vai trò gốc", OpenFrom2020);

        var ct = TestContext.Current.CancellationToken;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var write = await RoleRepo.UpsertAsync(
            new ForeignCompositeWriteContext(connection, transaction), role, TodayOpen, "CW-FOREIGN",
            "Vai trò đã đổi tên", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit,
            new OperationDate(Today), "tester", "composite");

        write.IsError.Should().BeTrue();
        write.Errors.Should().Contain(
            e => e.Code == "CompositeWrite.NotEnlisted",
            "a context from outside AST.Infrastructure can neither be Enlisted through nor claim created-here");

        await transaction.RollbackAsync(ct);
    }

    // A context implementing ONLY the public interface — which is exactly the point: it cannot implement the
    // internal created-here registry, so it can make no provenance claim. Connection/Transaction are null in
    // the mint test because that seam's guard runs before either is read.
    private sealed class ForeignCompositeWriteContext(IDbConnection? connection, IDbTransaction? transaction)
        : ICompositeWriteContext
    {
        public IDbConnection Connection => connection ?? throw new NotSupportedException();

        public IDbTransaction Transaction => transaction ?? throw new NotSupportedException();

        public bool IsEnlisted(string versionTable, long identityId) => false;
    }

    // ---------------------------------------------------------------------------------------

    // True when the key is free right now (acquires it briefly, then releases).
    private async Task<bool> CanAcquireAsync(string key)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var got = await connection.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 1)", new { name = key });
        if (got != 1)
        {
            return false;
        }

        await connection.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = key });
        return true;
    }

    // Header rows, not version rows: a zero-version header is invisible to every version-row count, which
    // is exactly why the orphan survived undetected for as long as it did.
    private async Task<long> CountHeaderRowsAsync(string headerTable, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM `{headerTable}` WHERE id = @identityId",
            new { identityId });
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
