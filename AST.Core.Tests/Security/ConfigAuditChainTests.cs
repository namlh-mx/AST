using AST.Core.Security;

namespace AST.Core.Tests.Security;

public class ConfigAuditChainTests
{
    private static ConfigAuditContent Content(int seq, string prev, string action = "Update") =>
        new(seq, "2026-07-12T00:00:00Z", new ConfigAuditActor("alice", "PC01"),
            "FileB", action, new ConfigAuditDiff(new[] { "bob" }, Array.Empty<string>()),
            "Success", null, "abc123", prev);

    [Fact]
    public void ComputeHash_is_deterministic_and_hex_sha256()
    {
        var c = Content(1, ConfigAuditChain.GenesisPrevHash);
        Assert.Equal(ConfigAuditChain.ComputeHash(c), ConfigAuditChain.ComputeHash(c));
        Assert.Equal(64, ConfigAuditChain.ComputeHash(c).Length);
    }

    [Fact]
    public void ComputeHash_changes_when_any_field_changes()
    {
        var a = ConfigAuditChain.ComputeHash(Content(1, ConfigAuditChain.GenesisPrevHash));
        var b = ConfigAuditChain.ComputeHash(Content(2, ConfigAuditChain.GenesisPrevHash));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FirstBrokenSeq_null_for_intact_chain()
    {
        var c1 = Content(1, ConfigAuditChain.GenesisPrevHash);
        var r1 = new ConfigAuditRecord(c1, ConfigAuditChain.ComputeHash(c1), null);
        var c2 = Content(2, r1.Hash);
        var r2 = new ConfigAuditRecord(c2, ConfigAuditChain.ComputeHash(c2), null);
        Assert.Null(ConfigAuditChain.FirstBrokenSeq(new[] { r1, r2 }));
    }

    [Fact]
    public void FirstBrokenSeq_detects_edited_middle_record()
    {
        var c1 = Content(1, ConfigAuditChain.GenesisPrevHash);
        var r1 = new ConfigAuditRecord(c1, ConfigAuditChain.ComputeHash(c1), null);
        var tampered = new ConfigAuditRecord(Content(2, r1.Hash, action: "Restore"), r1.Hash /* stale hash */, null);
        Assert.Equal(2, ConfigAuditChain.FirstBrokenSeq(new[] { r1, tampered }));
    }

    [Fact]
    public void FirstBrokenSeq_detects_broken_prev_link()
    {
        var c1 = Content(1, ConfigAuditChain.GenesisPrevHash);
        var r1 = new ConfigAuditRecord(c1, ConfigAuditChain.ComputeHash(c1), null);
        var c2 = Content(2, "deadbeef"); // wrong prevHash
        var r2 = new ConfigAuditRecord(c2, ConfigAuditChain.ComputeHash(c2), null);
        Assert.Equal(2, ConfigAuditChain.FirstBrokenSeq(new[] { r1, r2 }));
    }

    [Fact]
    public void CanonicalBytes_omits_snapshot_key_when_null_so_old_records_hash_unchanged()
    {
        var content = new ConfigAuditContent(
            1, "2026-07-15T00:00:00.0000000Z", new ConfigAuditActor("me", "PC"),
            "FileB", "Create", null, "Success", null, null, ConfigAuditChain.GenesisPrevHash);
        var json = System.Text.Encoding.UTF8.GetString(ConfigAuditChain.CanonicalBytes(content));
        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalBytes_includes_snapshot_when_present_and_chain_is_deterministic()
    {
        var snap = new ConfigConnectionSnapshot("db.local", 3306, "ast_db", "ast_app");
        var content = new ConfigAuditContent(
            1, "2026-07-15T00:00:00.0000000Z", new ConfigAuditActor("me", "PC"),
            "FileA", "Update", null, "Success", null, null, ConfigAuditChain.GenesisPrevHash, snap);
        var json = System.Text.Encoding.UTF8.GetString(ConfigAuditChain.CanonicalBytes(content));
        Assert.Contains("\"snapshot\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConfigAuditChain.ComputeHash(content), ConfigAuditChain.ComputeHash(content)); // deterministic

        // A different snapshot must change the hash (proves Snapshot actually feeds the canonical bytes).
        var otherSnap = new ConfigConnectionSnapshot("other.local", 3306, "ast_db", "ast_app");
        var contentOtherSnap = content with { Snapshot = otherSnap };
        Assert.NotEqual(ConfigAuditChain.ComputeHash(content), ConfigAuditChain.ComputeHash(contentOtherSnap));

        // No snapshot key when null, preserved as the backward-compat proof alongside the snapshot-bearing case.
        var contentNoSnap = content with { Snapshot = null };
        var jsonNoSnap = System.Text.Encoding.UTF8.GetString(ConfigAuditChain.CanonicalBytes(contentNoSnap));
        Assert.DoesNotContain("snapshot", jsonNoSnap, StringComparison.OrdinalIgnoreCase);

        // Appending the same content twice into a 2-record chain links correctly.
        var r1 = new ConfigAuditRecord(content, ConfigAuditChain.ComputeHash(content), null);
        var c2 = content with { Seq = 2, PrevHash = r1.Hash };
        var r2 = new ConfigAuditRecord(c2, ConfigAuditChain.ComputeHash(c2), null);
        Assert.Null(ConfigAuditChain.FirstBrokenSeq(new[] { r1, r2 }));
    }
}
