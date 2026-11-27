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

/// <summary>
/// Adds the (usually negative) number in the parameter to a size, not going below 0. For example the Create page's
/// options get <c>MaxHeight = column height − 220</c>, so the block grid below them keeps at least 220 pixels and the
/// options scroll instead.
/// </summary>
public sealed class OffsetConverter : IValueConverter
{
    public static readonly OffsetConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double size && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset)
            ? Math.Max(0, size + offset)
            : double.PositiveInfinity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
