using System.Security.Cryptography;
using AST.Core.Security;
using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class AdminKeyVerifierTests
{
    private const string Pass = "correct horse";

    // Builds a verifier whose embedded public key matches (pub) and the matching encrypted private key (priv).
    private static (IAdminKeyVerifier verifier, byte[] priv) MakeMatching()
    {
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        return (new AdminKeyVerifier(new EcdsaConfigSignature(pub)), priv);
    }

    [Fact]
    public void Verify_accepts_matching_key_and_passphrase()
    {
        var (verifier, priv) = MakeMatching();
        var result = verifier.Verify(priv, Pass);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Verify_rejects_wrong_passphrase_as_KeyUnreadable()
    {
        var (verifier, priv) = MakeMatching();
        var result = verifier.Verify(priv, "wrong passphrase");
        Assert.True(result.IsError);
        Assert.Equal("Config.KeyUnreadable", result.FirstError.Code);
    }

    [Fact]
    public void Verify_rejects_corrupt_key_bytes_as_KeyUnreadable()
    {
        var (verifier, _) = MakeMatching();
        var result = verifier.Verify(new byte[] { 1, 2, 3, 4 }, Pass);
        Assert.True(result.IsError);
        Assert.Equal("Config.KeyUnreadable", result.FirstError.Code);
    }

    [Fact]
    public void Verify_rejects_key_that_does_not_match_app_as_KeyMismatch()
    {
        // App embeds public key #1; admin presents a DIFFERENT valid key #2 (right passphrase, wrong app).
        var (appPub, _) = EcdsaKeys.Generate(Pass);
        var (_, otherPriv) = EcdsaKeys.Generate(Pass);
        var verifier = new AdminKeyVerifier(new EcdsaConfigSignature(appPub));

        var result = verifier.Verify(otherPriv, Pass);

        Assert.True(result.IsError);
        Assert.Equal("Config.KeyMismatch", result.FirstError.Code);
    }
}
