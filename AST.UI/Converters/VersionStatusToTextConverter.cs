using System.Globalization;
using System.Windows.Data;
using AST.Core.Presentation;

namespace AST.Converters;

// VersionStatus → VN label (delegates to the single home). Pure + null/UnsetValue-safe (wpf-rule-converter-patterns).
public sealed class VersionStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => VersionStatusPresentation.DisplayText(value is VersionStatus s ? s : VersionStatus.None);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
