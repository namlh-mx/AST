using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// V010 converts a legacy `cancelled` boolean into the durable `status` column. The BACKFILL and the
// abort gate can only be observed on a database that still holds pre-V010 rows, which the shared
// fixture never produces (it re-applies every migration from empty). So T1/T2 stage the schema
// themselves: apply V001-V009, seed, then run V010 alone. T3-T8 (the negative controls) run against
// the FULL schema (including V010), applied once in InitializeAsync -- exactly the ordinary fixture
// path, since they only need the finished CHECK constraints to exist.
//
// Real MySQL, no mocking (rule-testing invariant 1).
[Collection(AstTestDatabaseCollection.Name)]
public sealed class VersionLifecycleStatusMigrationTests : IAsyncLifetime
{
    private readonly string? _connectionString = TestDatabase.TryGetConnectionString();

    public async ValueTask InitializeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        await using var setup = new MySqlConnection(_connectionString);
        await setup.OpenAsync(TestContext.Current.CancellationToken);
        await MigrationRunner.DropAllTablesAsync(setup);
        await MigrationRunner.ApplyAllAsync(setup, TestDatabase.RequireMigrationsDirectory());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string V010Sql() =>
        File.ReadAllText(Path.Combine(TestDatabase.RequireMigrationsDirectory(), "V010__version_lifecycle_status.sql"));

    // Copies V001..V009 into a temp directory so MigrationRunner.ApplyAllAsync stops short of V010.
    private static string StagePreV010Directory()
    {
        var source = TestDatabase.RequireMigrationsDirectory();
        var staged = Directory.CreateTempSubdirectory("ast-v009-").FullName;
        foreach (var file in Directory.GetFiles(source, "V*.sql"))
        {
            if (string.CompareOrdinal(Path.GetFileName(file), "V010") < 0)
            {
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
            }
        }

        return staged;
    }

    private async Task<MySqlConnection> OpenPreV010SchemaAsync(string connectionString)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await MigrationRunner.DropAllTablesAsync(connection);
        await MigrationRunner.ApplyAllAsync(connection, StagePreV010Directory());
        return connection;
    }

    // ---------------------------------------------------------------------------------------
    // T1/T2 -- the backfill and the abort gate, against a database that still holds legacy rows.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task V010_BackfillsCancelledRowsToCancelledStatus()
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var db = await OpenPreV010SchemaAsync(_connectionString!);
        await db.ExecuteAsync("INSERT INTO `org_unit` (id) VALUES (1)");
        await db.ExecuteAsync(
            """
            INSERT INTO org_unit_version
              (org_unit_id, org_code, org_name_full_vn, org_name_short_vn,
               cancelled, effective_from, effective_to, isactive, recorded_by)
            VALUES
              (1, 'C1', 'A', 'A', 1, '2020-01-01', '2020-12-31', 0, 'tester'),
              (1, 'C1', 'A', 'A', 0, '2021-01-01', '9999-12-31', 1, 'tester')
            """);

        await db.ExecuteAsync(V010Sql());

        var statuses = (await db.QueryAsync<string>(
            "SELECT status FROM org_unit_version ORDER BY effective_from")).ToList();
        statuses.Should().Equal("cancelled", "normal");
    }

    [Fact]
    public async Task V010_AbortsWhenLegacyDataViolatesTheInvariant()
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var db = await OpenPreV010SchemaAsync(_connectionString!);
        await db.ExecuteAsync("INSERT INTO `org_unit` (id) VALUES (1)");

        // The shape the old model allowed and the new one forbids: cancelled AND still active.
        await db.ExecuteAsync(
            """
            INSERT INTO org_unit_version
              (org_unit_id, org_code, org_name_full_vn, org_name_short_vn,
               cancelled, effective_from, effective_to, isactive, recorded_by)
            VALUES (1, 'C1', 'A', 'A', 1, '2020-01-01', '9999-12-31', 1, 'tester')
            """);

        var act = async () => await db.ExecuteAsync(V010Sql());

        await act.Should().ThrowAsync<MySqlException>();

        // `cancelled` survives on all three. ⚠️ This assertion does NOT discriminate the phase ordering:
        // the violating row is on org_unit_version, the FIRST table, so even the old interleaved script
        // aborted before any DROP ran. MEASURED -- it passed against that script. The test that actually
        // pins the ordering seeds the LAST table; see the next one.
        (await CountLegacyCancelledColumnsAsync(db)).Should().Be(3);
    }

    // T11 -- F-01, and the fixture is the whole point. MySQL implicitly COMMITs each DDL statement, so a
    // .sql file is not one transaction and the statement ORDER is the recovery story. The violating row
    // goes on role_permission_version, the LAST table V010 touches:
    //   old script (DROP interleaved per table) -> org_unit_version and role_version have ALREADY dropped
    //                                              `cancelled` when the third CHECK fails => count is 1
    //   this script (every DROP in phase 4)     -> nothing is dropped => count is 3
    // Seeding the FIRST table instead would pass under both and prove nothing, which is exactly what the
    // sibling test above turned out to do.
    [Fact]
    public async Task V010_AbortingOnTheLastTable_LeavesEveryTablesLegacyColumnIntact()
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var db = await OpenPreV010SchemaAsync(_connectionString!);
        await db.ExecuteAsync("INSERT INTO `role` (id) VALUES (1)");
        await db.ExecuteAsync("INSERT INTO `function` (id) VALUES (1)");
        await db.ExecuteAsync("INSERT INTO `role_permission` (id) VALUES (1)");

        // cancelled = 1 AND isactive = 1: legal under the old model, forbidden by chk_rpv_status.
        await db.ExecuteAsync(
            """
            INSERT INTO role_permission_version
              (role_permission_id, role_id, function_id, scope_level,
               cancelled, effective_from, effective_to, isactive, recorded_by)
            VALUES (1, 1, 1, 4, 1, '2020-01-01', '9999-12-31', 1, 'tester')
            """);

        var act = async () => await db.ExecuteAsync(V010Sql());

        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(3819);

        (await CountLegacyCancelledColumnsAsync(db)).Should().Be(
            3, "an abort on the LAST table must still leave the first two tables' `cancelled` intact -- "
             + "every DROP COLUMN lives in phase 4, after every gate");
    }

    // T9 -- F-03: `cancelled` is TINYINT(1), which is TINYINT with a display width; it has no domain
    // constraint and holds -128..127. The backfill keys on `= 1`, so a stray 2 would land as 'normal'
    // and lose its meaning permanently once the column is dropped. Phase 1 gates that before anything
    // is added or destroyed.
    [Fact]
    public async Task V010_AbortsWhenALegacyCancelledValueIsOutsideZeroOne()
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var db = await OpenPreV010SchemaAsync(_connectionString!);
        await db.ExecuteAsync("INSERT INTO `org_unit` (id) VALUES (1)");
        await db.ExecuteAsync(
            """
            INSERT INTO org_unit_version
              (org_unit_id, org_code, org_name_full_vn, org_name_short_vn,
               cancelled, effective_from, effective_to, isactive, recorded_by)
            VALUES (1, 'C1', 'A', 'A', 2, '2020-01-01', '9999-12-31', 0, 'tester')
            """);

        var act = async () => await db.ExecuteAsync(V010Sql());

        // 3819 = CHECK violated. Without the number this would also pass if V010 failed for any other
        // reason, which is the whole lesson of T3-T8 above.
        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "chk_ouv_legacy_cancelled_domain must refuse the row before any column is added or dropped");

        (await CountLegacyCancelledColumnsAsync(db)).Should().Be(
            3, "phase 1 aborts before phase 2 adds anything, so all three tables are untouched");

        // The row's original marker survives -- the point of gating rather than backfilling it away.
        // ⚠️ `cancelled + 0`, not `cancelled`: MySqlConnector's TreatTinyAsBoolean is on by default, so a
        // plain read of a TINYINT(1) comes back as a bool and 2 arrives as 1. MEASURED -- this assertion
        // failed with "found 1" until the `+ 0`. That conversion is also WHY this finding bites: in the
        // old C# model a stray 2 read as Cancelled = true, while the backfill's `WHERE cancelled = 1`
        // would have left it 'normal' -- the migration would have silently flipped that row's meaning.
        (await db.ExecuteScalarAsync<int>("SELECT cancelled + 0 FROM org_unit_version")).Should().Be(2);
    }

    private static async Task<int> CountLegacyCancelledColumnsAsync(MySqlConnection db) =>
        await db.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = DATABASE() AND column_name = 'cancelled'
              AND table_name IN ('org_unit_version', 'role_version', 'role_permission_version')
            """);

    // ---------------------------------------------------------------------------------------
    // T3-T6, T8 -- negative controls against the FULL schema: the database must REJECT each
    // incoherent shape, not merely happen to never write it. One row per forbidden shape (not a
    // generated loop over the enum) so a failure names exactly which shape was wrongly accepted.
    // ---------------------------------------------------------------------------------------

    // Each row is a shape chk_ouv_status must REFUSE. The row comments name which one, deliberately:
    // a single [Theory] that fails tells you a shape was accepted without saying which.
    [Theory]
    [InlineData("cancelled", true, false)]  // T3 cancelled but active
    [InlineData("replaced", true, true)]    // T4 replaced but active
    [InlineData("replaced", false, false)]  // T5 replaced with no successor
    [InlineData("normal", true, true)]      // T6 normal WITH a successor
    [InlineData("retired", true, false)]    // T8 a value outside the domain
    public async Task ForbiddenLifecycleShapes_AreRejectedByTheDatabase(string status, bool isActive, bool hasSuccessor)
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var orgUnitId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");
        long? successorId = hasSuccessor
            ? await connection.ExecuteScalarAsync<long>("INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();")
            : null;

        var act = async () => await connection.ExecuteAsync(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, status, replaced_by_org_unit_id,
                 effective_from, effective_to, isactive, recorded_by)
            VALUES
                (@orgUnitId, 'FRB', 'A', 'A', @status, @successorId, '2020-01-01', '9999-12-31', @isActive, 'tester')
            """,
            new { orgUnitId, status, successorId, isActive });

        // The NUMBER, not just the exception type. `MySqlException` alone does not distinguish "the CHECK
        // rejected this row" from "the `status` column does not exist yet" -- and that is not theoretical:
        // all five rows here PASSED with V010 absent, because an INSERT naming a missing column throws
        // MySqlException too. MEASURED 2026-08-23 against this server: CHECK violated = 3819,
        // unknown column = 1054, unknown table = 1146. Asserting 3819 is what makes this test able to fail
        // for the reason it claims.
        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "the row must be refused by chk_ouv_status, not by a missing column or table");
    }

    // T10 -- F-02: the CHECK must enforce the EXACT token, not a collation-equivalent one. The tables
    // default to utf8mb4_0900_ai_ci (accent-insensitive, case-insensitive), under which `status =
    // 'cancelled'` also matches these spellings -- they would satisfy chk_ouv_status, then fail to
    // materialize as a VersionLifecycleStatus, i.e. the database would wave through a value the code
    // above it cannot read. V010 declares the column COLLATE utf8mb4_0900_as_cs to close that.
    // ⚠ Both rows below are ACCEPTED without the COLLATE clause -- that is what makes this a control
    // rather than a restatement of T8's out-of-domain case.
    [Theory]
    [InlineData("Cancelled")]   // case variant
    [InlineData("cancelléd")]   // accent variant
    [InlineData("CANCELLED")]   // upper case
    public async Task CollationEquivalentStatusTokens_AreRejectedByTheDatabase(string status)
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var orgUnitId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO org_unit () VALUES (); SELECT LAST_INSERT_ID();");

        // isactive = 0 deliberately: the row is otherwise a PERFECTLY legal cancelled row, so the only
        // thing that can reject it is the token's exactness. A row that also violated the isactive rule
        // would pass this test for the wrong reason.
        var act = async () => await connection.ExecuteAsync(
            """
            INSERT INTO org_unit_version
                (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, status,
                 effective_from, effective_to, isactive, recorded_by)
            VALUES
                (@orgUnitId, 'COL', 'A', 'A', @status, '2020-01-01', '9999-12-31', 0, 'tester')
            """,
            new { orgUnitId, status });

        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "only the exact lowercase token is a VersionLifecycleStatus name; the CHECK must say so");
    }

    // T7 -- a separate shape on a DIFFERENT table's CHECK: role_version's domain has no `replaced` value
    // at all (replacement is org-unit-only in v1), so this pins a different constraint than T3-T6/T8.
    // T7: replacement is org-unit-scoped in v1, so `replaced` is not merely unwritten on role_version --
    // chk_rv_status makes it unrepresentable. A different constraint on a different table, hence its own
    // [Fact] rather than a sixth row above.
    [Fact]
    public async Task ReplacedStatusOnRoleVersion_IsRejectedByTheDatabase()
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var roleId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO role () VALUES (); SELECT LAST_INSERT_ID();");

        var act = async () => await connection.ExecuteAsync(
            """
            INSERT INTO role_version
                (role_id, role_code, role_name, status, effective_from, effective_to, isactive, recorded_by)
            VALUES
                (@roleId, 'FRB-ROLE', 'A', 'replaced', '2020-01-01', '9999-12-31', 0, 'tester')
            """,
            new { roleId });

        // 3819 = CHECK violated (MEASURED 2026-08-23; unknown column is 1054, unknown table 1146). Without
        // the number this passes on an unmigrated schema, which is the failure it exists to rule out.
        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "the row must be refused by chk_rv_status, not by a missing column or table");
    }
}
