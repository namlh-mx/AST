using System;
using System.Globalization;
using System.Windows.Data;
using AST.Core.Presentation;
using Wpf.Ui.Controls;

namespace AST.Converters;

// Picks the status band's leading Fluent symbol from a StatusSeverity: success -> checkmark, everything
// else (info/warning/error/none) -> warning. Colour still comes from StatusSeverityToBrushConverter.
public sealed class StatusSeverityToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StatusSeverity.Success ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
