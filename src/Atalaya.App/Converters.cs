using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Atalaya.Domain;
using Atalaya.Storage.Sync;

namespace Atalaya.App;

/// <summary>Sync indicator colour (§3): green/amber/red.</summary>
public sealed class SyncHealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SyncHealth.Green => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
        SyncHealth.Amber => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
        _ => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Severity colour for finding badges (rúbrica §0).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critica => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        Severity.Alta => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x2B)),
        Severity.Media => new SolidColorBrush(Color.FromRgb(0xD2, 0xB0, 0x36)),
        _ => new SolidColorBrush(Color.FromRgb(0x6C, 0x93, 0xC0)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible (for empty-state overlays).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string / non-null → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? (!string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed)
            : value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a 0..1 progress as a whole percentage string.</summary>
public sealed class ProgressToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
