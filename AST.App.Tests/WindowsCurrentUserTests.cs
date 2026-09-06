using System.Security.Principal;
using AST.Core.Security;
using FluentAssertions;

namespace AST.App.Tests;

public class WindowsCurrentUserTests
{
    // Card 259 Part 3: the one missed call site. Both sides read the same environment, so this is
    // deterministic on any machine and goes RED the day WindowsCurrentUser stops normalizing.
    [Fact]
    public void Username_IsTheNormalizedWindowsIdentity()
    {
        var expected = WindowsUsernameNormalizer.Normalize(WindowsIdentity.GetCurrent()?.Name);
        new WindowsCurrentUser().Username.Should().Be(expected);
    }
}
