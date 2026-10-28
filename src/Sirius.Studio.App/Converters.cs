using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Sirius.Core.Graph;

namespace Sirius.Studio.App;

/// <summary>
/// Maps node status onto the indicator colour. Keeps the traffic-light
/// convention the old status bar used.
/// </summary>
public sealed class NodeStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Idle = Frozen("#6B7280");
    private static readonly SolidColorBrush Ready = Frozen("#22C55E");
    private static readonly SolidColorBrush Warning = Frozen("#F59E0B");
    private static readonly SolidColorBrush Error = Frozen("#EF4444");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is NodeStatus status
            ? status switch
            {
                NodeStatus.Ready => Ready,
                NodeStatus.Warning => Warning,
                NodeStatus.Error => Error,
                _ => Idle,
            }
            : Idle;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Collapses an element when its bound value is null or empty. With
/// <see cref="Invert"/> set it does the opposite, which is how placeholder text
/// is shown only while a value is absent.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Show when the value IS null/empty rather than when it is not.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            string text => string.IsNullOrWhiteSpace(text),
            _ => false,
        };

        if (Invert)
            isEmpty = !isEmpty;

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Set to invert the mapping.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Dims a node that has been bypassed.
/// </summary>
/// <remarks>
/// With the enable checkbox moved off the node and into the inspector, this is
/// the only cue on the canvas that a node is being skipped, so it has to be
/// obvious at a glance.
/// </remarks>
public sealed class BooleanToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false ? 0.4 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Picks the socket outline: a square for images, a circle for everything else.
/// </summary>
/// <remarks>
/// Implemented as a corner radius on a single square Border rather than two
/// alternative shapes, so one element covers both cases and the connector
/// keeps a constant size and anchor point regardless of type.
/// </remarks>
public sealed class PortTypeToCornerRadiusConverter : IValueConverter
{
    private static readonly CornerRadius Square = new(1);
    private static readonly CornerRadius Round = new(6);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string == "ImageData" ? Square : Round;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves a node's icon key (e.g. "tool-icon-03") to the drawing registered
/// in <c>Themes/Icons.xaml</c>.
/// </summary>
/// <remarks>
/// Returns an <see cref="ImageSource"/> rather than a geometry: the icon sets
/// are multi-coloured, so flattening them into one tintable shape would throw
/// the artwork away. Looked up by key at render time, so new icons are a matter
/// of regenerating the dictionary and setting
/// <c>VisionNodeAttribute.Icon</c> - no change to node or view code. An unknown
/// key renders nothing instead of throwing.
/// </remarks>
public sealed class IconKeyToImageConverter : IValueConverter
{
    internal const string FallbackKey = "Icon.ph_custom_node";

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return null;

        // A custom node may name an icon this build does not have - one
        // added in a later release, or a typo. The puzzle piece says "added
        // from outside" rather than leaving a gap in the toolbox.
        return Application.Current?.TryFindResource($"Icon.{key}") as ImageSource
               ?? Application.Current?.TryFindResource(FallbackKey) as ImageSource;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colours a socket by the type it carries, so compatible pairs are visually
/// obvious before a drag is attempted - the role socket colour plays in Blender.
/// </summary>
public sealed class PortTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ImageBrush = Frozen("#60A5FA");
    private static readonly SolidColorBrush HistogramBrush = Frozen("#A78BFA");
    private static readonly SolidColorBrush ProfileBrush = Frozen("#F472B6");
    private static readonly SolidColorBrush TextBrush = Frozen("#34D399");
    private static readonly SolidColorBrush NumberBrush = Frozen("#FBBF24");
    private static readonly SolidColorBrush ShapeBrush = Frozen("#A78BFA");
    private static readonly SolidColorBrush Fallback = Frozen("#9CA3AF");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string switch
        {
            "ImageData" => ImageBrush,
            "HistogramData" => HistogramBrush,
            "ProfileData" => ProfileBrush,
            "ShapeSet" => ShapeBrush,
            "String" => TextBrush,
            "Int32" or "Int64" or "Int16" or "Double" or "Single" => NumberBrush,
            _ => Fallback,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
