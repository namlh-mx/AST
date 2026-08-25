using AST.Core.Presentation;
using FluentAssertions;

namespace AST.Shell.Tests.Presentation;

// Headless mapping tests for the version status. Text is product UI copy (Vietnamese), single-homed
// here. BrushKey values are PROPOSAL pending requester/F5 sign-off; if a colour changes, change it here.
public class VersionStatusPresentationTests
{
    [Theory]
    [InlineData(VersionStatus.None, "")]
    [InlineData(VersionStatus.Cancelled, "Bị hủy")]
    [InlineData(VersionStatus.Replaced, "Bị thay thế")]
    [InlineData(VersionStatus.Expired, "Hết hiệu lực")]
    [InlineData(VersionStatus.Effective, "Hiệu lực")]
    [InlineData(VersionStatus.Pending, "Chờ hiệu lực")]
    public void DisplayText_maps_each_state(VersionStatus status, string expected)
        => Assert.Equal(expected, VersionStatusPresentation.DisplayText(status));

    [Theory]
    [InlineData(VersionStatus.None, "AstTextSecondaryBrush")]
    [InlineData(VersionStatus.Cancelled, "AstErrorBrush")]
    [InlineData(VersionStatus.Replaced, "AstTextSecondaryBrush")]
    [InlineData(VersionStatus.Expired, "AstTextSecondaryBrush")]
    [InlineData(VersionStatus.Effective, "AstSuccessBrush")]
    [InlineData(VersionStatus.Pending, "AstAccentLinkBrush")]
    public void BrushKey_maps_each_state(VersionStatus status, string expected)
        => Assert.Equal(expected, VersionStatusPresentation.BrushKey(status));

    // The states the two theories above pin with an explicit label AND brush key. Kept MANUAL on
    // purpose, and this list -- not a "did the call return something" assertion -- is the guard.
    //
    // An earlier version of this test asserted `NotBeNullOrWhiteSpace` over the enum instead. That
    // cannot fail for BrushKey: its `_` arm returns "AstTextSecondaryBrush", a perfectly non-empty
    // string, so a state with no arm of its own passes while rendering muted grey. The DisplayText
    // half would still redden, and its message names only the label -- so whoever added the state
    // would add a label, go green, and ship a colour nobody chose. That is that same labelling defect
    // reappearing inside the guard written to prevent it (AI Agent MED-1, 2026-08-24).
    //
    // Same shape as VersionCloseRules.Codes.All: a deliberately manual list that reddens when a new
    // member is declared. Adding a state means adding it HERE and to both theories, with the label
    // and colour a human picked.
    private static readonly VersionStatus[] ExplicitlyMapped =
    [
        VersionStatus.None, VersionStatus.Cancelled, VersionStatus.Replaced,
        VersionStatus.Expired, VersionStatus.Effective, VersionStatus.Pending,
    ];

    [Fact]
    public void EveryVersionStatusIsExplicitlyMapped()
        => Enum.GetValues<VersionStatus>().Should().BeEquivalentTo(
            ExplicitlyMapped,
            "a state missing from the theories above ships the `_` fallthrough's blank label and muted colour, with a green build");
}
