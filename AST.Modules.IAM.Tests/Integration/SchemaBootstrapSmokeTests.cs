using AST.Core.Startup;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Tests.TestSupport;
using Dapper;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// B1 smoke test: the fixture cleanly DROPs the ast_test schema -> runs all 10 migration scripts -> checks
// the DB structure via information_schema + ISchemaVersionChecker. Needs a real DB (mysql.secrets.json or
// env var AST_TEST_CONNECTION_STRING): NOT configured -> Skipped; configured but broken -> FAILS loudly.
// [Collection] B2: several other B2 test classes also DROP+reapply `ast_test` -> grouped into 1 collection
// so xUnit does not run 2 classes touching the real DB in parallel (see AstTestDatabaseCollection).
[Collection(TestSupport.AstTestDatabaseCollection.Name)]
public sealed class SchemaBootstrapSmokeTests : IAsyncLifetime
{
    private const int CurrentSchemaVersion = 10;

    private static readonly string[] ExpectedTables =
    [
        "org_unit", "org_unit_version",
        "role", "role_version",
        "function", "function_version",
        "user", "user_version",
        "role_permission", "role_permission_version",
        "schema_version", "app_control", "audit_log",
    ];

    private readonly string? _connectionString = TestDatabase.TryGetConnectionString();
    private bool _dbAvailable;

    public async ValueTask InitializeAsync()
    {
        if (_connectionString is null)
        {
            _dbAvailable = false;
            return;
        }

        // Deliberately NOT wrapped in a try/catch: a configured-but-unreachable DB, a missing migrations
        // directory, or a migration script that actually fails must FAIL this suite loudly. Only the
        // "not configured at all" case above skips.
        var migrationsDir = TestDatabase.RequireMigrationsDirectory();

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await MigrationRunner.DropAllTablesAsync(connection);
        await MigrationRunner.ApplyAllAsync(connection, migrationsDir);
        _dbAvailable = true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Migration_CreatesExactly13Tables_WithExpectedNames()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var actual = (await connection.QueryAsync<string>(
            "SELECT TABLE_NAME FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE()")).ToList();

        Assert.Equal(ExpectedTables.Length, actual.Count);
        foreach (var expected in ExpectedTables)
        {
            Assert.Contains(expected, actual);
        }
    }

    [Theory]
    [InlineData("org_unit_version")]
    [InlineData("role_version")]
    [InlineData("function_version")]
    [InlineData("user_version")]
    [InlineData("role_permission_version")]
    public async Task VersionTable_HasIdPrimaryKey_AndResolutionIndex(string table)
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();

        var pkColumn = await GetPrimaryKeyColumnAsync(connection, table);
        Assert.Equal("id", pkColumn);

        var indexPrefix = table switch
        {
            "org_unit_version" => "idx_ouv_res",
            "role_version" => "idx_rv_res",
            "function_version" => "idx_fv_res",
            "user_version" => "idx_uv_res",
            "role_permission_version" => "idx_rpv_res",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        var identityColumn = table switch
        {
            "org_unit_version" => "org_unit_id",
            "role_version" => "role_id",
            "function_version" => "function_id",
            "user_version" => "user_id",
            "role_permission_version" => "role_permission_id",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };

        var indexColumns = await GetIndexColumnsAsync(connection, table, indexPrefix);
        Assert.Equal([identityColumn, "isactive", "effective_from", "effective_to"], indexColumns);
    }

    [Fact]
    public async Task OrgUnitVersion_HasParentSelfReferenceFk_AndCheckConstraint()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var fks = await GetForeignKeysAsync(connection, "org_unit_version");

        Assert.Contains(fks, fk => fk.Column == "org_unit_id" && fk.ReferencedTable == "org_unit");
        Assert.Contains(fks, fk => fk.Column == "parent_id" && fk.ReferencedTable == "org_unit");
        Assert.True(await HasCheckConstraintAsync(connection, "org_unit_version", "chk_ouv_period"));
    }

    [Fact]
    public async Task UserVersion_HasTemporalFkParentEdges_ToOrgUnitAndRole()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var fks = await GetForeignKeysAsync(connection, "user_version");

        Assert.Contains(fks, fk => fk.Column == "user_id" && fk.ReferencedTable == "user");
        Assert.Contains(fks, fk => fk.Column == "org_unit_id" && fk.ReferencedTable == "org_unit");
        Assert.Contains(fks, fk => fk.Column == "role_id" && fk.ReferencedTable == "role");
    }

    [Fact]
    public async Task UserHeader_HasWriteOnceUniqueSid()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var hasUniqueSid = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'user'
              AND CONSTRAINT_NAME = 'uq_user_sid' AND CONSTRAINT_TYPE = 'UNIQUE'
            """);

        Assert.Equal(1, hasUniqueSid);
    }

    [Fact]
    public async Task FunctionVersion_HasEpochDefault_20000101()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var columnDefault = await connection.ExecuteScalarAsync<string>(
            """
            SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'function_version' AND COLUMN_NAME = 'effective_from'
            """);

        Assert.Equal("2000-01-01", columnDefault);
    }

    [Fact]
    public async Task RolePermissionVersion_HasFkToRoleAndFunction_AndScopeCheckConstraint()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var fks = await GetForeignKeysAsync(connection, "role_permission_version");

        Assert.Contains(fks, fk => fk.Column == "role_permission_id" && fk.ReferencedTable == "role_permission");
        Assert.Contains(fks, fk => fk.Column == "role_id" && fk.ReferencedTable == "role");
        Assert.Contains(fks, fk => fk.Column == "function_id" && fk.ReferencedTable == "function");
        Assert.True(await HasCheckConstraintAsync(connection, "role_permission_version", "chk_rpv_scope"));
    }

    [Fact]
    public async Task SchemaVersion_IsSeeded_UpTo10_WithVersionAsPrimaryKey()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        await using var connection = await OpenAsync();
        var pkColumn = await GetPrimaryKeyColumnAsync(connection, "schema_version");
        Assert.Equal("version", pkColumn);

        var maxVersion = await connection.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_version");
        Assert.Equal(CurrentSchemaVersion, maxVersion);

        var rowCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_version");
        Assert.Equal(CurrentSchemaVersion, rowCount);
    }

    [Fact]
    public async Task SchemaVersionChecker_ReportsMatch_WhenExpectedVersionIsCurrent()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        var checker = CreateChecker();
        var result = await checker.CheckAsync(CurrentSchemaVersion, TestContext.Current.CancellationToken);

        Assert.True(result.IsMatch);
        Assert.Equal(string.Empty, result.BlockMessage);
    }

    [Fact]
    public async Task SchemaVersionChecker_Blocks_WhenExpectedVersionMismatches()
    {
        TestDatabase.SkipUnlessAvailable(_dbAvailable);

        var checker = CreateChecker();
        var result = await checker.CheckAsync(expectedVersion: 999, TestContext.Current.CancellationToken);

        Assert.False(result.IsMatch);
        Assert.NotEqual(string.Empty, result.BlockMessage);
        Assert.Contains("CHẶN", result.BlockMessage);
    }

    private ISchemaVersionChecker CreateChecker()
    {
        var factory = new AST.Infrastructure.MySqlConnectionFactory(new FixedConnectionStringProvider(_connectionString!));
        return new SchemaVersionChecker(factory);
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static Task<string?> GetPrimaryKeyColumnAsync(MySqlConnection connection, string table) =>
        connection.ExecuteScalarAsync<string?>(
            """
            SELECT k.COLUMN_NAME
            FROM information_schema.TABLE_CONSTRAINTS t
            JOIN information_schema.KEY_COLUMN_USAGE k
              ON t.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND t.TABLE_SCHEMA = k.TABLE_SCHEMA AND t.TABLE_NAME = k.TABLE_NAME
            WHERE t.CONSTRAINT_TYPE = 'PRIMARY KEY' AND t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = @table
            """, new { table });

    private static async Task<List<string>> GetIndexColumnsAsync(MySqlConnection connection, string table, string indexName)
    {
        var rows = await connection.QueryAsync<string>(
            """
            SELECT COLUMN_NAME FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND INDEX_NAME = @indexName
            ORDER BY SEQ_IN_INDEX
            """, new { table, indexName });
        return rows.ToList();
    }

    private sealed record ForeignKeyRow(string Column, string ReferencedTable);

    private static async Task<List<ForeignKeyRow>> GetForeignKeysAsync(MySqlConnection connection, string table)
    {
        var rows = await connection.QueryAsync<ForeignKeyRow>(
            """
            SELECT COLUMN_NAME AS `Column`, REFERENCED_TABLE_NAME AS ReferencedTable
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND REFERENCED_TABLE_NAME IS NOT NULL
            """, new { table });
        return rows.ToList();
    }

    private static Task<long> HasCheckConstraintCountAsync(MySqlConnection connection, string table, string constraintName) =>
        connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
              AND CONSTRAINT_NAME = @constraintName AND CONSTRAINT_TYPE = 'CHECK'
            """, new { table, constraintName });

    private static async Task<bool> HasCheckConstraintAsync(MySqlConnection connection, string table, string constraintName) =>
        await HasCheckConstraintCountAsync(connection, table, constraintName) == 1;
}
