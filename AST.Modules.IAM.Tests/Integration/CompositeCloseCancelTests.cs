using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Infrastructure;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Seam-2 gap F1 (docs/shared-components.md VersionedRepository row): CloseVersionAsync/CancelVersionAsync
// had no ICompositeWriteContext overload, so a caller could not Close/Cancel a version as part of a larger
// composite write (e.g. "revoke a grant" = Close a role_permission version inside the same transaction as
// editing its parent role). This file pins the 2 new composite overloads:
//   - identical persisted results vs the non-context sibling for equivalent input (parity);
//   - not-Enlist-ed fails clear (CompositeWrite.NotEnlisted), same as UpsertVersionAsync(context, ...);
//   - a Close/Cancel enlisted inside a composite genuinely shares ONE transaction with another write —
//     a later failure rolls BOTH back (the actual motivating scenario).
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class CompositeCloseCancelTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod FuturePlan2027 = new(new DateOnly(2027, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly DateOnly CutAt = Today.AddDays(-1);

    private RoleRepository RoleRepo => (RoleRepository)Roles;

    private IDbConnectionFactory NewConnectionFactory() =>
        new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));

    // ---------------------------------------------------------------------------------------
    // Parity — CloseVersionAsync(context, ...) vs CloseVersionAsync(...) on 2 identical fixtures.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CloseVersionAsync_ContextAndNonContext_ProduceIdenticalPersistedResults()
    {
        SkipUnlessDbAvailable();

        var roleA = await CreateRoleAsync("F1-CA", "Vai trò A", OpenFrom2020);
        var roleB = await CreateRoleAsync("F1-CB", "Vai trò B", OpenFrom2020);
        var versionIdA = (await Roles.GetByIdentityAsync(roleA, Today)).Value.Id;
        var versionIdB = (await Roles.GetByIdentityAsync(roleB, Today)).Value.Id;

        var nonContextResult = await RoleRepo.CloseVersionAsync(roleA, versionIdA, CutAt, new OperationDate(Today), "closer", "đóng A");

        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(RoleRepo, roleB);
        var contextResultHolder = default(ErrorOr<UpsertResult>);
        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            contextResultHolder = await RoleRepo.CloseVersionAsync(context, roleB, versionIdB, CutAt, new OperationDate(Today), "closer", "đóng B");
            return contextResultHolder.IsError ? contextResultHolder.Errors : Result.Success;
        });

        nonContextResult.IsError.Should().BeFalse(DescribeErrors(nonContextResult.Errors));
        compositeResult.IsError.Should().BeFalse(DescribeErrors(compositeResult.Errors));
        contextResultHolder.IsError.Should().BeFalse(DescribeErrors(contextResultHolder.Errors));

        var rowsA = await ReadRoleVersionsAsync(roleA);
        var rowsB = await ReadRoleVersionsAsync(roleB);

        rowsA.Should().HaveCount(rowsB.Count, "the composite-context Close must produce the same row shape as the non-context Close");
        rowsA.Should().ContainSingle(r => r.IsActive).Subject.EffectiveTo.Should().Be(CutAt);
        rowsB.Should().ContainSingle(r => r.IsActive).Subject.EffectiveTo.Should().Be(CutAt);
        rowsA.Should().Contain(r => !r.IsActive, "the original version must be soft-deactivated, never deleted");
        rowsB.Should().Contain(r => !r.IsActive, "the original version must be soft-deactivated, never deleted");
    }

    [Fact]
    public async Task CloseVersionAsync_Context_NotEnlisted_FailsClear_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F1-NE", "Vai trò", OpenFrom2020);
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;
        var rowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);

        // Deliberately NOT enlisted.
        var composite = new CompositeWrite(NewConnectionFactory());
        var result = await composite.ExecuteAsync(async context =>
        {
            var close = await RoleRepo.CloseVersionAsync(context, role, versionId, CutAt, new OperationDate(Today), "closer", "đóng");
            return close.IsError ? close.Errors : ErrorOr.Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "CompositeWrite.NotEnlisted");
        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(rowsBefore);
    }

    // ---------------------------------------------------------------------------------------
    // Parity — CancelVersionAsync(context, ...) vs CancelVersionAsync(...), via the test-only
    // SelfOwningOrgUnitRepository (org_unit_version records the cancelled state in `status`, and CancelPlanAsync there
    // wraps the protected base call, same shape as OrgUnitRepository.CancelPlanAsync).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelVersionAsync_ContextAndNonContext_ProduceIdenticalPersistedResults()
    {
        SkipUnlessDbAvailable();

        var repo = BuildSelfOwningOrgUnitRepository();

        var orgA = await repo.CreateIdentityAsync();
        var orgB = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(orgA, FuturePlan2027, "F1CANA", null, "tester", "seed")).IsError.Should().BeFalse();
        (await repo.UpsertAsync(orgB, FuturePlan2027, "F1CANB", null, "tester", "seed")).IsError.Should().BeFalse();

        var versionIdA = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", orgA);
        var versionIdB = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", orgB);

        var nonContextResult = await repo.CancelPlanAsync(orgA, versionIdA, Today, "canceller", "hủy A");

        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(repo, orgB);
        var contextResultHolder = default(ErrorOr<UpsertResult>);
        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            contextResultHolder = await repo.CancelPlanAsync(context, orgB, versionIdB, Today, "canceller", "hủy B");
            return contextResultHolder.IsError ? contextResultHolder.Errors : Result.Success;
        });

        nonContextResult.IsError.Should().BeFalse(DescribeErrors(nonContextResult.Errors));
        compositeResult.IsError.Should().BeFalse(DescribeErrors(compositeResult.Errors));
        contextResultHolder.IsError.Should().BeFalse(DescribeErrors(contextResultHolder.Errors));

        var cancelledA = await ReadCancelledFlagAsync(versionIdA);
        var cancelledB = await ReadCancelledFlagAsync(versionIdB);
        cancelledA.Should().BeTrue();
        cancelledB.Should().BeTrue("the composite-context Cancel must produce the same durable marker as the non-context Cancel");
    }

    // TASK 0 (2026-08-11) — Additional guard test #3: the composite overload behaves IDENTICALLY to the
    // non-context overload under the same "engine no longer re-reads its own clock" fix. Uses
    // AdvancingBusinessDateProvider (TestSupport) so the SECOND `.Today` read (which the pre-fix engine
    // used to perform independently inside CancelVersionCoreAsync) would have rolled to Today+1 and
    // wrongly BLOCKed — proving the composite path also relies on the caller-supplied `operationDate`
    // parameter, not on re-reading its own IBusinessDateProvider.
    [Fact]
    public async Task CancelVersionAsync_Context_UsesCallerOperationDate_NotEngineOwnClock_Succeeds()
    {
        SkipUnlessDbAvailable();

        var advancing = new AdvancingBusinessDateProvider(Today);
        var repo = BuildSelfOwningOrgUnitRepository(advancing);

        var org = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(org, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), "F1CANR", null, "tester", "seed"))
            .IsError.Should().BeFalse();
        var versionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", org, Today);

        // FIRST read (mirrors the caller capturing its own operationDate once, e.g.
        // OrgUnitDeclarationService's `today`) -- returns `Today`.
        var operationDate = advancing.Today;

        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(repo, org);
        var contextResultHolder = default(ErrorOr<UpsertResult>);
        var compositeResult = await composite.ExecuteAsync(async context =>
        {
            contextResultHolder = await repo.CancelPlanAsync(context, org, versionId, operationDate, "canceller", "hủy");
            return contextResultHolder.IsError ? contextResultHolder.Errors : Result.Success;
        });

        compositeResult.IsError.Should().BeFalse(DescribeErrors(compositeResult.Errors));
        contextResultHolder.IsError.Should().BeFalse(DescribeErrors(contextResultHolder.Errors));
        contextResultHolder.Errors.Should().NotContain(e => e.Code == "VersionedRepository.NotAFuturePlan");
        (await ReadCancelledFlagAsync(versionId)).Should().BeTrue();
    }

    [Fact]
    public async Task CancelVersionAsync_Context_NotEnlisted_FailsClear_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var repo = BuildSelfOwningOrgUnitRepository();
        var org = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(org, FuturePlan2027, "F1CANNE", null, "tester", "seed")).IsError.Should().BeFalse();
        var versionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", org);
        var rowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", org);

        var composite = new CompositeWrite(NewConnectionFactory());
        var result = await composite.ExecuteAsync(async context =>
        {
            var cancel = await repo.CancelPlanAsync(context, org, versionId, Today, "canceller", "hủy");
            return cancel.IsError ? cancel.Errors : ErrorOr.Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "CompositeWrite.NotEnlisted");
        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", org)).Should().Be(rowsBefore);
    }

    // ---------------------------------------------------------------------------------------
    // The actual motivating scenario (task brief): Close enlisted alongside another repository's write
    // in ONE composite transaction — a later failure rolls BOTH back.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CompositeWrite_CloseEnlistedWithAnotherWrite_LaterFailureRollsBothBack()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F1-RB", "Vai trò", OpenFrom2020);
        // A function whose coverage does NOT extend far enough to cover the role rename below —
        // a real business reason for the SECOND write to fail (STRICT temporal-FK), not a synthetic one.
        var narrowFunction = await CreateFunctionAsync("F1.Fn.Narrow", new EffectivePeriod(new DateOnly(2021, 1, 1), new DateOnly(2021, 12, 31)));
        var versionId = (await Roles.GetByIdentityAsync(role, Today)).Value.Id;

        var grant = await CreateRolePermissionHeaderAsync();
        var roleRowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist((RolePermissionRepository)RolePermissions, grant)
            .Enlist((FunctionRepository)Functions, narrowFunction);

        var closeResultHolder = default(ErrorOr<UpsertResult>);
        var result = await composite.ExecuteAsync(async context =>
        {
            closeResultHolder = await RoleRepo.CloseVersionAsync(context, role, versionId, CutAt, new OperationDate(Today), "closer", "đóng vai trò");
            if (closeResultHolder.IsError)
            {
                return closeResultHolder.Errors;
            }

            var grantWrite = await ((RolePermissionRepository)RolePermissions).UpsertAsync(
                context, grant, new EffectivePeriod(Today, EffectivePeriod.OpenEnd), role, narrowFunction, ScopeLevel.Global, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return grantWrite.IsError ? grantWrite.Errors : Result.Success;
        });

        closeResultHolder.IsError.Should().BeFalse("the Close itself must genuinely have succeeded inside the transaction");
        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "TemporalFk.ParentGap");

        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(
            roleRowsBefore, "the Close must have been rolled back along with the failed grant write");
        var stillOpen = await Roles.GetByIdentityAsync(role, new DateOnly(2030, 1, 1));
        stillOpen.IsError.Should().BeFalse(DescribeErrors(stillOpen.Errors));
    }

    // ---------------------------------------------------------------------------------------
    // F-2/F-3: the composite path's `context.IsEnlisted` dependent
    // check was never exercised with an actual exclusively-owned dependent. Uses the same test-only
    // SelfOwningOrgUnitRepository double as CancelAutoCutTests.cs (only entity with BOTH
    // SupportsCancellation and a declared ExclusivelyOwnedDependents edge).
    // ---------------------------------------------------------------------------------------

    private static readonly EffectivePeriod Bounded2020To2025Mid = new(new DateOnly(2020, 1, 1), new DateOnly(2025, 6, 30));
    private static readonly DateOnly DependentSeedFrom = new(2024, 1, 1);

    [Fact]
    public async Task CompositeCancel_ExclusivelyOwnedDependent_Enlisted_IsAutoCutInSameTransaction()
    {
        SkipUnlessDbAvailable();

        var repo = BuildSelfOwningOrgUnitRepository();
        var parent = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(parent, Bounded2020To2025Mid, "F23P1", null, "tester", "seed")).IsError.Should().BeFalse();
        (await repo.UpsertAsync(parent, FuturePlan2027, "F23P1", null, "tester", "future plan")).IsError.Should().BeFalse();

        var child = await repo.CreateIdentityAsync();
        var seededVersionId = await repo.SeedDependentVersionAsync(child, parent, DependentSeedFrom, EffectivePeriod.OpenEnd, "seed");

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent, FuturePlan2027.From);

        // Both the parent AND the dependent are Enlist-ed up front — the positive case for F1/F2's decision.
        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(repo, parent).Enlist(repo, child);
        var result = await composite.ExecuteAsync(async context =>
        {
            var cancel = await repo.CancelPlanAsync(context, parent, targetVersionId, Today, "canceller", "hủy kế hoạch");
            return cancel.IsError ? cancel.Errors : Result.Success;
        });

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var childRows = await ReadOrgUnitVersionRowsAsync(child);
        childRows.Should().HaveCount(2, "the dependent must be remnant-cut, not deleted");
        childRows.Should().Contain(r => r.Id == seededVersionId && !r.IsActive,
            "the pre-cut dependent version must be soft-deactivated, never physically deleted");
        var cutRow = childRows.Should().ContainSingle(r => r.IsActive).Subject;
        cutRow.EffectiveFrom.Should().Be(DependentSeedFrom);
        cutRow.EffectiveTo.Should().Be(new DateOnly(2025, 6, 30));
    }

    [Fact]
    public async Task CompositeCancel_ExclusivelyOwnedDependent_NotEnlisted_FailsClearWithDistinctCode_WritesNothing()
    {
        SkipUnlessDbAvailable();

        var repo = BuildSelfOwningOrgUnitRepository();
        var parent = await repo.CreateIdentityAsync();
        (await repo.UpsertAsync(parent, Bounded2020To2025Mid, "F23P2", null, "tester", "seed")).IsError.Should().BeFalse();
        (await repo.UpsertAsync(parent, FuturePlan2027, "F23P2", null, "tester", "future plan")).IsError.Should().BeFalse();

        var child = await repo.CreateIdentityAsync();
        var seededVersionId = await repo.SeedDependentVersionAsync(child, parent, DependentSeedFrom, EffectivePeriod.OpenEnd, "seed");

        var targetVersionId = await GetActiveVersionIdAsync("org_unit_version", "org_unit_id", parent, FuturePlan2027.From);
        var parentRowsBefore = await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent);

        // Only the parent is Enlist-ed — the dependent was forgotten (a caller bug, not a race).
        var composite = new CompositeWrite(NewConnectionFactory()).Enlist(repo, parent);
        var result = await composite.ExecuteAsync(async context =>
        {
            var cancel = await repo.CancelPlanAsync(context, parent, targetVersionId, Today, "canceller", "hủy kế hoạch");
            return cancel.IsError ? cancel.Errors : Result.Success;
        });

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "VersionedRepository.DependentNotEnlisted",
            "a forgotten-enlistment bug must be reported distinctly from the retryable DependentSetChanged race guard");
        result.Errors.Should().NotContain(e => e.Code == "VersionedRepository.DependentSetChanged");

        (await CountVersionRowsAsync("org_unit_version", "org_unit_id", parent)).Should().Be(
            parentRowsBefore, "the whole composite transaction must roll back, writing nothing");
        var dependentRows = await ReadOrgUnitVersionRowsAsync(child);
        dependentRows.Should().ContainSingle(r => r.Id == seededVersionId && r.IsActive,
            "the dependent must be completely untouched when the composite rolls back");
    }

    // ---------------------------------------------------------------------------------------

    private sealed class OrgUnitVersionRow
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly EffectiveTo { get; init; }
    }

    private async Task<IReadOnlyList<OrgUnitVersionRow>> ReadOrgUnitVersionRowsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<OrgUnitVersionRow>(
            """
            SELECT id AS Id, isactive AS IsActive, effective_from AS EffectiveFrom, effective_to AS EffectiveTo
            FROM org_unit_version WHERE org_unit_id = @orgUnitId
            """,
            new { orgUnitId });
        return rows.ToList();
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

    private SelfOwningOrgUnitRepository BuildSelfOwningOrgUnitRepository() =>
        BuildSelfOwningOrgUnitRepository(new FixedBusinessDateProvider(Today));

    private SelfOwningOrgUnitRepository BuildSelfOwningOrgUnitRepository(AST.Core.Time.IBusinessDateProvider dates) =>
        new(
            new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!)),
            new StandardScopeFilterBuilder(),
            new EffectivePeriodResolver(),
            new PeriodEditor(),
            FkValidator,
            IamTemporalFkEdges.CreateRegistry(),
            dates);

    private sealed class RoleVersionRow
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public DateOnly EffectiveTo { get; init; }
    }

    private async Task<IReadOnlyList<RoleVersionRow>> ReadRoleVersionsAsync(long roleId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var rows = await connection.QueryAsync<RoleVersionRow>(
            "SELECT id AS Id, isactive AS IsActive, effective_to AS EffectiveTo FROM role_version WHERE role_id = @roleId",
            new { roleId });
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

    private async Task<bool> ReadCancelledFlagAsync(long versionId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT (status = 'cancelled') FROM org_unit_version WHERE id = @versionId", new { versionId });
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
