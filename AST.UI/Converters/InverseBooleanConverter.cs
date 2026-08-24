using System;
using System.Globalization;
using System.Windows.Data;

namespace AST.Converters;

// Inverts a bool for one-way IsEnabled bindings (e.g. "form enabled while NOT authenticated").
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
