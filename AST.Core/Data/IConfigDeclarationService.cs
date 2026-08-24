using ErrorOr;

namespace AST.Core.Data;

// Contract for the UI slice (the "Sign & Save" button): writes File A; the first save creates File B
// recording the root admin (spec §2.1/§3).
[SharedComponent]
public interface IConfigDeclarationService
{
    ErrorOr<Success> SaveConnection(ConnectionFields fields, byte[]? privateKey, string? passphrase);

    // Reads current File A for the re-declaration pre-fill (spec §4.2). Propagates the store's fail-closed errors.
    ErrorOr<ConnectionFields> GetCurrent();

    // Past declaration events (newest first), mapped from the config-audit log. Never includes the password.
    ErrorOr<IReadOnlyList<ConnectionHistoryEntry>> GetHistory();
}
