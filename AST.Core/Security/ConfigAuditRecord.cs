using System.Text.Json.Serialization;

namespace AST.Core.Security;

public sealed record ConfigAuditActor(string User, string Machine);
public sealed record ConfigAuditDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);
// What a FileA declaration record remembers about the connection, so the screen can list past
// configurations and offer to reuse one. NEVER add the password: this log is a plain file on a share that
// every workstation reads, it is append-only, and its whole point is to be readable for audit — a secret in
// here could not be revoked or redacted afterwards. Reuse deliberately refills the 4 fields below and makes
// the operator retype the password.
public sealed record ConfigConnectionSnapshot(string Host, int Port, string Database, string User);

// Everything the hash covers (excludes Hash + TipSig by construction).
public sealed record ConfigAuditContent(
    int Seq, string TsUtc, ConfigAuditActor Actor, string Target, string Action,
    ConfigAuditDiff? Diff, string Result, string? Reason, string? KeyFingerprint, string PrevHash,
    // Present only on FileA declaration records; omitted-when-null keeps pre-existing records' canonical bytes
    // (and therefore their Hash + TipSig) unchanged. Do NOT set DefaultIgnoreCondition on the serializer.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConfigConnectionSnapshot? Snapshot = null);

// One stored line = content + its Hash + optional TipSig (present only on key-signed Save records).
public sealed record ConfigAuditRecord(ConfigAuditContent Content, string Hash, string? TipSig);

// A request to append; the writer fills Seq/TsUtc/Actor/KeyFingerprint/PrevHash/Hash/TipSig.
public sealed record ConfigAuditEvent(
    string Target, string Action, ConfigAuditDiff? Diff, string Result, string? Reason,
    ConfigConnectionSnapshot? Snapshot = null);

public sealed record ConfigAuditIntegrity(bool ChainValid, int? FirstBrokenSeq, bool TipSignatureValid);
