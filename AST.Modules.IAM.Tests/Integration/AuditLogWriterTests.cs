using System.Data;
using AST.Core.Data;
using AST.Infrastructure;
using Dapper;
using ErrorOr;
using FluentAssertions;
using MySqlConnector;

namespace AST.Modules.IAM.Tests.Integration;

// Pins IAuditLogWriter (AST.Core/Data/IAuditLogWriter.cs, impl AST.Infrastructure/AuditLogWriter.cs):
// scoped ahead of the Role permission-grant/revoke feature (D-2/O2) so it is callable from WITHIN an
// ambient CompositeWrite transaction. Real MySQL, no mocking (rule-testing invariant 1).
public sealed class AuditLogWriterTests : IamRepositoryTestBase
{
    private readonly IAuditLogWriter _writer = new AuditLogWriter();

    [Fact]
    public async Task WriteAsync_GivenAmbientTransaction_WritesOneRowWithTheRightShape()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var entry = new AuditLogEntry(
            EventType: "permission-change",
            Username: "tester",
            Target: "role_permission:42",
            DetailJson: """{"action":"grant","function":"Fx.One"}""");

        var result = await _writer.WriteAsync(entry, transaction, ct);

        result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

        var row = await connection.QuerySingleAsync<AuditLogRow>(new CommandDefinition(
            "SELECT username, event_type AS EventType, target, detail, occurred_at AS OccurredAt FROM audit_log ORDER BY id DESC LIMIT 1",
            transaction: transaction,
            cancellationToken: ct));

        row.Username.Should().Be("tester");
        row.EventType.Should().Be("permission-change");
        row.Target.Should().Be("role_permission:42");
        // MySQL's JSON column type re-serializes (whitespace/key order) on round-trip -- compare parsed
        // structure, not the raw string, which is what the `detail` column contract actually promises.
        System.Text.Json.JsonDocument.Parse(row.Detail!).RootElement.GetProperty("action").GetString()
            .Should().Be("grant");
        row.OccurredAt.Should().NotBe(default);

        await transaction.CommitAsync(ct);
    }

    [Fact]
    public async Task WriteAsync_TransactionLaterRollsBack_AuditRowIsRolledBackToo()
    {
        SkipUnlessDbAvailable();

        var ct = TestContext.Current.CancellationToken;
        // Scoped to a unique target so this stays correct if another test class writes audit_log
        // concurrently -- an unscoped whole-table COUNT(*) would be flaky the moment that happens.
        const string target = "role_permission:rollback-probe";

        await using (var connection = new MySqlConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var entry = new AuditLogEntry("permission-change", "tester", target, null);
            var result = await _writer.WriteAsync(entry, transaction, ct);
            result.IsError.Should().BeFalse(DescribeErrors(result.Errors));

            // Visible WITHIN the same still-open transaction (proves the write went through the ambient
            // connection, not a second one that would have already committed independently).
            var uncommittedCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM audit_log WHERE target = @target",
                new { target }, transaction: transaction, cancellationToken: ct));
            uncommittedCount.Should().Be(1);

            await transaction.RollbackAsync(ct);
        }

        // After rollback, on a FRESH connection, the row must not exist -- no leaked write outside the
        // ambient transaction (rule-platform-infra).
        (await CountAuditRowsForTargetAsync(target, ct)).Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_TransactionHasNoConnection_FailsClearWithoutOpeningAnotherOne()
    {
        // Headless, no DB needed: proves the guard branch fires and no fallback connection is opened.
        var writer = new AuditLogWriter();
        var entry = new AuditLogEntry("login", null, null, null);

        var result = await writer.WriteAsync(entry, new ConnectionlessTransaction(), TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("AuditLogWriter.NoAmbientConnection");
        result.FirstError.Type.Should().Be(ErrorType.Failure);
    }

    private async Task<long> CountAuditRowsForTargetAsync(string target, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_log WHERE target = @target", new { target }, cancellationToken: ct));
    }

    // A minimal, deliberately DB-less IDbTransaction stub -- not a Moq of a persistence seam (forbidden by
    // rule-testing invariant 2), just a plain fake proving the no-connection guard branch.
    private sealed class ConnectionlessTransaction : IDbTransaction
    {
        public IDbConnection? Connection => null;
        public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        public void Commit() => throw new NotSupportedException();
        public void Rollback() => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }

    // Shape mirrors the actual columns (V008) -- matches AuditLogEntry's mapping, not invented fields.
    private sealed record AuditLogRow(string? Username, string EventType, string? Target, string? Detail, DateTime OccurredAt);
}
