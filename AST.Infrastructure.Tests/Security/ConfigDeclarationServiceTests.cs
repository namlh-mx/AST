using AST.Core.Data;
using AST.Core.Iam;
using AST.Core.Security;
using AST.Infrastructure.Security;
using ErrorOr;

namespace AST.Infrastructure.Tests.Security;

public class ConfigDeclarationServiceTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-decl-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;
    private readonly EcdsaConfigSignature _sig;
    private readonly string _pub;
    private readonly byte[] _priv;
    private static readonly ConnectionFields Sample = new("db.local", 3306, "ast_db", "ast_app", "p@ss");

    private sealed class StubUser(string? name) : ICurrentWindowsUser
    {
        public string? Username { get; } = name;
    }

    private sealed class FixedClock : AST.Core.Time.IClock
    { public DateTime UtcNow => new(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc); }

    // A config-audit stub whose Read returns a supplied record list (for GetHistory filter/map tests).
    private sealed class StubReadAudit : IConfigAuditLog
    {
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> ReadResult = new List<ConfigAuditRecord>();
        public ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase) => Result.Success;
        public ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read() => ReadResult;
        public ErrorOr<ConfigAuditIntegrity> VerifyIntegrity() => new ConfigAuditIntegrity(true, null, true);
    }

    private static ConfigAuditRecord Rec(int seq, string target, string action, ConfigConnectionSnapshot? snap)
    {
        var c = new ConfigAuditContent(seq, "2026-07-15T00:00:00Z", new ConfigAuditActor("me", "PC"),
            target, action, null, "Success", null, null, ConfigAuditChain.GenesisPrevHash, snap);
        return new ConfigAuditRecord(c, ConfigAuditChain.ComputeHash(c), null);
    }

    // Records every Append so a test can assert which lifecycle events were emitted, in order.
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

    public ConfigDeclarationServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
        (_pub, _priv) = EcdsaKeys.Generate(Pass);
        _sig = new EcdsaConfigSignature(_pub);
    }

    // Debug-mode style: requireSignature=false -> saves unsigned, no key needed (matches the F5 dev flow).
    private (IConnectionConfigStore conn, IBreakGlassStore bg) Stores()
        => (new FileConnectionConfigStore(_sig, new AesConfigProtector(), _paths, requireSignature: false),
            new FileBreakGlassStore(_sig, _paths, requireSignature: false));

    private ConfigDeclarationService NewService(string? user, IConfigAuditLog audit)
    {
        var (conn, bg) = Stores();
        return new ConfigDeclarationService(conn, bg, new StubUser(user), audit);
    }

    [Fact]
    public void SaveConnection_first_run_writes_FileA_and_creates_FileB_with_current_user()
    {
        var (conn, bg) = Stores();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("alice"), new SpyAudit());

        var result = svc.SaveConnection(Sample, privateKey: null, passphrase: null);

        Assert.False(result.IsError);
        Assert.Equal(Sample, conn.Read().Value);
        Assert.Equal(new[] { "alice" }, bg.Read().Value);
    }

    [Fact]
    public void SaveConnection_first_run_emits_FileB_Create_then_FileA_Update()
    {
        var audit = new SpyAudit();
        var svc = NewService("alice", audit);

        var result = svc.SaveConnection(Sample, privateKey: null, passphrase: null);

        Assert.False(result.IsError);
        Assert.Equal(
            new[] { ("FileB", "Create"), ("FileA", "Update") },
            audit.Events.Select(e => (e.Target, e.Action)).ToArray());
    }

    [Fact]
    public void SaveConnection_second_run_emits_only_FileA_Update()
    {
        var audit = new SpyAudit();
        var (conn, bg) = Stores();
        bg.Save(new[] { "existing_admin" }, null, null); // File B already exists
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("someone_else"), audit);

        var result = svc.SaveConnection(Sample, null, null);

        Assert.False(result.IsError);
        Assert.Equal(
            new[] { ("FileA", "Update") },
            audit.Events.Select(e => (e.Target, e.Action)).ToArray());
    }

    [Fact]
    public void SaveConnection_does_not_overwrite_existing_FileB()
    {
        var (conn, bg) = Stores();
        bg.Save(new[] { "existing_admin" }, null, null);
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("someone_else"), new SpyAudit());

        var result = svc.SaveConnection(Sample, null, null);

        Assert.False(result.IsError);
        Assert.Equal(new[] { "existing_admin" }, bg.Read().Value); // File B stays unchanged
    }

    [Fact]
    public void SaveConnection_FileB_corrupt_propagates_error_and_does_not_overwrite()
    {
        var (conn, bg) = Stores();
        const string garbage = "{ rác không phải json hợp lệ";
        Directory.CreateDirectory(_paths.Dir);
        File.WriteAllText(_paths.AdminsFile, garbage);
        var audit = new SpyAudit();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("someone"), audit);

        var result = svc.SaveConnection(Sample, null, null);

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.Validation, result.FirstError.Type);
        Assert.Equal("Config.ContentInvalid", result.FirstError.Code);
        Assert.Equal(garbage, File.ReadAllText(_paths.AdminsFile)); // File B is NOT overwritten
        Assert.True(bg.Read().IsError); // still corrupt, does not silently recover
        Assert.Empty(audit.Events); // a failed save emits no lifecycle event
    }

    [Fact]
    public void SaveConnection_first_run_without_current_user_is_error()
    {
        var (conn, bg) = Stores();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser(null), new SpyAudit());

        var result = svc.SaveConnection(Sample, null, null);

        Assert.True(result.IsError);
        Assert.Equal("Config.CurrentUserUnknown", result.FirstError.Code);
    }

    [Fact]
    public void GetCurrent_returns_saved_fields_when_FileA_exists()
    {
        var (conn, bg) = Stores();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("admin1"), new SpyAudit());
        svc.SaveConnection(Sample, privateKey: null, passphrase: null);

        var result = svc.GetCurrent();

        Assert.False(result.IsError);
        Assert.Equal(Sample, result.Value);
    }

    [Fact]
    public void GetCurrent_propagates_NotDeclared_when_FileA_absent()
    {
        var (conn, bg) = Stores();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("admin1"), new SpyAudit());

        var result = svc.GetCurrent();

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.NotFound, result.FirstError.Type);
        Assert.Equal("Config.NotDeclared", result.FirstError.Code);
    }

    [Fact]
    public void SaveConnection_records_snapshot_without_password()
    {
        var audit = new SpyAudit();
        var svc = NewService("me", audit);
        svc.SaveConnection(Sample, privateKey: null, passphrase: null);
        var fileA = audit.Events.Last(e => e.Target == "FileA" && e.Action == "Update");
        Assert.Equal(new ConfigConnectionSnapshot("db.local", 3306, "ast_db", "ast_app"), fileA.Snapshot);
    }

    [Fact]
    public void SaveConnection_written_log_contains_fields_but_not_password()
    {
        var log = new FileConfigAuditLog(_sig, _paths, new StubUser("me"), new FixedClock(), _pub);
        var (conn, bg) = Stores();
        var svc = new ConfigDeclarationService(conn, bg, new StubUser("me"), log);
        svc.SaveConnection(Sample, privateKey: null, passphrase: null);
        var text = File.ReadAllText(_paths.AuditFile);
        Assert.Contains("db.local", text);
        Assert.DoesNotContain("p@ss", text);   // password never written to the log
    }

    [Fact]
    public void GetHistory_returns_declaration_entries_newest_first_without_password()
    {
        var audit = new StubReadAudit
        {
            ReadResult = new List<ConfigAuditRecord>
            {
                Rec(1, "FileA", "Update", new ConfigConnectionSnapshot("h1", 3306, "d1", "u1")),
                Rec(2, "FileA", "Update", new ConfigConnectionSnapshot("h2", 3307, "d2", "u2")),
            }
        };
        var svc = NewService("me", audit);
        var hist = svc.GetHistory().Value;
        Assert.Equal(2, hist.Count);
        Assert.Equal("h2", hist[0].Host);   // newest first
        Assert.Equal(3306, hist[1].Port);
        Assert.Equal("u1", hist[1].DbUser);
    }

    [Fact]
    public void GetHistory_excludes_records_without_a_snapshot()
    {
        var audit = new StubReadAudit
        {
            ReadResult = new List<ConfigAuditRecord>
            {
                Rec(1, "FileB", "Create", null),   // no snapshot
                Rec(2, "FileA", "Update", new ConfigConnectionSnapshot("h1", 3306, "d1", "u1")),
            }
        };
        var hist = NewService("me", audit).GetHistory().Value;
        Assert.Single(hist);
        Assert.Equal("h1", hist[0].Host);
    }

    [Fact]
    public void GetHistory_fails_clear_when_log_unreadable()
    {
        var audit = new StubReadAudit { ReadResult = Error.Validation("Config.ContentInvalid", "hỏng") };
        var result = NewService("me", audit).GetHistory();
        Assert.True(result.IsError);
        Assert.Equal("Config.ContentInvalid", result.FirstError.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
