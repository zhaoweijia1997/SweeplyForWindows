using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SweeplyForWindows;

/// <summary>Visible when the bound int equals the converter parameter (page switching).</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter as string, out int page) && i == page ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapsed for null or empty text; with the parameter "invert", visible only then.</summary>
public sealed class TextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) != (parameter as string == "invert") ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when false (the opposite of BooleanToVisibilityConverter).</summary>
public sealed class FalseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A width divided by the parameter (columns of a wrap panel whose rows each fit their own tallest item).</summary>
public sealed class ColumnWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && int.TryParse(parameter as string, out int columns) && columns > 0 && width > columns
            ? Math.Floor(width / columns) - 1 // the pixel left over stops the last column from wrapping
            : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0.55 opacity for dimmed items, 1 otherwise.</summary>
public sealed class DimOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 0.55 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
