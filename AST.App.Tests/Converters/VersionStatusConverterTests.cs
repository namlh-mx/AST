using System.Globalization;
using AST.Converters;
using AST.Core.Presentation;

namespace AST.App.Tests.Converters;

// The text converter is a thin delegation to VersionStatusPresentation; these guard that delegation +
// null-safety (converters run on the MTA worker thread -- no STA needed, they touch no visual element).
// VersionStatusToBrushConverter's fallback-brush behavior is covered in BrushConverterFallbackTests.cs (the
// established single home for that concern) -- not duplicated here with a weaker assertion.
public class VersionStatusConverterTests
{
    [Fact]
    public void Text_converter_delegates_to_presentation()
        => Assert.Equal("Hiệu lực",
            new VersionStatusToTextConverter().Convert(VersionStatus.Effective, typeof(string), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void Text_converter_is_null_safe()
        => Assert.Equal(string.Empty,
            new VersionStatusToTextConverter().Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
}
