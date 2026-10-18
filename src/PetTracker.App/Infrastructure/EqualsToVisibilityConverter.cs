using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PetTracker.App.Infrastructure;

/// <summary>Visible when the bound value's text equals the ConverterParameter (used for the settings sections).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
