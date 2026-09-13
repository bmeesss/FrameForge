using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FrameForge.App.Converters;

/// <summary>
/// Shows an element when the bound page name matches ConverterParameter.
/// </summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value?.ToString();
        var target = parameter?.ToString();
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
