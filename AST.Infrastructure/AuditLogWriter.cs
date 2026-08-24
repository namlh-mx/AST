using System.Data;
using AST.Core.Data;
using Dapper;
using ErrorOr;

namespace AST.Infrastructure;

// Concrete `audit_log` writer (V008). Holds no connection/factory of its own on purpose -- every write goes
// through the caller-supplied ambient transaction's own connection, so it structurally cannot open a second
// connection (rule-platform-infra: no leaked write outside the ambient transaction).
public sealed class AuditLogWriter : IAuditLogWriter
{
    public async Task<ErrorOr<Success>> WriteAsync(
        AuditLogEntry entry, IDbTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (transaction.Connection is null)
        {
            // Fail clear (rule-platform-infra): a transaction with no live connection is a caller bug, not a
            // silent no-op and not a reason to open a fallback connection of our own.
            return Error.Failure(
                "AuditLogWriter.NoAmbientConnection",
                "Không có connection trên transaction được truyền vào -- audit_log KHÔNG được ghi (writer này không tự mở connection thứ hai).");
        }

        await transaction.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (username, event_type, target, detail)
            VALUES (@Username, @EventType, @Target, @Detail)
            """,
            new
            {
                entry.Username,
                entry.EventType,
                entry.Target,
                Detail = entry.DetailJson,
            },
            transaction: transaction,
            cancellationToken: cancellationToken));

        return Result.Success;
    }
}
