using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AST.Core.Presentation;

namespace AST.Converters;

// Resolves a StatusSeverity to the themed foreground brush (P2: colored text, no fill). The severity->key
// decision (StatusSeverityPresentation) is headless-testable in AST.Core; only the WPF resource lookup lives here.
public sealed class StatusSeverityToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not StatusSeverity severity) return null;
        var key = StatusSeverityPresentation.BrushKey(severity);
        // A declared severity must never render as invisible/black: if the themed brush is missing (a
        // resource-wiring bug), fall back to a visible system brush, matching AstDialog.BrushFor.
        return Application.Current?.TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
