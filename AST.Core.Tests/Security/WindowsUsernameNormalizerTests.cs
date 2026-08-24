using AST.Core.Security;

namespace AST.Core.Tests.Security;

public class WindowsUsernameNormalizerTests
{
    [Theory]
    [InlineData(@"EXAMPLE\alice", "alice")]      // SAM form: strip domain prefix
    [InlineData("alice", "alice")]            // bare
    [InlineData("ALICE", "alice")]            // case-insensitive
    [InlineData("alice@example.local", "alice")] // UPN form: strip suffix
    [InlineData(@"  EXAMPLE\Alice  ", "alice")]  // trim + case
    public void Normalize_returns_bare_lowercase_username(string raw, string expected)
        => Assert.Equal(expected, WindowsUsernameNormalizer.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_returns_null_for_blank(string? raw)
        => Assert.Null(WindowsUsernameNormalizer.Normalize(raw));
}
