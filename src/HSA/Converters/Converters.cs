using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HSA.Models;

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

/// <summary>
/// Maps a consumable's color name ("black", "cyan", "magenta", "yellow", …) to a
/// brush used for the small status chip next to a printer in the list. The chip
/// text is always light/white so the same colors work in both light and dark themes.
/// </summary>
public sealed class ColorNameToBrushConverter : IValueConverter
{
    // Chosen to be saturated enough to be distinguishable on both light (#FEFBFF)
    // and dark (#1B1B1F) backgrounds while keeping WCAG-acceptable contrast for white
    // foreground text.
    private static readonly SolidColorBrush Black    = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)));
    private static readonly SolidColorBrush Cyan     = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x65, 0x8A)));
    private static readonly SolidColorBrush Magenta  = Freeze(new SolidColorBrush(Color.FromRgb(0x9B, 0x00, 0x58)));
    private static readonly SolidColorBrush Yellow   = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0x88, 0x00)));
    private static readonly SolidColorBrush Red      = Freeze(new SolidColorBrush(Color.FromRgb(0xBA, 0x1A, 0x1A)));
    private static readonly SolidColorBrush Green    = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
    private static readonly SolidColorBrush Blue     = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x4F, 0xCC)));
    private static readonly SolidColorBrush White    = Freeze(new SolidColorBrush(Color.FromRgb(0x75, 0x76, 0x80)));
    private static readonly SolidColorBrush Unknown  = Freeze(new SolidColorBrush(Color.FromRgb(0x75, 0x76, 0x80)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Unknown;
        return s.ToLowerInvariant() switch
        {
            "black"   => Black,
            "k"       => Black,
            "cyan"    => Cyan,
            "c"       => Cyan,
            "magenta" => Magenta,
            "m"       => Magenta,
            "yellow"  => Yellow,
            "y"       => Yellow,
            "red"     => Red,
            "green"   => Green,
            "blue"    => Blue,
            "white"   => White,
            _         => Unknown
        };
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverse of <see cref="CountToVisibilityConverter"/> — returns Visible when
/// the bound collection is empty, otherwise Collapsed. Used to show a friendly
/// "no consumables fetched yet" placeholder for non-network printers.
/// </summary>
public sealed class CountToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        var threshold = 0;
        if (parameter is not null && int.TryParse(parameter.ToString(), out var p)) threshold = p;
        return n > threshold ? Visibility.Collapsed : Visibility.Visible;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the bound string is null or empty, otherwise Collapsed.
/// Used to hide a placeholder when consumable data is present.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
