using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Sirius.Vision;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// Draws a per-channel intensity histogram.
/// </summary>
/// <remarks>
/// A direct <see cref="OnRender"/> implementation rather than a charting
/// control: this redraws on every parameter change, and 256 bins per channel
/// is a filled polygon, not something that needs a chart engine. The old app
/// pushed 768 <c>SeriesPoint</c> objects into a DevExpress chart on each pass.
/// </remarks>
public sealed class HistogramControl : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(HistogramData),
        typeof(HistogramControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Clamps the vertical scale. A large flat background produces one enormous
    /// bin that would otherwise flatten the rest of the plot - the old code
    /// handled this by hard-coding a 100000 ceiling.
    /// </summary>
    public static readonly DependencyProperty ClipPercentileProperty = DependencyProperty.Register(
        nameof(ClipPercentile),
        typeof(double),
        typeof(HistogramControl),
        new FrameworkPropertyMetadata(0.995, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush[] ChannelBrushes =
    [
        Frozen(Color.FromArgb(0x99, 0x60, 0xA5, 0xFA)),
        Frozen(Color.FromArgb(0x99, 0x34, 0xD3, 0x99)),
        Frozen(Color.FromArgb(0x99, 0xF8, 0x71, 0x71)),
    ];

    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen CursorPen = FrozenPen(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Brush ReadoutBrush = Frozen(Color.FromArgb(0xE6, 0x1A, 0x1B, 0x1E));
    private static readonly Brush ReadoutTextBrush = Frozen(Color.FromRgb(0xE6, 0xE8, 0xEA));

    /// <summary>Solid versions of the fills, for the readout's text.</summary>
    private static readonly Brush[] ChannelTextBrushes =
    [
        Frozen(Color.FromRgb(0x60, 0xA5, 0xFA)),
        Frozen(Color.FromRgb(0x34, 0xD3, 0x99)),
        Frozen(Color.FromRgb(0xF8, 0x71, 0x71)),
    ];

    private static readonly Typeface ReadoutTypeface = new("Consolas");

    /// <summary>Where the pointer is, in control pixels, or null when it is away.</summary>
    private double? _cursorX;

    public HistogramData? Data
    {
        get => (HistogramData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double ClipPercentile
    {
        get => (double)GetValue(ClipPercentileProperty);
        set => SetValue(ClipPercentileProperty, value);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        _cursorX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        _cursorX = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 1 || height <= 1)
            return;

        // Transparent background still needs to be drawn for hit-testing and to
        // give the grid something to sit on.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        for (var i = 1; i < 4; i++)
        {
            var x = width * i / 4.0;
            drawingContext.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        }

        var data = Data;
        if (data is null || data.ChannelCount == 0 || data.BinCount < 2)
            return;

        var ceiling = ComputeCeiling(data);
        if (ceiling <= 0)
            return;

        for (var channel = 0; channel < data.ChannelCount; channel++)
        {
            var bins = data.Channels[channel];
            var brush = ChannelBrushes[channel % ChannelBrushes.Length];
            drawingContext.DrawGeometry(brush, null, BuildGeometry(bins, ceiling, width, height));
        }

        DrawCursor(drawingContext, data, width, height);
    }

    /// <summary>
    /// A line at the bin under the pointer, and how many pixels are in it.
    /// </summary>
    /// <remarks>
    /// Snapped to a bin, because the question asked of a histogram is "how
    /// many are at this level", and that has an answer only at a bin. The
    /// counts are the raw ones, not the clipped scale the bars are drawn
    /// against - the clipping is there so the plot is readable, and it would
    /// be a poor reason to report a smaller number than was counted.
    /// </remarks>
    private void DrawCursor(DrawingContext context, HistogramData data, double width, double height)
    {
        if (_cursorX is not { } pointer || data.BinCount < 2)
            return;

        var step = width / (data.BinCount - 1);
        var bin = (int)Math.Clamp(Math.Round(pointer / step), 0, data.BinCount - 1);
        var x = bin * step;

        context.DrawLine(CursorPen, new Point(x, 0), new Point(x, height));

        var lines = new List<(string Text, Brush Brush)> { ($"level {bin}", ReadoutTextBrush) };

        for (var channel = 0; channel < data.ChannelCount; channel++)
        {
            lines.Add((
                $"{Name(channel, data.ChannelCount)} {data.Channels[channel][bin]:#,0}",
                ChannelTextBrushes[channel % ChannelTextBrushes.Length]));
        }

        DrawReadout(context, x, width, height, lines);
    }

    /// <summary>
    /// Channel names by the engine's own order - blue first, as OpenCV
    /// stores them - and nothing at all for a single-channel frame, where
    /// the label would say less than the number beside it.
    /// </summary>
    private static string Name(int channel, int count) => count == 1
        ? "count"
        : channel switch { 0 => "B", 1 => "G", 2 => "R", _ => $"Ch{channel}" };

    private void DrawReadout(
        DrawingContext context,
        double x,
        double width,
        double height,
        IReadOnlyList<(string Text, Brush Brush)> lines)
    {
        const double padding = 4;
        const double gap = 8;

        var formatted = lines.Select(line => new FormattedText(
            line.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            ReadoutTypeface,
            10,
            line.Brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)).ToArray();

        var panelWidth = formatted.Max(text => text.Width) + (2 * padding);
        var panelHeight = formatted.Sum(text => text.Height) + (2 * padding);

        var left = x + gap;
        if (left + panelWidth > width)
            left = x - gap - panelWidth;

        left = Math.Max(0, left);

        context.DrawRectangle(ReadoutBrush, null, new Rect(left, 2, panelWidth, panelHeight));

        var y = 2 + padding;
        foreach (var text in formatted)
        {
            context.DrawText(text, new Point(left + padding, y));
            y += text.Height;
        }
    }

    private static Geometry BuildGeometry(float[] bins, double ceiling, double width, double height)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(0, height),
            IsClosed = true,
            IsFilled = true,
        };

        var step = width / (bins.Length - 1);

        for (var i = 0; i < bins.Length; i++)
        {
            var normalised = Math.Min(1.0, bins[i] / ceiling);
            figure.Segments.Add(new LineSegment(new Point(i * step, height - (normalised * height)), true));
        }

        figure.Segments.Add(new LineSegment(new Point(width, height), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Scale ceiling taken from a high percentile of the non-empty bins, so a
    /// single saturated peak does not squash everything else.
    /// </summary>
    private double ComputeCeiling(HistogramData data)
    {
        var counts = data.Channels
            .SelectMany(channel => channel)
            .Where(static count => count > 0)
            .OrderBy(static count => count)
            .ToArray();

        if (counts.Length == 0)
            return 0;

        var index = (int)Math.Clamp(counts.Length * ClipPercentile, 0, counts.Length - 1);
        var ceiling = counts[index];

        return ceiling > 0 ? ceiling : data.Max;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
