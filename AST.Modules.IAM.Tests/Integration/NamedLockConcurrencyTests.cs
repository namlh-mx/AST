using AST.Core.Data;
using AST.Core.EffectivePeriod;
using Dapper;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// B3 -- concurrent-write protection (D6/§7): 2 UpsertVersionAsync calls on the SAME identity running IN PARALLEL
// (2 REAL MySQL connections, no mocking) must be serialized by a named lock (`GET_LOCK('astep:<table>:<identity>', ...)`) --
// after both complete, there must NOT be 2 isactive=1 versions with overlapping periods.
public sealed class NamedLockConcurrencyTests : IamRepositoryTestBase
{
    private static readonly EffectivePeriod OpenFrom2020 = new(new DateOnly(2020, 1, 1), EffectivePeriod.OpenEnd);
    private static readonly EffectivePeriod OpenFrom2025 = new(new DateOnly(2025, 1, 1), EffectivePeriod.OpenEnd);

    [Fact]
    public async Task UpsertAsync_TwoConcurrentWritesOnSameIdentity_SamePeriod_SerializedByNamedLock_NoOverlapAfterward()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("CONC-A", "Đơn vị concurrency", "CONC-A", null, OpenFrom2020);

        // Same identity, SAME new period (exact match -- a case that easily creates an overlap if the named lock is NOT
        // working: both threads read the "active state" BEFORE the other commits -> both independently compute
        // "shrink the base version + insert a new one" -> resulting in 2 active versions on [2025-01-01,open) that OVERLAP each other).
        var taskA = OrgUnits.UpsertAsync(
            id, OpenFrom2025, "CONC-A", "Đơn vị concurrency (A thắng)", "CONC-A", null, VersionOperationKind.Edit,
            "tester-a", "concurrent-a");
        var taskB = OrgUnits.UpsertAsync(
            id, OpenFrom2025, "CONC-A", "Đơn vị concurrency (B thắng)", "CONC-A", null, VersionOperationKind.Edit,
            "tester-b", "concurrent-b");

        var results = await Task.WhenAll(taskA, taskB);

        // The named lock SERIALIZES the writes -- no contention/deadlock, both writes succeed (the one that runs
        // SECOND simply sees the state already changed by the one that ran FIRST, and correctly recomputes accordingly).
        Assert.All(results, r => Assert.False(r.IsError, DescribeErrors(r.Errors)));

        Assert.False(await HasOverlappingActiveVersionsAsync(id), "Bất biến Đ6 bị vi phạm: có >=2 phiên bản active chồng lấn kỳ sau khi ghi đồng thời.");

        var activeCount = await CountActiveVersionsAsync(id, new DateOnly(2025, 6, 1));
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task UpsertAsync_TwoConcurrentWritesOnSameIdentity_DisjointPeriods_SerializedByNamedLock_NoOverlapAfterward()
    {
        SkipUnlessDbAvailable();

        var id = await CreateOrgUnitAsync("CONC-B", "Đơn vị concurrency rời kỳ", "CONC-B", null, OpenFrom2020);

        // 2 DISJOINT periods but both carve into the same base version [2020-01-01, open) -- they still contend for the
        // same named lock key (same identity) -> must still be serialized to avoid both threads reading the base
        // version as "still whole" and independently remnant-ing/soft-deactivating it.
        var periodA = new EffectivePeriod(new DateOnly(2025, 1, 1), new DateOnly(2025, 6, 30));
        var periodB = new EffectivePeriod(new DateOnly(2025, 7, 1), EffectivePeriod.OpenEnd);

        var taskA = OrgUnits.UpsertAsync(
            id, periodA, "CONC-B", "Đơn vị concurrency (nửa đầu 2025)", "CONC-B", null, VersionOperationKind.Edit,
            "tester-a", "concurrent-a");
        var taskB = OrgUnits.UpsertAsync(
            id, periodB, "CONC-B", "Đơn vị concurrency (từ nửa sau 2025)", "CONC-B", null, VersionOperationKind.Edit,
            "tester-b", "concurrent-b");

        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.False(r.IsError, DescribeErrors(r.Errors)));
        Assert.False(await HasOverlappingActiveVersionsAsync(id), "Bất biến Đ6 bị vi phạm sau khi ghi đồng thời 2 kỳ rời nhau.");
    }

    private async Task<bool> HasOverlappingActiveVersionsAsync(long orgUnitId)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM org_unit_version a
            JOIN org_unit_version b ON a.org_unit_id = b.org_unit_id AND a.id < b.id
            WHERE a.org_unit_id = @orgUnitId AND a.isactive = 1 AND b.isactive = 1
              AND a.effective_from <= b.effective_to AND b.effective_from <= a.effective_to
            """,
            new { orgUnitId });
        return count > 0;
    }

    private async Task<long> CountActiveVersionsAsync(long orgUnitId, DateOnly asOf)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM org_unit_version
            WHERE org_unit_id = @orgUnitId AND isactive = 1
              AND effective_from <= @asOf AND @asOf <= effective_to
            """,
            new { orgUnitId, asOf });
    }
}
