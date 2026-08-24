using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;
using AST.Core.Time;

namespace AST.Modules.IAM.Tests.Integration;

// Seam-2 gap F2 (docs/shared-components.md VersionedRepository row): a repository could not contribute an
// arbitrary extra named-lock key (not tied to one (versionTable, identityId), e.g. a business SINGLETON
// lock) to the SAME up-front, fixed-order §7 batch a composite write already acquires — only
// CompositeWrite.Enlist(IVersionedWriteTarget, long) existed. This file pins the new
// CompositeWrite.Enlist(string) overload:
//   - the extra key is acquired in the SAME up-front batch (a concurrent holder blocks the WHOLE
//     composite, exactly like an identity-scoped key would — CompositeWriteTests precedent);
//   - the fixed order is preserved when identity-scoped keys and extra raw keys are combined.
//
// Real MySQL, no mocking (rule-testing invariant 1).
public sealed class CompositeLockHookTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod TodayOpen = new(Today, EffectivePeriod.OpenEnd);

    private RoleRepository RoleRepo => (RoleRepository)Roles;

    private IDbConnectionFactory NewConnectionFactory() =>
        new MySqlConnectionFactory(new FixedConnectionStringProvider(ConnectionString!));

    [Fact]
    public async Task CompositeWrite_ExtraLockKey_IsAcquiredUpFront_BlocksUntilExternalHolderReleases()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F2-LH1", "Vai trò", OpenFrom2020);
        const string extraKey = "astep:singleton:role-admin-flag";

        var roleRowsBefore = await CountVersionRowsAsync("role_version", "role_id", role);

        await using var blocker = await HoldLockAsync(extraKey);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(extraKey);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(cancellationToken);

        var compositeTask = Task.Run(async () => await composite.ExecuteAsync(async context =>
        {
            var write = await RoleRepo.UpsertAsync(
                context, role, TodayOpen,
                "F2-LH1", "Vai trò composite", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return write.IsError ? write.Errors : Result.Success;
        }));

        await WaitUntilUserLockWaiterAsync(observer, extraKey, cancellationToken);

        compositeTask.IsCompleted.Should().BeFalse(
            "the composite must still be waiting on the extra lock key, not have skipped past it");
        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().Be(
            roleRowsBefore, "no write may happen before every up-front lock (including the extra key) is acquired");

        await ReleaseLockAsync(blocker, extraKey);

        var result = await compositeTask;
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
        (await CountVersionRowsAsync("role_version", "role_id", role)).Should().BeGreaterThan(roleRowsBefore);
    }

    [Fact]
    public async Task CompositeWrite_ExtraLockKeyOrderedBeforeIdentityKeys_HeldWhileWaitingOnExtraKeyDoesNotBlockIdentityKey()
    {
        SkipUnlessDbAvailable();

        var role = await CreateRoleAsync("F2-LH2", "Vai trò", OpenFrom2020);
        // "astep:aaa..." sorts ordinally BEFORE "astep:role_version:{id}" -> must be acquired FIRST.
        const string extraKeyFirst = "astep:aaa-singleton";
        var identityKey = $"astep:role_version:{role}";

        await using var blocker = await HoldLockAsync(extraKeyFirst);

        var composite = new CompositeWrite(NewConnectionFactory())
            .Enlist(RoleRepo, role)
            .Enlist(extraKeyFirst);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var observer = new MySqlConnection(UnpooledConnectionString);
        await observer.OpenAsync(cancellationToken);

        var compositeTask = Task.Run(async () => await composite.ExecuteAsync(async context =>
        {
            var write = await RoleRepo.UpsertAsync(
                context, role, TodayOpen,
                "F2-LH2", "Vai trò composite", false, adminFlagChangeAuthorized: false, VersionOperationKind.Edit, new OperationDate(Today), "tester", "composite");
            return write.IsError ? write.Errors : Result.Success;
        }));

        await WaitUntilUserLockWaiterAsync(observer, extraKeyFirst, cancellationToken);

        compositeTask.IsCompleted.Should().BeFalse();

        // THE DISCRIMINATOR: while blocked on the extra key, the composite must NOT already hold the
        // identity key — proving the extra key was ordered (and acquired) BEFORE it, per the fixed order.
        (await CanAcquireAsync(identityKey)).Should().BeTrue(
            "the composite must not hold the identity key while still waiting on the earlier-sorted extra key");

        await ReleaseLockAsync(blocker, extraKeyFirst);

        var result = await compositeTask;
        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));
    }

    // ---------------------------------------------------------------------------------------

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

    private async Task<long> CountVersionRowsAsync(string versionTable, string identityColumn, long identityId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {versionTable} WHERE {identityColumn} = @identityId",
            new { identityId });
    }
}
