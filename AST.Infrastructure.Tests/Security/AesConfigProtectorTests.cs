using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class AesConfigProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_roundtrips()
    {
        var sut = new AesConfigProtector();
        var plain = "connection-json"u8.ToArray();

        var cipher = sut.Protect(plain);
        var back = sut.Unprotect(cipher);

        Assert.Equal(plain, back);
    }

    [Fact]
    public void Protect_uses_random_iv_so_ciphertext_differs()
    {
        var sut = new AesConfigProtector();
        var plain = "same"u8.ToArray();
        Assert.NotEqual(sut.Protect(plain), sut.Protect(plain));
    }

    [Fact]
    public void Unprotect_throws_on_garbage()
    {
        var sut = new AesConfigProtector();
        Assert.ThrowsAny<Exception>(() => sut.Unprotect(new byte[] { 1, 2, 3 }));
    }
}
