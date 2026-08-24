namespace AST.Core.Security;

// Asymmetric signature for File A/B (spec §2). Verify uses the embedded public key; Sign/KeyMatches need the root admin's private key.
[SharedComponent]
public interface IConfigSignature
{
    bool Verify(byte[] data, byte[] signature);
    byte[] Sign(byte[] data, byte[] privateKeyPkcs8, string passphrase);
    bool KeyMatches(byte[] privateKeyPkcs8, string passphrase); // does the private key match the embedded public key? (invariant B1/B8)
}
