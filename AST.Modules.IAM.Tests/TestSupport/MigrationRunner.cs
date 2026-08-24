using MySqlConnector;

namespace AST.Modules.IAM.Tests.TestSupport;

// Helper used ONLY for integration tests: applies the migrations/V*.sql scripts in order on 1 connection.
// A real DBA does NOT use this — they run it by hand via DBeaver in the correct file order (per the B1 requirement).
internal static class MigrationRunner
{
    public static async Task ApplyAllAsync(MySqlConnection connection, string migrationsDirectory, CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(migrationsDirectory, "V*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"Không tìm thấy migration script nào trong '{migrationsDirectory}'.");
        }

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            await using var command = connection.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- whole migration script from the repo; DDL cannot be parameterized.
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // Cleanly DROPs the existing schema before rerunning migrations from scratch (idempotent for integration tests).
    public static async Task DropAllTablesAsync(MySqlConnection connection, CancellationToken cancellationToken = default)
    {
        var tableNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT TABLE_NAME FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE()";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        if (tableNames.Count == 0)
        {
            return;
        }

        // Table names come from information_schema of the current database, never from user input. Identifiers
        // cannot be parameterized, so backticks are escaped by doubling them per MySQL's identifier-quoting rule.
        var dropStatements = string.Join(
            string.Empty,
            tableNames.Select(t => $"DROP TABLE IF EXISTS `{t.Replace("`", "``")}`;"));
        await using var dropCommand = connection.CreateCommand();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- identifiers escaped above; SqlParameter cannot bind table names.
        dropCommand.CommandText = $"SET FOREIGN_KEY_CHECKS=0;{dropStatements}SET FOREIGN_KEY_CHECKS=1;";
        await dropCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
