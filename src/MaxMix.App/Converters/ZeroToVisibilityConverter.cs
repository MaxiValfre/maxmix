namespace MaxMix.App.Converters;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound integer is 0,
/// otherwise <see cref="Visibility.Collapsed"/>. Used for "no items" placeholder
/// text on collection counts.
/// </summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int    i => i,
            byte   b => b,
            long   l => (int)l,
            short  s => s,
            _        => -1,
        };
        return n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
