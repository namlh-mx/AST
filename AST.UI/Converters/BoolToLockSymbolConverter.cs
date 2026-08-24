using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace AST.Converters;

// Maps an audit row's Signed flag to a lock symbol: signed -> closed lock, unsigned -> open lock.
public sealed class BoolToLockSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SymbolRegular.LockClosed24 : SymbolRegular.LockOpen24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
