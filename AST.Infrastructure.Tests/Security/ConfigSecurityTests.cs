using AST.Infrastructure.Security;

namespace AST.Infrastructure.Tests.Security;

public class ConfigSecurityTests
{
    [Fact]
    public void Debug_build_disables_signature_requirement()
        => Assert.False(RequireConfigSignature.Value); // the test plan runs in Debug

    [Fact]
    public void EnsureKeyConfigured_rejects_release_with_placeholder_key()
    {
        var r = ConfigSecurity.EnsureKeyConfigured(requireSignature: true, isPlaceholder: true);
        Assert.True(r.IsError);
        Assert.Equal("Config.PublicKeyNotConfigured", r.FirstError.Code);
    }

    [Theory]
    [InlineData(true, false)]   // release + real key -> ok
    [InlineData(false, true)]   // debug + placeholder -> ok (dev)
    [InlineData(false, false)]
    public void EnsureKeyConfigured_ok_otherwise(bool require, bool placeholder)
        => Assert.False(ConfigSecurity.EnsureKeyConfigured(require, placeholder).IsError);
}
