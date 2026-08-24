using System.Security.Cryptography;
using AST.Core.Security;

namespace AST.Infrastructure.Security;

public sealed class EcdsaConfigSignature(string publicKeyBase64) : IConfigSignature
{
    public bool Verify(byte[] data, byte[] signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }        // corrupt public-key base64
        catch (CryptographicException) { return false; }  // invalid signature/key -> fail-closed
    }

    public byte[] Sign(byte[] data, byte[] privateKeyPkcs8, string passphrase)
    {
        using var ec = ECDsa.Create();
        ec.ImportEncryptedPkcs8PrivateKey(passphrase, privateKeyPkcs8, out _); // wrong passphrase -> throws (caller catches)
        return ec.SignData(data, HashAlgorithmName.SHA256);
    }

    public bool KeyMatches(byte[] privateKeyPkcs8, string passphrase)
    {
        using var ec = ECDsa.Create();
        ec.ImportEncryptedPkcs8PrivateKey(passphrase, privateKeyPkcs8, out _);
        var derivedPub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        return derivedPub == publicKeyBase64;
    }
}
