using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AST.Modules.IAM.Tests.TestSupport;

// Reads the test connection string from mysql.secrets.json (repo root, gitignored) or falls back to an env var.
// No configuration found -> returns null; the test caller must SKIP (Assert.SkipUnless), never silently pass.
//
// The two "no green result" outcomes are deliberately DIFFERENT, and must stay that way:
//   * NOT configured at all      -> Skipped. This machine simply has no test DB; nothing was verified and
//                                   the report says so out loud.
//   * configured but unreachable -> FAIL, with the MySqlException surfaced. A wrong password, a dead server
//                                   or a broken migration is a real defect and must never look like "no DB".
// Collapsing the two is what let the whole IAM integration suite report Passed while running nothing.
internal static class TestDatabase
{
    // Single home of the skip reason -- shared by every test class that needs the real DB.
    private const string NoDbConfiguredReason =
        "No test database configured on this machine: set the AST_TEST_CONNECTION_STRING environment variable, " +
        "or add mysql.secrets.json (ConnectionStrings:AstTest) above the test output directory. " +
        "A configured-but-unreachable database FAILS instead of skipping -- that is a real defect, not a missing setup.";

    // Single home of the guard too: every test needing the real DB calls this FIRST. Reports a truthful
    // Skipped -- never an early `return`, which xUnit reports as Passed and which is how this suite once
    // claimed green while verifying nothing.
    // [DoesNotReturnIf(false)] forwards xUnit's own annotation, so `cs is not null` still narrows at the
    // call site; without it the caller's `string?` stays nullable and CS8604 breaks the 0-warning build.
    public static void SkipUnlessAvailable([DoesNotReturnIf(false)] bool available) =>
        Assert.SkipUnless(available, NoDbConfiguredReason);

    private const string EnvVarName = "AST_TEST_CONNECTION_STRING";
    private const string SecretsFileName = "mysql.secrets.json";
    private const string MigrationsDirName = "migrations";

    public static string? TryGetConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var secretsPath = FindUpwards(SecretsFileName, isDirectory: false);
        if (secretsPath is null)
        {
            return null;
        }

        using var stream = File.OpenRead(secretsPath);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connStrings) &&
            connStrings.TryGetProperty("AstTest", out var astTest))
        {
            return astTest.GetString();
        }

        return null;
    }

    // Only meaningful once a connection string EXISTS: at that point the machine is configured, so a missing
    // migrations directory is a broken setup, not an absent one -- hence it throws rather than returning null.
    public static string RequireMigrationsDirectory() =>
        FindUpwards(MigrationsDirName, isDirectory: true)
        ?? throw new InvalidOperationException(
            $"A test database IS configured, but the '{MigrationsDirName}' directory was not found in any parent of " +
            $"{AppContext.BaseDirectory}. Configured-but-broken must fail loudly, never masquerade as 'no database'.");

    private static string? FindUpwards(string name, bool isDirectory)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            var exists = isDirectory ? Directory.Exists(candidate) : File.Exists(candidate);
            if (exists)
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
