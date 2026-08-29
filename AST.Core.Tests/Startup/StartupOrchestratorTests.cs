using AST.Core.Data;
using AST.Core.Security;
using AST.Core.Startup;
using ErrorOr;
using FluentAssertions;

namespace AST.Core.Tests.Startup;

public class StartupOrchestratorTests
{
    private static readonly ConnectionFields Sample = new("db.local", 3306, "ast_db", "ast_app", "p@ss");

    private sealed class StubConfigStore(ErrorOr<ConnectionFields> result, Exception? throwOnRead = null)
        : IConnectionConfigStore
    {
        public ErrorOr<ConnectionFields> Read() => throwOnRead is null ? result : throw throwOnRead;
        public ErrorOr<Success> Save(ConnectionFields fields, byte[]? privateKey, string? passphrase)
            => throw new NotSupportedException("Không dùng trong test orchestrator.");
    }

    private sealed class StubConnectionTester(ErrorOr<Success> result, Exception? throwOnTest = null)
        : IConnectionTester
    {
        public ErrorOr<Success> Test(ConnectionFields fields) => throwOnTest is null ? result : throw throwOnTest;
    }

    // Records Append so tests can assert whether a SignatureVerifyFailed event was emitted at startup.
    private sealed class SpyAudit : IConfigAuditLog
    {
        public List<ConfigAuditEvent> Events { get; } = new();
        public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase)
        {
            Events.Add(evt);
            return Result.Success;
        }
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read() => Array.Empty<ConfigAuditRecord>();
        public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity() => new ConfigAuditIntegrity(true, null, true);
    }

    // audit is the LAST ctor parameter (added by Task 5); tests that do not care pass a throwaway spy.
    private static StartupOrchestrator Orchestrator(
        IConnectionConfigStore store,
        IConnectionTester tester,
        Func<SchemaCheckOutcome> checkSchema,
        Action<Exception> onUnexpectedError,
        IConfigAuditLog audit)
        => new(store, tester, checkSchema, onUnexpectedError, audit);

    [Fact]
    public void Ok_reachable_schema_match_is_Connected()
    {
        var orchestrator = Orchestrator(
            new StubConfigStore(Sample),
            new StubConnectionTester(Result.Success),
            () => new SchemaCheckOutcome(true, string.Empty),
            _ => throw new Exception("Không được gọi khi thành công."),
            new SpyAudit());

        var status = orchestrator.Resolve();

        Assert.Equal(StartupMode.Connected, status.Mode);
        Assert.Equal("Startup.Ready", status.Reason);
    }

    [Fact]
    public void NotFound_error_maps_to_Config_NotDeclared_and_skips_connection_test()
    {
        // checkSchema is only called if outcome==Ok && dbReachable -- throwing here PROVES
        // the NotDeclared branch truly skips the DB-connect/schema-check step (does not fall into Startup.Unexpected).
        var orchestrator = Orchestrator(
            new StubConfigStore(Error.NotFound("Config.NotDeclared", "Chưa khai báo.")),
            new StubConnectionTester(Result.Success),
            () => throw new Exception("Không được gọi khi chưa có File A."),
            _ => throw new Exception("Không được gọi -- đây không phải nhánh lỗi bất ngờ."),
            new SpyAudit());

        var status = orchestrator.Resolve();

        Assert.Equal(StartupMode.NotConnected, status.Mode);
        Assert.Equal("Config.NotDeclared", status.Reason);
    }

    [Fact]
    public void Validation_error_maps_to_Config_Corrupt()
    {
        var orchestrator = Orchestrator(
            new StubConfigStore(Error.Validation("Config.Corrupt", "Tệp bị hỏng.")),
            new StubConnectionTester(Result.Success),
            () => throw new Exception("Không được gọi."),
            _ => throw new Exception("Không được gọi."),
            new SpyAudit());

        Assert.Equal("Config.Corrupt", orchestrator.Resolve().Reason);
    }

    [Fact]
    public void SignatureInvalid_emits_exactly_one_SignatureVerifyFailed_audit_event()
    {
        var audit = new SpyAudit();
        var orchestrator = Orchestrator(
            new StubConfigStore(ConfigErrors.SignatureInvalid("File A")),
            new StubConnectionTester(Result.Success),
            () => throw new Exception("Không được gọi."),
            _ => throw new Exception("Không được gọi."),
            audit);

        var status = orchestrator.Resolve();

        // startup mode is unchanged (still the corrupt/NotConnected branch)
        Assert.Equal(StartupMode.NotConnected, status.Mode);
        // exactly one audit event, with the expected shape (no key at startup -> best-effort, no tipSig)
        Assert.Single(audit.Events);
        var evt = audit.Events[0];
        Assert.Equal("FileA", evt.Target);
        Assert.Equal("SignatureVerifyFailed", evt.Action);
        Assert.Equal("Failure", evt.Result);
        Assert.Equal("Config.SignatureInvalid", evt.Reason);
    }

    [Fact]
    public void ContentInvalid_does_not_emit_a_signature_verify_failed_event()
    {
        var audit = new SpyAudit();
        var orchestrator = Orchestrator(
            new StubConfigStore(ConfigErrors.ContentInvalid("File A")),
            new StubConnectionTester(Result.Success),
            () => throw new Exception("Không được gọi."),
            _ => throw new Exception("Không được gọi."),
            audit);

        orchestrator.Resolve();

        Assert.Empty(audit.Events); // only Config.SignatureInvalid triggers the emit, not generic content corruption
    }

    [Fact]
    public void NotDeclared_does_not_emit_an_audit_event()
    {
        var audit = new SpyAudit();
        var orchestrator = Orchestrator(
            new StubConfigStore(Error.NotFound("Config.NotDeclared", "Chưa khai báo.")),
            new StubConnectionTester(Result.Success),
            () => throw new Exception("Không được gọi."),
            _ => throw new Exception("Không được gọi."),
            audit);

        orchestrator.Resolve();

        Assert.Empty(audit.Events);
    }

    [Fact]
    public void ConfigStore_Read_throws_is_caught_and_reported_as_Startup_Unexpected()
    {
        var thrown = new InvalidOperationException("boom-read");
        Exception? captured = null;
        var orchestrator = Orchestrator(
            new StubConfigStore(Sample, throwOnRead: thrown),
            new StubConnectionTester(Result.Success),
            () => new SchemaCheckOutcome(true, string.Empty),
            ex => captured = ex,
            new SpyAudit());

        var status = orchestrator.Resolve();

        status.Mode.Should().Be(StartupMode.NotConnected);
        status.Reason.Should().Be("Startup.Unexpected");
        status.Message.Should().Be("Lỗi hệ thống, người dùng thử lại sau hoặc liên hệ quản trị viên.");
        Assert.Same(thrown, captured);
    }

    [Fact]
    public void ConnectionTester_Test_throws_is_caught_and_reported_as_Startup_Unexpected()
    {
        var thrown = new InvalidOperationException("boom-test");
        Exception? captured = null;
        var orchestrator = Orchestrator(
            new StubConfigStore(Sample),
            new StubConnectionTester(Result.Success, throwOnTest: thrown),
            () => new SchemaCheckOutcome(true, string.Empty),
            ex => captured = ex,
            new SpyAudit());

        var status = orchestrator.Resolve();

        Assert.Equal(StartupMode.NotConnected, status.Mode);
        Assert.Equal("Startup.Unexpected", status.Reason);
        Assert.Same(thrown, captured);
    }

    [Fact]
    public void CheckSchema_throws_is_caught_and_reported_as_Startup_Unexpected()
    {
        var thrown = new InvalidOperationException("boom-schema");
        Exception? captured = null;
        var orchestrator = Orchestrator(
            new StubConfigStore(Sample),
            new StubConnectionTester(Result.Success),
            () => throw thrown,
            ex => captured = ex,
            new SpyAudit());

        var status = orchestrator.Resolve();

        Assert.Equal(StartupMode.NotConnected, status.Mode);
        Assert.Equal("Startup.Unexpected", status.Reason);
        Assert.Same(thrown, captured);
    }
}
