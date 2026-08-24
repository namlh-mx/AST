using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AST.Converters;

// Collapses an element when its bound string is null/empty/whitespace, otherwise shows it.
// Used for the status banner and the authenticated-key info block so they reserve no space when empty.
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
