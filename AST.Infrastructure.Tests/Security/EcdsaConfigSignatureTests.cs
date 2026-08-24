using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class EcdsaConfigSignatureTests
{
    private const string Pass = "s3cret-pass";

    [Fact]
    public void Sign_then_Verify_roundtrips()
    {
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        var sut = new EcdsaConfigSignature(pub);
        var data = "hello"u8.ToArray();

        var sig = sut.Sign(data, priv, Pass);

        Assert.True(sut.Verify(data, sig));
    }

    [Fact]
    public void Verify_fails_when_data_tampered()
    {
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        var sut = new EcdsaConfigSignature(pub);
        var sig = sut.Sign("hello"u8.ToArray(), priv, Pass);

        Assert.False(sut.Verify("hellp"u8.ToArray(), sig));
    }

    [Fact]
    public void Verify_fails_when_signature_tampered()
    {
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        var sut = new EcdsaConfigSignature(pub);
        var data = "hello"u8.ToArray();
        var sig = sut.Sign(data, priv, Pass);
        sig[0] ^= 0xFF;

        Assert.False(sut.Verify(data, sig));
    }

    [Fact]
    public void Sign_throws_on_wrong_passphrase()
    {
        var (_, priv) = EcdsaKeys.Generate(Pass);
        var sut = new EcdsaConfigSignature(EcdsaKeys.Generate(Pass).PublicBase64);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => sut.Sign("x"u8.ToArray(), priv, "wrong-pass"));
    }

    [Fact]
    public void KeyMatches_true_for_matching_pair_false_otherwise()
    {
        var (pub, priv) = EcdsaKeys.Generate(Pass);
        var sut = new EcdsaConfigSignature(pub);
        var (_, otherPriv) = EcdsaKeys.Generate(Pass);

        Assert.True(sut.KeyMatches(priv, Pass));
        Assert.False(sut.KeyMatches(otherPriv, Pass));
    }
}
