namespace AST.Shell.Session;

public sealed class AdminSession : IAdminSession
{
    public bool IsAuthenticated { get; private set; }
    public byte[]? PrivateKey { get; private set; }
    public string? Passphrase { get; private set; }

    public event EventHandler? Changed;

    public void Authenticate(byte[]? privateKey, string? passphrase)
    {
        PrivateKey = privateKey;
        Passphrase = passphrase;
        IsAuthenticated = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        PrivateKey = null;
        Passphrase = null;
        IsAuthenticated = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
