namespace AST.Shell.Session;

// In-memory holder for the verified admin key+passphrase (spec §4.3). Lives ONLY in the running process's
// memory on the admin's machine, never persisted. Registered singleton at the composition root.
public interface IAdminSession
{
    bool IsAuthenticated { get; }
    byte[]? PrivateKey { get; }
    string? Passphrase { get; }
    // privateKey may be null on the Debug "skip" path (RequireConfigSignature=false) — still marks authenticated.
    void Authenticate(byte[]? privateKey, string? passphrase);
    void Clear();
    event EventHandler? Changed;
}
