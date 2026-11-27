using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DeveParTools.App.Controls;

/// <summary>Resolves a resource key (e.g. "BlockFoundBrush") to the brush of the current theme.</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public static readonly ResourceBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var res))
        {
            return res as IBrush;
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resolves an icon resource key to its geometry.</summary>
public sealed class ResourceGeometryConverter : IValueConverter
{
    public static readonly ResourceGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && Application.Current is { } app && app.TryFindResource(key, out var res) ? res as Geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 → 0..100.</summary>
public sealed class PercentConverter : IValueConverter
{
    public static readonly PercentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double d ? d * 100 : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
