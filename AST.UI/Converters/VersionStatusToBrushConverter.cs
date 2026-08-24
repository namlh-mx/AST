using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AST.Core.Presentation;

namespace AST.Converters;

// VersionStatus → themed foreground brush, resolved from the app resources by the brush KEY the presentation
// returns. Falls back to a visible system brush on a missing key (never invisible/black), matching
// StatusSeverityToBrushConverter. Pure + null-safe (wpf-rule-converter-patterns).
public sealed class VersionStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = VersionStatusPresentation.BrushKey(value is VersionStatus s ? s : VersionStatus.None);
        return Application.Current?.TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
