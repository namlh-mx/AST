using ErrorOr;

namespace AST.Core.Security;

// Gates the admin-auth screen (spec §4.1): proves the supplied private key + passphrase can produce
// signatures the app's embedded public key accepts. Composition of the existing IConfigSignature — no new crypto.
[SharedComponent]
public interface IAdminKeyVerifier
{
    ErrorOr<Success> Verify(byte[] privateKey, string? passphrase);
}
