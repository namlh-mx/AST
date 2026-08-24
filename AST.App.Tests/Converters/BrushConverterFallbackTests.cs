using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AST.Converters;
using AST.Core.Startup;
using AST.Core.Presentation;

namespace AST.App.Tests.Converters;

// These run headless (no WPF Application): Application.Current is null, so TryFindResource is skipped and the
// fallback branch is exercised — proving a declared severity/mode never converts to a null (invisible) brush.
// The "themed key actually resolves to the right themed brush" half needs an STA Application with the resource
// dictionaries loaded and is deliberately not covered here.
public class BrushConverterFallbackTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void StatusSeverity_non_severity_value_converts_to_null()
    {
        var c = new StatusSeverityToBrushConverter();
        Assert.Null(c.Convert("not a severity", typeof(Brush), null, Culture));
    }

    [Fact]
    public void StatusSeverity_declared_value_falls_back_to_a_visible_brush()
    {
        var c = new StatusSeverityToBrushConverter();
        Assert.Same(SystemColors.ControlTextBrush, c.Convert(StatusSeverity.Error, typeof(Brush), null, Culture));
    }

    [Fact]
    public void StatusSeverity_ConvertBack_is_not_supported()
    {
        var c = new StatusSeverityToBrushConverter();
        Assert.Throws<NotSupportedException>(() => c.ConvertBack(null, typeof(object), null, Culture));
    }

    [Fact]
    public void StartupMode_non_mode_value_converts_to_null()
    {
        var c = new StartupModeToBrushConverter();
        Assert.Null(c.Convert("not a mode", typeof(Brush), null, Culture));
    }

    [Fact]
    public void StartupMode_declared_value_falls_back_to_a_visible_brush()
    {
        var c = new StartupModeToBrushConverter();
        Assert.Same(SystemColors.ControlTextBrush, c.Convert(StartupMode.Connected, typeof(Brush), null, Culture));
    }

    [Fact]
    public void StartupMode_ConvertBack_is_not_supported()
    {
        var c = new StartupModeToBrushConverter();
        Assert.Throws<NotSupportedException>(() => c.ConvertBack(null, typeof(object), null, Culture));
    }

    [Fact]
    public void VersionStatus_declared_value_falls_back_to_a_visible_brush()
    {
        var c = new VersionStatusToBrushConverter();
        Assert.Same(SystemColors.ControlTextBrush, c.Convert(VersionStatus.Effective, typeof(Brush), null, Culture));
    }
}
