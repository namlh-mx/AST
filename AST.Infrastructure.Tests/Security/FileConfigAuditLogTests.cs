using AST.Core.Security;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class FileConfigAuditLogTests : IDisposable
{
    private const string Pass = "pp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ast-audit-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigPaths _paths;
    private readonly EcdsaConfigSignature _sig;
    private readonly string _pub;
    private readonly byte[] _priv;

    private sealed class FixedUser(string? name) : ICurrentWindowsUser { public string? Username { get; } = name; }
    private sealed class FixedClock : IClock { public DateTime UtcNow => new(2026, 7, 12, 8, 0, 0, DateTimeKind.Utc); }

    public FileConfigAuditLogTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = ConfigPathResolver.Resolve(_dir);
        (_pub, _priv) = EcdsaKeys.Generate(Pass);
        _sig = new EcdsaConfigSignature(_pub);
    }

    private FileConfigAuditLog Log(string? user = "example\\alice") =>
        new(_sig, _paths, new FixedUser(user), new FixedClock(), _pub);

    [Fact]
    public void Append_signed_then_Read_and_Verify_intact()
    {
        var log = Log();
        Assert.False(log.Append(new ConfigAuditEvent("FileB", "Create", null, "Success", null), _priv, Pass).IsError);
        Assert.False(log.Append(new ConfigAuditEvent("FileB", "Update",
            new ConfigAuditDiff(new[] { "bob" }, Array.Empty<string>()), "Success", null), _priv, Pass).IsError);

        var records = log.Read().Value;
        Assert.Equal(2, records.Count);
        Assert.Equal("alice", records[0].Content.Actor.User); // normalized
        Assert.NotNull(records[1].TipSig);

        var integrity = log.VerifyIntegrity().Value;
        Assert.True(integrity.ChainValid);
        Assert.Null(integrity.FirstBrokenSeq);
        Assert.True(integrity.TipSignatureValid);
    }

    [Fact]
    public void SignatureVerifyFailed_record_has_no_tipSig_and_still_chains()
    {
        var log = Log();
        Assert.False(log.Append(new ConfigAuditEvent("FileA", "SignatureVerifyFailed", null, "Failure", "Config.SignatureInvalid"), null, null).IsError);
        var records = log.Read().Value;
        Assert.Null(records[0].TipSig);
        Assert.True(log.VerifyIntegrity().Value.ChainValid);
    }

    [Fact]
    public void Full_rewrite_without_key_fails_tip_signature()
    {
        var log = Log();
        log.Append(new ConfigAuditEvent("FileB", "Update", new ConfigAuditDiff(new[] { "x" }, Array.Empty<string>()), "Success", null), _priv, Pass);
        // Attacker rewrites the whole file with a self-consistent chain but cannot forge a tipSig.
        var forgedContent = new ConfigAuditContent(1, "2026-07-12T00:00:00Z",
            new ConfigAuditActor("attacker", "EVIL"), "FileB", "Update",
            new ConfigAuditDiff(new[] { "attacker" }, Array.Empty<string>()), "Success", null, "ffff", ConfigAuditChain.GenesisPrevHash);
        var forged = new ConfigAuditRecord(forgedContent, ConfigAuditChain.ComputeHash(forgedContent), "AAAA");
        File.WriteAllText(_paths.AuditFile, System.Text.Json.JsonSerializer.Serialize(forged) + "\n");

        var integrity = log.VerifyIntegrity().Value;
        Assert.False(integrity.TipSignatureValid); // chain may parse but the tip signature is invalid
    }

    [Fact]
    public void Read_missing_file_is_empty_not_error()
        => Assert.Empty(Log().Read().Value);

    [Fact]
    public void Append_wrong_passphrase_returns_KeyUnreadable()
    {
        var result = Log().Append(new ConfigAuditEvent("FileB", "Update", null, "Success", null), _priv, "wrong-passphrase");
        Assert.True(result.IsError);
        Assert.Equal("Config.KeyUnreadable", result.FirstError.Code);
    }

    [Fact]
    public void Read_corrupt_line_returns_ContentInvalid_not_crash()
    {
        Directory.CreateDirectory(_paths.AuditDir);
        File.WriteAllText(_paths.AuditFile, "{\"hash\":\"x\"}\n"); // parses to a record with null Content
        var result = Log().Read();
        Assert.True(result.IsError);
        Assert.Equal("Config.ContentInvalid", result.FirstError.Code);
    }

    [Fact]
    public void Malformed_tipSig_fails_verification_without_throwing()
    {
        var content = new ConfigAuditContent(1, "2026-07-12T00:00:00Z",
            new ConfigAuditActor("alice", "PC01"), "FileB", "Update", null, "Success", null, "abc", ConfigAuditChain.GenesisPrevHash);
        var rec = new ConfigAuditRecord(content, ConfigAuditChain.ComputeHash(content), "!!not-base64!!");
        Directory.CreateDirectory(_paths.AuditDir);
        File.WriteAllText(_paths.AuditFile, System.Text.Json.JsonSerializer.Serialize(rec) + "\n");

        var integrity = Log().VerifyIntegrity();
        Assert.False(integrity.IsError);
        Assert.True(integrity.Value.ChainValid);
        Assert.False(integrity.Value.TipSignatureValid);
    }

    [Fact]
    public void Mixed_legacy_and_snapshot_records_still_verify_intact()
    {
        // Load-bearing backward-compat: a real file mixing a pre-snapshot record (FileB Create, no Snapshot)
        // and a new snapshot-carrying record (FileA Update) must still chain + tip-verify — the JsonIgnore
        // -when-null on Snapshot keeps the legacy record's canonical bytes (and thus its hash) unchanged.
        var log = Log();
        Assert.False(log.Append(new ConfigAuditEvent("FileB", "Create", null, "Success", null), _priv, Pass).IsError);
        Assert.False(log.Append(new ConfigAuditEvent("FileA", "Update", null, "Success", null,
            new ConfigConnectionSnapshot("db.local", 3306, "ast_db", "ast_app")), _priv, Pass).IsError);

        var integrity = log.VerifyIntegrity().Value;
        Assert.True(integrity.ChainValid);
        Assert.Null(integrity.FirstBrokenSeq);
        Assert.True(integrity.TipSignatureValid);
        // The new-shape record carries the snapshot; the legacy record must NOT gain a snapshot key.
        var lines = File.ReadAllLines(_paths.AuditFile);
        Assert.DoesNotContain("snapshot", lines[0], StringComparison.OrdinalIgnoreCase); // FileB Create stays legacy-shaped
        Assert.Contains("snapshot", lines[1], StringComparison.OrdinalIgnoreCase);        // FileA Update carries it
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
}
