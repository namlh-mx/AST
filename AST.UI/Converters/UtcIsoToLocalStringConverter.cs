using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AST.Converters;

// Parses a raw UTC ISO-8601 timestamp string (as stored in ConfigAuditRecord/ConnectionHistoryEntry.TsUtc)
// and formats it in local time as dd-MM-yyyy HH:mm for display. Unparsable input passes through unchanged
// rather than throwing (view-layer display concern only, no VM change).
public sealed class UtcIsoToLocalStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue) return string.Empty;
        var text = value as string;
        if (string.IsNullOrEmpty(text)) return text;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        return text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
