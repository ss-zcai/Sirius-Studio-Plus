using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Sirius.Vision;

namespace Sirius.Studio.App.Controls;

/// <summary>One profile on a chart, with the name its legend shows.</summary>
public sealed record ChartSeries(string Label, ProfileData Data);

/// <summary>
/// Plots a line intensity profile against labelled axes.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the DevExpress <c>lineProfileChartControl</c>, which was fed by
/// clearing and refilling a series point-by-point on every selection change.
/// </para>
/// <para>
/// Axes matter more here than on most charts: the question asked of a profile
/// is usually "how dark is that, and how wide is it", and an unlabelled trace
/// answers neither. The vertical axis is intensity, the horizontal one is
/// distance along the line in pixels.
/// </para>
/// </remarks>
public sealed class ProfileControl : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(ProfileData),
        typeof(ProfileControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Several profiles to draw over each other. With more than one, this
    /// is what is drawn and <see cref="Data"/> is ignored.
    /// </summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IReadOnlyList<ChartSeries>),
        typeof(ProfileControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Room for the intensity labels, in device-independent pixels.</summary>
    private const double AxisGutter = 46;

    /// <summary>Room for the distance labels below the plot.</summary>
    private const double AxisFooter = 16;

    /// <summary>Room for the summary line above the plot.</summary>
    private const double HeaderHeight = 15;

    /// <summary>Extra room above the plot when there is a legend to show.</summary>
    private const double LegendHeight = 14;

    private static readonly Pen LinePen = FrozenPen(Color.FromRgb(0x60, 0xA5, 0xFA), 1.4);

    /// <summary>
    /// A pen per channel name.
    /// </summary>
    /// <remarks>
    /// Drawing the red channel in red is the whole point of plotting three
    /// traces: with one legend to read, the reader should not then have to
    /// consult it for every peak. The tones are lightened towards the panel
    /// rather than pure - saturated blue on a dark background is barely
    /// legible, and pure green reads as much brighter than the other two.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, Pen> ChannelPens =
        new Dictionary<string, Pen>(StringComparer.Ordinal)
        {
            ["R"] = FrozenPen(Color.FromRgb(0xF8, 0x71, 0x71), 1.4),
            ["G"] = FrozenPen(Color.FromRgb(0x4A, 0xDE, 0x80), 1.4),
            ["B"] = FrozenPen(Color.FromRgb(0x60, 0xA5, 0xFA), 1.4),
            ["A"] = FrozenPen(Color.FromRgb(0x9A, 0xA0, 0xA6), 1.4),
        };

    /// <summary>
    /// A pen per trace when profiles are overlaid: distinct hues, lightened
    /// for the dark panel as the channel pens are, blue first so a lone
    /// profile looks as it always did.
    /// </summary>
    private static readonly Pen[] SeriesPens =
    [
        FrozenPen(Color.FromRgb(0x60, 0xA5, 0xFA), 1.4),
        FrozenPen(Color.FromRgb(0xFB, 0xBF, 0x24), 1.4),
        FrozenPen(Color.FromRgb(0x4A, 0xDE, 0x80), 1.4),
        FrozenPen(Color.FromRgb(0xF8, 0x71, 0x71), 1.4),
        FrozenPen(Color.FromRgb(0xC0, 0x84, 0xFC), 1.4),
        FrozenPen(Color.FromRgb(0x22, 0xD3, 0xEE), 1.4),
        FrozenPen(Color.FromRgb(0xF4, 0x72, 0xB6), 1.4),
        FrozenPen(Color.FromRgb(0x9A, 0xA0, 0xA6), 1.4),
    ];

    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));

    private static readonly Pen CursorPen = FrozenPen(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Brush ReadoutBrush = FrozenBrush(Color.FromArgb(0xE6, 0x1A, 0x1B, 0x1E));
    private static readonly Brush ReadoutTextBrush = FrozenBrush(Color.FromRgb(0xE6, 0xE8, 0xEA));

    private static readonly Typeface LabelTypeface = new("Consolas");

    /// <summary>Where the pointer is, in control pixels, or null when it is away.</summary>
    private double? _cursorX;

    public ProfileData? Data
    {
        get => (ProfileData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
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

        // Transparent fill so the control still takes mouse input and so the
        // panel behind it shows through.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (Series is { Count: > 1 } series)
        {
            RenderOverlay(drawingContext, series, width, height);
            return;
        }

        var data = Data;
        if (data is null || data.Length < 2)
        {
            DrawLabel(drawingContext, "No profile - draw a line on an image, or wire one into a Line Chart", 4, 4);
            return;
        }

        if (data.ValidCount == 0)
        {
            DrawLabel(drawingContext, data.IsLine ? "The line lies outside the image" : "Nothing was measured", 4, 4);
            return;
        }

        DrawLabel(drawingContext, Summarise(data), 4, 1);

        // A single channel needs no legend: its statistics are in the header
        // and there is nothing to tell apart.
        var legend = data.Channels.Count > 1;
        var header = HeaderHeight + (legend ? LegendHeight : 0);

        if (legend)
            DrawLegend(drawingContext, data, 4, HeaderHeight);

        var plot = new Rect(
            AxisGutter,
            header,
            Math.Max(1, width - AxisGutter - 6),
            Math.Max(1, height - header - AxisFooter));

        var (low, high) = IntensityRange(data);

        DrawIntensityAxis(drawingContext, plot, low, high);
        DrawDistanceAxis(drawingContext, plot, data.XOrigin, data.SampledLength);

        foreach (var channel in data.Channels)
            DrawTrace(drawingContext, plot, channel, data, data.XOrigin, data.SampledLength, low, high, PenFor(channel.Name));

        DrawCursor(drawingContext, plot, data);
    }

    /// <summary>One line on an overlaid chart: a channel of one profile, with its name and pen.</summary>
    private sealed record Trace(string Name, ProfileChannel Channel, ProfileData Source, Pen Pen);

    /// <summary>
    /// Several profiles over each other, on one horizontal axis spanning
    /// them all.
    /// </summary>
    /// <remarks>
    /// Each profile is placed by its own origin and spacing rather than by
    /// sample index, so a ring read with 72 calipers and one read with 36
    /// line up angle for angle, and an arc lies over the part of a full ring
    /// it covers.
    /// </remarks>
    private void RenderOverlay(DrawingContext context, IReadOnlyList<ChartSeries> series, double width, double height)
    {
        var traces = series
            .Where(entry => entry.Data.Length >= 2)
            .SelectMany(entry => entry.Data.Channels.Select(channel => (entry, channel)))
            .Select((pair, i) => new Trace(
                pair.entry.Data.Channels.Count == 1 ? pair.entry.Label : $"{pair.entry.Label} {pair.channel.Name}",
                pair.channel,
                pair.entry.Data,
                SeriesPens[i % SeriesPens.Length]))
            .ToArray();

        var measured = traces.Where(trace => trace.Channel.ValidCount > 0).ToArray();

        if (measured.Length == 0)
        {
            DrawLabel(context, "Nothing was measured", 4, 4);
            return;
        }

        var origin = traces.Min(trace => trace.Source.XOrigin);
        var end = traces.Max(trace => trace.Source.XOrigin + trace.Source.SampledLength);
        var length = Math.Max(end - origin, 1e-6);

        var units = traces.Select(trace => trace.Source.XUnit).Distinct().ToArray();

        var header = string.Format(
            CultureInfo.CurrentCulture,
            "{0} profiles   span {1:0.#}-{2:0.#}{3}",
            series.Count,
            origin,
            end,
            units[0]);

        // Overlaying degrees on pixels draws, but means nothing; say so
        // rather than leave the axis to look as though it fitted both.
        if (units.Length > 1)
            header += "   mixed units: " + string.Join(", ", units);

        DrawLabel(context, header, 4, 1);

        var legendRows = DrawSeriesLegend(context, traces, 4, HeaderHeight, width);
        var top = HeaderHeight + (legendRows * LegendHeight);

        var plot = new Rect(
            AxisGutter,
            top,
            Math.Max(1, width - AxisGutter - 6),
            Math.Max(1, height - top - AxisFooter));

        double low = measured.Min(trace => trace.Channel.Min);
        double high = measured.Max(trace => trace.Channel.Max);

        if (high - low < 1e-6)
        {
            low -= 1;
            high += 1;
        }

        var step = NiceStep(high - low, 4);
        low = Math.Floor(low / step) * step;
        high = Math.Ceiling(high / step) * step;

        DrawIntensityAxis(context, plot, low, high);
        DrawDistanceAxis(context, plot, origin, length);

        foreach (var trace in traces)
            DrawTrace(context, plot, trace.Channel, trace.Source, origin, length, low, high, trace.Pen);

        DrawOverlayCursor(context, plot, traces, origin, length, units[0]);
    }

    /// <summary>
    /// A swatch, name and range per trace, wrapping onto further rows when
    /// they do not fit across.
    /// </summary>
    /// <returns>How many rows it took.</returns>
    private int DrawSeriesLegend(DrawingContext context, IReadOnlyList<Trace> traces, double left, double y, double width)
    {
        var x = left;
        var rows = 1;

        foreach (var trace in traces)
        {
            var format = trace.Source.ValueFormat;
            var text = Format(
                trace.Channel.ValidCount == 0
                    ? $"{trace.Name} -"
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        "{0} {1} to {2}",
                        trace.Name,
                        trace.Channel.Min.ToString(format, CultureInfo.CurrentCulture),
                        trace.Channel.Max.ToString(format, CultureInfo.CurrentCulture)));

            if (x > left && x + 10 + text.Width > width)
            {
                x = left;
                y += LegendHeight;
                rows++;
            }

            context.DrawRectangle(trace.Pen.Brush, null, new Rect(x, y + 4, 7, 3));
            context.DrawText(text, new Point(x + 10, y));
            x += 10 + text.Width + 14;
        }

        return rows;
    }

    /// <summary>
    /// The cursor over overlaid profiles: each reads its own nearest sample,
    /// since their samples need not fall at the same places.
    /// </summary>
    private void DrawOverlayCursor(
        DrawingContext context, Rect plot, IReadOnlyList<Trace> traces, double origin, double length, string unit)
    {
        if (_cursorX is not { } pointer || pointer < plot.Left - 2 || pointer > plot.Right + 2)
            return;

        var at = origin + (Math.Clamp(pointer - plot.Left, 0, plot.Width) / plot.Width * length);

        // Snapped to the first trace's sample where it has one there, so
        // with profiles that share an axis the line sits on the readings.
        var first = traces[0].Source;
        var nearest = NearestIndex(first, at);
        if (nearest is { } index)
            at = first.DistanceAt(index);

        var x = plot.Left + ((at - origin) / length * plot.Width);
        context.DrawLine(CursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        var lines = new List<(string Text, Brush Brush)>
        {
            (WithUnit(at.ToString("0.#", CultureInfo.CurrentCulture), unit), ReadoutTextBrush),
        };

        foreach (var trace in traces)
        {
            var sample = NearestIndex(trace.Source, at) is { } i ? trace.Channel.Samples[i] : float.NaN;

            lines.Add((
                float.IsFinite(sample)
                    ? $"{trace.Name} {sample.ToString(trace.Source.ValueFormat, CultureInfo.CurrentCulture)}"
                    : $"{trace.Name} -",
                trace.Pen.Brush));
        }

        DrawReadout(context, plot, x, lines);
    }

    /// <summary>The sample of <paramref name="data"/> nearest a place on the axis, or null when it does not reach there.</summary>
    private static int? NearestIndex(ProfileData data, double at)
    {
        if (data.Length == 0 || data.Spacing <= 0)
            return null;

        var index = (int)Math.Round((at - data.XOrigin) / data.Spacing);
        return index >= 0 && index < data.Length ? index : null;
    }

    /// <summary>
    /// The intensity span the plot covers, padded out to tick boundaries.
    /// </summary>
    /// <remarks>
    /// Taken from the data rather than fixed at 0-255, because the profile may
    /// come from a 16-bit or float image where that range would be meaningless.
    /// A flat profile is given an arbitrary span so it draws as a centre line
    /// instead of collapsing onto the axis.
    /// </remarks>
    private static (double Low, double High) IntensityRange(ProfileData data)
    {
        double low = data.Min;
        double high = data.Max;

        if (high - low < 1e-6)
        {
            low -= 1;
            high += 1;
        }

        var step = NiceStep(high - low, 4);

        return (Math.Floor(low / step) * step, Math.Ceiling(high / step) * step);
    }

    /// <summary>
    /// A tick interval that lands on 1, 2 or 5 times a power of ten.
    /// </summary>
    /// <remarks>
    /// Dividing the range into equal parts gives labels like 37.4 and 74.8,
    /// which are hard to read a value off. Rounding the interval instead keeps
    /// the labels to numbers the eye can interpolate between.
    /// </remarks>
    private static double NiceStep(double range, int targetTicks)
    {
        if (range <= 0)
            return 1;

        var rough = range / Math.Max(1, targetTicks);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalised = rough / magnitude;

        var step = normalised switch
        {
            <= 1 => 1.0,
            <= 2 => 2.0,
            <= 5 => 5.0,
            _ => 10.0,
        };

        return step * magnitude;
    }

    private void DrawIntensityAxis(DrawingContext context, Rect plot, double low, double high)
    {
        var step = NiceStep(high - low, 4);

        context.DrawLine(AxisPen, plot.TopLeft, plot.BottomLeft);

        for (var value = low; value <= high + (step / 2); value += step)
        {
            var y = plot.Bottom - ((value - low) / (high - low) * plot.Height);

            if (y < plot.Top - 0.5 || y > plot.Bottom + 0.5)
                continue;

            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = Format(value.ToString(step < 1 ? "0.##" : "0", CultureInfo.CurrentCulture));

            // Right-aligned against the axis, centred on its gridline.
            context.DrawText(text, new Point(plot.Left - 5 - text.Width, y - (text.Height / 2)));
        }
    }

    private void DrawDistanceAxis(DrawingContext context, Rect plot, double origin, double length)
    {
        if (length <= 0)
            return;

        var step = NiceStep(length, 6);

        context.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);

        // Ticks on round values of the axis itself, which for a ring that
        // starts at 30 degrees means 60, 90, ... rather than 30 + 30, 30 + 60.
        for (var distance = Math.Ceiling(origin / step) * step; distance <= origin + length + (step / 2); distance += step)
        {
            var x = plot.Left + ((distance - origin) / length * plot.Width);

            if (x > plot.Right + 0.5)
                continue;

            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var text = Format(distance.ToString(step < 1 ? "0.##" : "0", CultureInfo.CurrentCulture));
            var left = Math.Min(x - (text.Width / 2), plot.Right - text.Width);

            context.DrawText(text, new Point(Math.Max(0, left), plot.Bottom + 2));
        }
    }

    private static Pen PenFor(string channel) =>
        ChannelPens.TryGetValue(channel, out var pen) ? pen : LinePen;

    /// <summary>
    /// One channel as a line, each sample placed by where it is on the
    /// axis - <paramref name="origin"/> at the left of the plot,
    /// <paramref name="origin"/> + <paramref name="length"/> at the right.
    /// </summary>
    private static void DrawTrace(
        DrawingContext context,
        Rect plot,
        ProfileChannel channel,
        ProfileData source,
        double origin,
        double length,
        double low,
        double high,
        Pen pen)
    {
        var span = high - low;

        if (length <= 0)
            return;

        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            var drawing = false;

            for (var i = 0; i < channel.Samples.Length; i++)
            {
                var value = channel.Samples[i];

                // A gap is where the line ran outside the image. Breaking the
                // figure leaves it blank, rather than drawing a straight run
                // between two points across values that were never measured.
                if (!float.IsFinite(value))
                {
                    drawing = false;
                    continue;
                }

                var point = new Point(
                    plot.Left + ((source.DistanceAt(i) - origin) / length * plot.Width),
                    plot.Bottom - ((value - low) / span * plot.Height));

                if (drawing)
                {
                    ctx.LineTo(point, true, false);
                }
                else
                {
                    ctx.BeginFigure(point, false, false);
                    drawing = true;
                }
            }
        }

        geometry.Freeze();

        context.PushClip(new RectangleGeometry(plot));
        context.DrawGeometry(null, pen, geometry);
        context.Pop();
    }

    /// <summary>
    /// A swatch and the statistics for each channel, below the header.
    /// </summary>
    /// <remarks>
    /// Per-channel figures live here rather than in the header because three
    /// sets of min, max and mean on one line would not fit, and because the
    /// swatch is what ties a figure to a trace.
    /// </remarks>
    private void DrawLegend(DrawingContext context, ProfileData data, double x, double y)
    {
        foreach (var channel in data.Channels)
        {
            var pen = PenFor(channel.Name);

            context.DrawRectangle(pen.Brush, null, new Rect(x, y + 4, 7, 3));
            x += 10;

            var text = Format(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} {1:0.#}-{2:0.#} mean {3:0.#}",
                    channel.Name,
                    channel.Min,
                    channel.Max,
                    channel.Mean));

            context.DrawText(text, new Point(x, y));
            x += text.Width + 14;
        }
    }

    /// <summary>
    /// A line at the sample under the pointer, and what it reads.
    /// </summary>
    /// <remarks>
    /// Snapped to a sample rather than drawn where the pointer is, so the
    /// numbers shown are measurements rather than something interpolated
    /// between two of them. A chart that answers "roughly 90" is a picture;
    /// one that answers "93.3 at 412 px" is an instrument.
    /// </remarks>
    private void DrawCursor(DrawingContext context, Rect plot, ProfileData data)
    {
        if (_cursorX is not { } pointer || data.Length < 2)
            return;

        if (pointer < plot.Left - 2 || pointer > plot.Right + 2)
            return;

        var index = SampleAt(pointer, plot, data.Length);
        var x = plot.Left + (index * (plot.Width / (data.Length - 1)));

        context.DrawLine(CursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        var lines = new List<(string Text, Brush Brush)>
        {
            (WithUnit(data.DistanceAt(index).ToString("0.#", CultureInfo.CurrentCulture), data.XUnit), ReadoutTextBrush),
        };

        foreach (var channel in data.Channels)
        {
            var sample = channel.Samples[index];

            lines.Add((
                float.IsFinite(sample)
                    ? $"{channel.Name} {sample.ToString(data.ValueFormat, CultureInfo.CurrentCulture)}"
                    : $"{channel.Name} -",
                PenFor(channel.Name).Brush));
        }

        DrawReadout(context, plot, x, lines);
    }

    /// <summary>
    /// The sample nearest a position along the plot.
    /// </summary>
    /// <remarks>
    /// Rounded, not truncated: truncating puts the readout a sample behind
    /// the line for the whole right-hand half of every step, which looks
    /// like a lag rather than an error and so goes unnoticed.
    /// </remarks>
    internal static int SampleAt(double x, Rect plot, int count)
    {
        if (count < 2 || plot.Width <= 0)
            return 0;

        var step = plot.Width / (count - 1);
        return (int)Math.Clamp(Math.Round((x - plot.Left) / step), 0, count - 1);
    }

    /// <summary>
    /// A small panel beside the cursor, flipped to the other side when it
    /// would otherwise run off the plot.
    /// </summary>
    private void DrawReadout(
        DrawingContext context, Rect plot, double x, IReadOnlyList<(string Text, Brush Brush)> lines)
    {
        const double padding = 4;
        const double gap = 8;

        var formatted = lines.Select(line => Format(line.Text, line.Brush)).ToArray();
        var width = formatted.Max(text => text.Width) + (2 * padding);
        var height = formatted.Sum(text => text.Height) + (2 * padding);

        var left = x + gap;
        if (left + width > plot.Right)
            left = x - gap - width;

        left = Math.Max(plot.Left, left);
        var top = Math.Max(plot.Top, plot.Top + 2);

        context.DrawRectangle(ReadoutBrush, null, new Rect(left, top, width, height));

        var y = top + padding;
        foreach (var text in formatted)
        {
            context.DrawText(text, new Point(left + padding, y));
            y += text.Height;
        }
    }

    /// <summary>
    /// The header line. Sampling settings appear only when they differ from the
    /// default, so the common case stays short.
    /// </summary>
    private static string Summarise(ProfileData data)
    {
        // A line has a length in the image; anything else has a span on its
        // own axis - 0 to 355 degrees round a ring.
        var extent = data.IsLine
            ? string.Format(CultureInfo.CurrentCulture, "len {0:0.#}px", data.Distance)
            : string.Format(
                CultureInfo.CurrentCulture,
                "span {0:0.#}-{1:0.#}{2}",
                data.XOrigin,
                data.XOrigin + data.SampledLength,
                data.XUnit);

        // With one channel its statistics belong here, where they always
        // were. With several they belong beside their swatch in the legend,
        // and this line keeps to what the channels have in common.
        var text = data.Channels.Count == 1
            ? string.Format(
                CultureInfo.CurrentCulture,
                "n {0}   min {1}   max {2}   mean {3}   {4}",
                data.Length,
                data.Channels[0].Min.ToString(data.ValueFormat, CultureInfo.CurrentCulture),
                data.Channels[0].Max.ToString(data.ValueFormat, CultureInfo.CurrentCulture),
                data.Channels[0].Mean.ToString(data.ValueFormat, CultureInfo.CurrentCulture),
                extent)
            : string.Format(
                CultureInfo.CurrentCulture,
                "n {0}   {1} channels   {2}",
                data.Length,
                data.Channels.Count,
                extent);

        if (Math.Abs(data.Spacing - 1) > 1e-9)
            text += string.Format(CultureInfo.CurrentCulture, "   step {0:0.###}{1}", data.Spacing, data.XUnit);

        if (data.Thickness > 1)
            text += string.Format(CultureInfo.CurrentCulture, "   band {0}px", data.Thickness);

        if (data.HasGaps)
        {
            text += string.Format(
                CultureInfo.CurrentCulture,
                "   {0} {1}",
                data.Length - data.ValidCount,
                data.GapLabel);
        }

        return text;
    }

    /// <summary>A number with its unit: "12 px", but "45°" - a degree sign sits against its number.</summary>
    private static string WithUnit(string number, string unit) =>
        unit is "°" or "" ? number + unit : $"{number} {unit}";

    private void DrawLabel(DrawingContext drawingContext, string text, double x, double y) =>
        drawingContext.DrawText(Format(text), new Point(x, y));

    private FormattedText Format(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        10,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private FormattedText Format(string text) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        10,
        LabelBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
