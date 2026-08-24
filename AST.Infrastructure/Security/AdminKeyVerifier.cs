using System.Security.Cryptography;
using AST.Core.Security;
using ErrorOr;

namespace AST.Infrastructure.Security;

public sealed class AdminKeyVerifier(IConfigSignature signature) : IAdminKeyVerifier
{
    public ErrorOr<Success> Verify(byte[] privateKey, string? passphrase)
    {
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(32);
            var sig = signature.Sign(nonce, privateKey, passphrase ?? string.Empty);
            return signature.Verify(nonce, sig) ? Result.Success : ConfigErrors.KeyMismatch();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return ConfigErrors.KeyUnreadable();
        }
    }
}
