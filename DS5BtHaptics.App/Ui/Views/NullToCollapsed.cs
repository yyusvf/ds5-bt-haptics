using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DS5BtHaptics.App.Ui.Views;

/// <summary>Hides an element while its bound value is null.</summary>
internal sealed class NullToCollapsed : IValueConverter
{
    public static readonly NullToCollapsed Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
