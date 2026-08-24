using ErrorOr;

namespace AST.Core.Security;

// Share-based, append-only, hash-chained config trust log (spec §3). Writes are fail-clear; a write
// failure must be surfaced, never swallowed. Reading/verifying needs no key; tip-signing needs the key.
[SharedComponent]
public interface IConfigAuditLog
{
    // When privateKey+passphrase are supplied, also signs the new record's Hash (tipSig). Otherwise hash-chain only.
    ErrorOr<Success> Append(ConfigAuditEvent evt, byte[]? privateKey, string? passphrase);
    ErrorOr<IReadOnlyList<ConfigAuditRecord>> Read();
    ErrorOr<ConfigAuditIntegrity> VerifyIntegrity();
}
