using System.Security.Cryptography;

namespace AST.Infrastructure.Security;

// Generates an ECDSA P-256 key pair. Used by AST.ConfigKeyGen (pre-build) and tests. The private key is exported as passphrase-encrypted PKCS#8.
public static class EcdsaKeys
{
    public static (string PublicBase64, byte[] EncryptedPrivatePkcs8) Generate(string passphrase)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var priv = ec.ExportEncryptedPkcs8PrivateKey(passphrase, pbe);
        return (pub, priv);
    }
}
