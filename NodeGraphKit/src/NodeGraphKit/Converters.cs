using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NodeGraphKit.Model;

namespace NodeGraphKit;

/// <summary>The status dot: grey idle, green ready, amber warning, red error.</summary>
public sealed class NodeStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Idle = Frozen("#6B7280");
    private static readonly Brush Ready = Frozen("#22C55E");
    private static readonly Brush Warning = Frozen("#F59E0B");
    private static readonly Brush Error = Frozen("#EF4444");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            NodeStatus.Ready => Ready,
            NodeStatus.Warning => Warning,
            NodeStatus.Error => Error,
            _ => Idle,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Collapses an element when its value is null or blank. With
/// <see cref="Invert"/> it shows only then - for placeholder text.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isEmpty = value is null || value is string text && string.IsNullOrWhiteSpace(text);
        return isEmpty != Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims a bypassed node - the only sign of it on the canvas, so it has to be obvious.</summary>
public sealed class BooleanToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false ? 0.4 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns an icon key ("ph_number") into the drawing registered as
/// <c>Icon.ph_number</c>. An unknown key shows the puzzle piece rather than a gap.
/// </summary>
public sealed class IconKeyToImageConverter : IValueConverter
{
    internal const string FallbackKey = "Icon.ph_custom_node";

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return Application.Current?.TryFindResource(FallbackKey) as ImageSource;

        return Application.Current?.TryFindResource($"Icon.{key}") as ImageSource
               ?? Application.Current?.TryFindResource(FallbackKey) as ImageSource;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
