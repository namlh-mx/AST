using System.Security.Cryptography;
using AST.Core.Security;

namespace AST.Infrastructure.Security;

public sealed class AesConfigProtector : IConfigProtector
{
    // Obfuscation key embedded in the app (NOT a real secret). 32 bytes = AES-256.
    private static readonly byte[] Key =
    {
        0x41,0x53,0x54,0x2D,0x63,0x66,0x67,0x2D,0x6F,0x62,0x66,0x75,0x73,0x63,0x61,0x74,
        0x65,0x2D,0x6B,0x65,0x79,0x2D,0x76,0x31,0x2D,0x33,0x32,0x62,0x79,0x74,0x65,0x73
    };

    public byte[] Protect(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var body = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        var result = new byte[aes.IV.Length + body.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);         // 16-byte IV prepended
        Buffer.BlockCopy(body, 0, result, aes.IV.Length, body.Length);
        return result;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        var iv = new byte[16];
        if (ciphertext.Length < iv.Length) throw new CryptographicException("Ciphertext too short.");
        Buffer.BlockCopy(ciphertext, 0, iv, 0, iv.Length);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(ciphertext, iv.Length, ciphertext.Length - iv.Length);
    }
}
