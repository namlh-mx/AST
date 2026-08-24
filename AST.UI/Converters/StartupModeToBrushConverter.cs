using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AST.Core.Startup;
using AST.Core.Presentation;

namespace AST.Converters;

// Wraps the headless StartupModePresentation.BrushKey mapping: turns a StartupMode into the themed banner
// brush resource (AstConnectedBrush / AstNotConnectedBrush). The mode->key decision is headless-testable in
// AST.Core; only the WPF resource lookup lives here.
public sealed class StartupModeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not StartupMode mode) return null;
        var key = StartupModePresentation.BrushKey(mode);
        // A declared mode must never render as invisible/black: if the themed banner brush is missing (a
        // resource-wiring bug), fall back to a visible system brush, matching AstDialog.BrushFor.
        return Application.Current?.TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
