using System.Security.Cryptography;
using System.Text.Json;

namespace AST.Core.Security;

// Pure, BCL-only. Canonical bytes are System.Text.Json in the record's declaration order (stable),
// so ComputeHash is deterministic across machines. The stored line adds Hash + TipSig around this.
public static class ConfigAuditChain
{
    public const string GenesisPrevHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.General)
    {
        // camelCase to match the rest of the config JSON; no indenting -> stable bytes.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] CanonicalBytes(ConfigAuditContent content)
        => JsonSerializer.SerializeToUtf8Bytes(content, Canonical);

    public static string ComputeHash(ConfigAuditContent content)
        => Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(content)));

    // Returns the seq of the first record whose Hash is wrong or whose PrevHash does not chain; null = intact.
    public static int? FirstBrokenSeq(IReadOnlyList<ConfigAuditRecord> records)
    {
        var expectedPrev = GenesisPrevHash;
        foreach (var r in records)
        {
            if (r.Content.PrevHash != expectedPrev) return r.Content.Seq;
            if (ComputeHash(r.Content) != r.Hash) return r.Content.Seq;
            expectedPrev = r.Hash;
        }
        return null;
    }
}
