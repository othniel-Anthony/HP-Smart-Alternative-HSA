using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HSA.Converters;

/// <summary>
/// Compares an int value to ConverterParameter; returns true if they match.
/// Used for binding ToggleButton.IsChecked to an int index in the ViewModel.
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        if (!int.TryParse(parameter.ToString(), out var expected)) return false;
        return value is int actual && actual == expected;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null && int.TryParse(parameter.ToString(), out var i))
            return i;
        return Binding.DoNothing;
    }
}

/// <summary>
/// Returns Visible when the bound value (typically a progress total > 0) indicates
/// a refresh is in progress, otherwise Collapsed.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        var threshold = 0;
        if (parameter is not null && int.TryParse(parameter.ToString(), out var p)) threshold = p;
        return n > threshold ? Visibility.Visible : Visibility.Collapsed;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// For a horizontal progress bar: takes a percent (0-100) and a maximum pixel width,
/// returns the actual width. If percent is null (unknown), returns 0.
/// </summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        int? pct = values[0] is int i ? i : (int?)null;
        double maxWidth = values[1] is double d ? d : 0.0;
        if (pct is null || maxWidth <= 0) return 0.0;
        return Math.Max(0.0, Math.Min(maxWidth, maxWidth * pct.Value / 100.0));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
