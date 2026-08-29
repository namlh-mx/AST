using AST.Infrastructure.Security;
using FluentAssertions;

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

    // The guard's own test asserted only the Code, so the sentence an operator reads was pinned by
    // nothing. Added when the settled operator wording was applied: this is
    // the only thing that reddens if that sentence is changed by accident.
    [Fact]
    public void EnsureKeyConfigured_reports_the_settled_sentence()
    {
        var r = ConfigSecurity.EnsureKeyConfigured(requireSignature: true, isPlaceholder: true);

        r.FirstError.Description.Should().Be("Ứng dụng chưa khai báo khóa công khai để xác thực.");
    }

    [Theory]
    [InlineData(true, false)]   // release + real key -> ok
    [InlineData(false, true)]   // debug + placeholder -> ok (dev)
    [InlineData(false, false)]
    public void EnsureKeyConfigured_ok_otherwise(bool require, bool placeholder)
        => Assert.False(ConfigSecurity.EnsureKeyConfigured(require, placeholder).IsError);
}
