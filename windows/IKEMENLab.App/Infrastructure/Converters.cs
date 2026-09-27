using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IKEMENLab.App.Infrastructure;

/// <summary>true → Visible, false → Collapsed. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>null / empty string / zero count → Collapsed. Parameter "invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Enum value == parameter name → true (for segmented RadioButtons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && parameter is string s && string.Equals(value.ToString(), s, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string s && targetType.IsEnum)
        {
            return Enum.Parse(targetType, s);
        }

        return Binding.DoNothing;
    }
}

/// <summary>0..1 fraction × parameter (max width in px) → width.</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };
        var max = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            ? m
            : 100d;
        return Math.Clamp(fraction, 0, 1) * max;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
