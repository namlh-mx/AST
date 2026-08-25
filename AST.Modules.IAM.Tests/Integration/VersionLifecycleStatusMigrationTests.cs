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
    // ⚠ One row per TABLE (AI Agent AST-CONSULT-144/F-06). Phase 1 writes THREE separate constraints, one
    // per table, and this control originally covered org_unit_version alone -- the gate was claimed for
    // three tables while covering one.
    // MEASURED per table, ALL THREE (AI Agent AST-CONSULT-147/F-04 asked for this; two of the three had
    // been inference): removing chk_ouv_/chk_rv_/chk_rpv_legacy_cancelled_domain and its phase-4 DROP
    // reddens exactly that table's row and leaves the other two GREEN.
    // The assertion on the constraint NAME is a second, narrower claim: that THIS table's own gate is
    // what fired, not merely that some CHECK did.
    // ⚠ TWO mutations, because they witness DIFFERENT assertions and neither can stand in for the other.
    // (An earlier draft of this comment cited only the removal and reported the rename's result -- the
    // removal structurally CANNOT reach the name assertion, so that was a claim, not a measurement.)
    //   REMOVE chk_rv_legacy_cancelled_domain and its phase-4 DROP -> V010 runs to COMPLETION, so the row
    //     reddens at the ThrowAsync below and never reaches line 184. Witnesses the gate's EXISTENCE only.
    //   RENAME it to something that is NOT a superstring of the original (a `_x` suffix would still
    //     satisfy Contain) -> the abort and the 3819 both still pass and ONLY the name assertion reddens.
    // MEASURED, both, on this server: exactly the role_version row went red each time, the other two
    // stayed green.
    [Theory]
    [InlineData("org_unit_version", "chk_ouv_legacy_cancelled_domain")]
    [InlineData("role_version", "chk_rv_legacy_cancelled_domain")]
    [InlineData("role_permission_version", "chk_rpv_legacy_cancelled_domain")]
    public async Task V010_AbortsWhenALegacyCancelledValueIsOutsideZeroOne(string table, string constraintName)
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var db = await OpenPreV010SchemaAsync(_connectionString!);
        await SeedOutOfDomainCancelledRowAsync(db, table);

        var act = async () => await db.ExecuteAsync(V010Sql());

        // 3819 = CHECK violated. Without the number this would also pass if V010 failed for any other
        // reason, which is the whole lesson of T3-T8 above.
        var thrown = (await act.Should().ThrowAsync<MySqlException>()).Which;
        thrown.Number.Should().Be(
            3819, $"{constraintName} must refuse the row before any column is added or dropped");
        // ⚠ This asserts on the server's HUMAN-READABLE message, which MySqlConnector surfaces verbatim
        // and does not document as a contract -- unlike Number/SqlState (AI Agent AST-CONSULT-147/F-02).
        // Kept, with the dependency stated rather than hidden: there is no structured field carrying the
        // violated constraint's NAME, so dropping this assertion means dropping the claim, not moving it.
        // The name is a format PARAMETER of ER_CHECK_CONSTRAINT_VIOLATED, so it survives a translated
        // message even though the surrounding words would not.
        // PINNED, measured on the same server the mutation runs used: MySQL 9.7.1, @@lc_messages=en_US.
        // If this ever reddens on a server that is otherwise correct, that matrix is what changed.
        thrown.Message.Should().Contain(
            constraintName, "this row's whole claim is that THIS table's own gate fired, not a sibling's");

        // ⚠ The reason names COLUMNS, not tables, and the distinction is load-bearing (AI Agent
        // AST-CONSULT-147/F-03). On the role_permission_version row the first two tables ALREADY carry
        // their phase-1 CHECK by the time this runs, so "all three tables are untouched" -- the wording
        // this replaces -- was false while the assertion itself was true. A reason wider than what its
        // assertion can see is how the next reader inherits a claim nothing tests.
        (await CountLegacyCancelledColumnsAsync(db)).Should().Be(
            3, "the abort preserves every table's legacy `cancelled` column: nothing is DROPped before "
             + "phase 4, whichever table's gate fires");

        // The row's original marker survives -- the point of gating rather than backfilling it away.
        // ⚠️ `cancelled + 0`, not `cancelled`: MySqlConnector's TreatTinyAsBoolean is on by default, so a
        // plain read of a TINYINT(1) comes back as a bool and 2 arrives as 1. MEASURED -- this assertion
        // failed with "found 1" until the `+ 0`. That conversion is also WHY this finding bites: in the
        // old C# model a stray 2 read as Cancelled = true, while the backfill's `WHERE cancelled = 1`
        // would have left it 'normal' -- the migration would have silently flipped that row's meaning.
        // The table name is interpolated, never user input -- it comes from this test's own [InlineData].
        (await db.ExecuteScalarAsync<int>($"SELECT cancelled + 0 FROM `{table}`"))
            .Should().Be(OutOfDomainCancelled);
    }

    // The one value this control is about: legal for TINYINT, outside {0,1}, and NOT matched by the
    // backfill's `WHERE cancelled = 1`. A const rather than a helper parameter -- the seed has exactly
    // one caller and one meaningful value, and a parameter would invite a 0 or 1 for which no assertion
    // here makes any claim (AI Agent, this round).
    private const int OutOfDomainCancelled = 2;

    // Seeds ONE version row carrying OutOfDomainCancelled on the named table, minting whatever identity
    // rows that table's FKs require. PRE-V010 schema only -- `status` does not exist yet.
    // Called BEFORE `act`, deliberately: a seed this helper cannot place must fail loudly here, not be
    // mistaken for the abort the test is asserting.
    private static async Task SeedOutOfDomainCancelledRowAsync(MySqlConnection db, string table)
    {
        const int cancelled = OutOfDomainCancelled;
        switch (table)
        {
            case "org_unit_version":
                await db.ExecuteAsync("INSERT INTO `org_unit` (id) VALUES (1)");
                await db.ExecuteAsync(
                    """
                    INSERT INTO org_unit_version
                      (org_unit_id, org_code, org_name_full_vn, org_name_short_vn,
                       cancelled, effective_from, effective_to, isactive, recorded_by)
                    VALUES (1, 'C1', 'A', 'A', @cancelled, '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { cancelled });
                break;

            case "role_version":
                await db.ExecuteAsync("INSERT INTO `role` (id) VALUES (1)");
                await db.ExecuteAsync(
                    """
                    INSERT INTO role_version
                      (role_id, role_code, role_name,
                       cancelled, effective_from, effective_to, isactive, recorded_by)
                    VALUES (1, 'R1', 'A', @cancelled, '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { cancelled });
                break;

            case "role_permission_version":
                await db.ExecuteAsync("INSERT INTO `role` (id) VALUES (1)");
                await db.ExecuteAsync("INSERT INTO `function` (id) VALUES (1)");
                await db.ExecuteAsync("INSERT INTO `role_permission` (id) VALUES (1)");
                await db.ExecuteAsync(
                    """
                    INSERT INTO role_permission_version
                      (role_permission_id, role_id, function_id, scope_level,
                       cancelled, effective_from, effective_to, isactive, recorded_by)
                    VALUES (1, 1, 1, 4, @cancelled, '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { cancelled });
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(table), table, "no pre-V010 seed is defined for this table");
        }
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
    // ⚠ All three tokens below are ACCEPTED without the COLLATE clause -- that is what makes this a
    // control rather than a restatement of T8's out-of-domain case.
    // ⚠ One set of tokens per TABLE (AI Agent AST-CONSULT-144/F-05). V010 writes COLLATE utf8mb4_0900_as_cs
    // three times, once per table, and this control originally covered org_unit_version alone -- so the
    // collation claim held for one table while being stated for three.
    // MEASURED per table, ALL THREE (AI Agent AST-CONSULT-147/F-04 asked for this; the previous wording
    // reported two tables that had never been run): stripping COLLATE from org_unit_version,
    // role_version or role_permission_version reddens exactly that table's 3 rows and leaves the
    // other 6 GREEN.
    [Theory]
    [InlineData("org_unit_version", "Cancelled")]              // case variant
    [InlineData("org_unit_version", "cancelléd")]              // accent variant
    [InlineData("org_unit_version", "CANCELLED")]              // upper case
    [InlineData("role_version", "Cancelled")]
    [InlineData("role_version", "cancelléd")]
    [InlineData("role_version", "CANCELLED")]
    [InlineData("role_permission_version", "Cancelled")]
    [InlineData("role_permission_version", "cancelléd")]
    [InlineData("role_permission_version", "CANCELLED")]
    public async Task CollationEquivalentStatusTokens_AreRejectedByTheDatabase(string table, string status)
    {
        TestDatabase.SkipUnlessAvailable(_connectionString is not null);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var act = await ArrangeStatusInsertAsync(connection, table, status);

        (await act.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(
            3819, "only the exact lowercase token is a VersionLifecycleStatus name; the CHECK must say so");
    }

    // Mints the identity rows the named table's FKs require, then returns the version-row INSERT itself
    // as the delegate to assert on. FULL (post-V010) schema.
    // ⚠ The minting happens HERE and not inside the returned delegate (AI Agent, this round). The
    // assertion above pins error 3819 = CHECK violated; if a setup INSERT ran inside `act` and some
    // identity table later gained a CHECK of its own, a rejected SETUP row would raise 3819 too and the
    // test would go green without ever reaching the token it exists to test -- a guard narrower than its
    // claim, which is the exact shape this whole review round is closing. Today no identity table has a
    // CHECK, so this is a latent hole being closed, not a live bug.
    // isactive = 0 deliberately: the row is otherwise a PERFECTLY legal cancelled row, so the only thing
    // that can reject it is the token's exactness. A row that also violated the isactive rule would pass
    // its test for the wrong reason.
    private static async Task<Func<Task>> ArrangeStatusInsertAsync(
        MySqlConnection connection, string table, string status)
    {
        switch (table)
        {
            case "org_unit_version":
            {
                var orgUnitId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO `org_unit` () VALUES (); SELECT LAST_INSERT_ID();");
                return async () => await connection.ExecuteAsync(
                    """
                    INSERT INTO org_unit_version
                        (org_unit_id, org_code, org_name_full_vn, org_name_short_vn, status,
                         effective_from, effective_to, isactive, recorded_by)
                    VALUES
                        (@orgUnitId, 'COL', 'A', 'A', @status, '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { orgUnitId, status });
            }

            case "role_version":
            {
                var roleId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO `role` () VALUES (); SELECT LAST_INSERT_ID();");
                return async () => await connection.ExecuteAsync(
                    """
                    INSERT INTO role_version
                        (role_id, role_code, role_name, status,
                         effective_from, effective_to, isactive, recorded_by)
                    VALUES
                        (@roleId, 'COL-ROLE', 'A', @status, '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { roleId, status });
            }

            case "role_permission_version":
            {
                var roleId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO `role` () VALUES (); SELECT LAST_INSERT_ID();");
                var functionId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO `function` () VALUES (); SELECT LAST_INSERT_ID();");
                var rolePermissionId = await connection.ExecuteScalarAsync<long>(
                    "INSERT INTO `role_permission` () VALUES (); SELECT LAST_INSERT_ID();");
                return async () => await connection.ExecuteAsync(
                    """
                    INSERT INTO role_permission_version
                        (role_permission_id, role_id, function_id, scope_level, status,
                         effective_from, effective_to, isactive, recorded_by)
                    VALUES
                        (@rolePermissionId, @roleId, @functionId, 4, @status,
                         '2020-01-01', '9999-12-31', 0, 'tester')
                    """,
                    new { rolePermissionId, roleId, functionId, status });
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(table), table, "no status seed is defined for this table");
        }
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
