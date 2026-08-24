using AST.Core.Presentation;

namespace AST.Shell.Tests.Presentation;

public class StatusSeverityPresentationTests
{
    [Theory]
    [InlineData(StatusSeverity.Success, "AstSuccessBrush")]
    [InlineData(StatusSeverity.Error, "AstErrorBrush")]
    [InlineData(StatusSeverity.Info, "AstErrorBrush")]
    [InlineData(StatusSeverity.Warning, "AstErrorBrush")]
    [InlineData(StatusSeverity.None, "AstTextSecondaryBrush")]
    public void BrushKey_maps_each_severity(StatusSeverity severity, string expectedKey)
        => Assert.Equal(expectedKey, StatusSeverityPresentation.BrushKey(severity));
}
