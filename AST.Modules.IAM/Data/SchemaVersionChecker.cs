using AST.Core.Data;
using AST.Core.Startup;
using Dapper;
using ILogger = Serilog.ILogger;

namespace AST.Modules.IAM.Data;

public sealed class SchemaVersionChecker(IDbConnectionFactory connectionFactory, ILogger? logger = null)
    : ISchemaVersionChecker
{
    private readonly ILogger _logger = logger ?? Serilog.Log.Logger;

    public async Task<SchemaVersionCheckResult> CheckAsync(int expectedVersion, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            "SELECT MAX(version) FROM schema_version",
            cancellationToken: cancellationToken);
        var actual = await connection.QuerySingleOrDefaultAsync<int?>(command);

        var result = new SchemaVersionCheckResult(expectedVersion, actual);
        if (!result.IsMatch)
        {
            _logger.Warning("Schema version mismatch: {Message}", result.BlockMessage);
        }

        return result;
    }
}
