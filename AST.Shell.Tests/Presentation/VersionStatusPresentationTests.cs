using AST.Core.Presentation;

namespace AST.Shell.Tests.Presentation;

// Headless mapping tests for the 4-state version status. Text is product UI copy (Vietnamese), single-homed
// here. BrushKey values are PROPOSAL pending requester/F5 sign-off; if a colour changes, change it here.
public class VersionStatusPresentationTests
{
    [Theory]
    [InlineData(VersionStatus.None, "")]
    [InlineData(VersionStatus.Cancelled, "Bị hủy")]
    [InlineData(VersionStatus.Expired, "Hết hiệu lực")]
    [InlineData(VersionStatus.Effective, "Hiệu lực")]
    [InlineData(VersionStatus.Pending, "Chờ hiệu lực")]
    public void DisplayText_maps_each_state(VersionStatus status, string expected)
        => Assert.Equal(expected, VersionStatusPresentation.DisplayText(status));

    [Theory]
    [InlineData(VersionStatus.None, "AstTextSecondaryBrush")]
    [InlineData(VersionStatus.Cancelled, "AstErrorBrush")]
    [InlineData(VersionStatus.Expired, "AstTextSecondaryBrush")]
    [InlineData(VersionStatus.Effective, "AstSuccessBrush")]
    [InlineData(VersionStatus.Pending, "AstAccentLinkBrush")]
    public void BrushKey_maps_each_state(VersionStatus status, string expected)
        => Assert.Equal(expected, VersionStatusPresentation.BrushKey(status));
}
