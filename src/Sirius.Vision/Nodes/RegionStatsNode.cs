using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// How bright the image is inside a region, or inside each of a set of
/// shapes: mean, spread, darkest and brightest.
/// </summary>
/// <remarks>
/// <para>
/// For the questions an edge or a blob does not answer: is the label
/// there, is the window clear, did the coating cover this patch. A drawn
/// region gives one reading; wiring shapes in - blobs, the circles Find
/// Circle found, the outlines a defect check drew - gives one per shape,
/// written onto each so a Shape Table lists them and Filter Shapes can keep
/// the dark ones.
/// </para>
/// <para>
/// A colour image is read as its brightness. Pixels outside the image are
/// not counted, so a shape hanging over the edge is measured on the part
/// of it that is there.
/// </para>
/// </remarks>
[VisionNode(
    "Region Stats",
    TypeKey = "analysis.regionStats",
    Category = NodeCategory.Analysis,
    Icon = "ph_region_stats",
    Description = "Mean, standard deviation, darkest and brightest inside a drawn region, or inside each shape " +
                  "wired in - written onto the shapes as well.")]
public sealed class RegionStatsNode : VisionNode
{
    private OrientedRect _region = new(100, 100, 200, 100, 0);
    private int _strokeWidth = 3;

    public RegionStatsNode()
    {
        ImageIn = AddInput<ImageData>("In");
        ShapesIn = AddInput<ShapeSet>("Shapes", isRequired: false);

        MeanOut = AddOutput<double>("Mean");
        StdDevOut = AddOutput<double>("Std Dev");
        MinimumOut = AddOutput<double>("Min");
        MaximumOut = AddOutput<double>("Max");
        ShapesOut = AddOutput<ShapeSet>("Shapes");
    }

    public InputPort ImageIn { get; }

    /// <summary>Shapes to measure inside, one reading each. Without them, the drawn Region is measured.</summary>
    public InputPort ShapesIn { get; }

    /// <summary>The mean inside the region, or inside the first shape.</summary>
    public OutputPort MeanOut { get; }

    public OutputPort StdDevOut { get; }

    public OutputPort MinimumOut { get; }

    public OutputPort MaximumOut { get; }

    /// <summary>The shapes - or the region - with their readings written onto them.</summary>
    public OutputPort ShapesOut { get; }

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "Where to measure when no shapes are wired in: centre x, y, width, height, angle. Draw it " +
                      "on the image.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Stroke Width", Min = 1, Max = 51, Step = 1,
        Description = "For points and open lines, which have no inside: the width of the band measured along them.")]
    public int StrokeWidth
    {
        get => _strokeWidth;
        set => SetParam(ref _strokeWidth, Math.Clamp(value, 1, 51));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var image = context.Get<ImageData>(ImageIn)!;

        var shapes = context.Get<ShapeSet>(ShapesIn) is { } wired
            ? wired
            : new ShapeSet(
                [new OrientedRectShape(
                    new Point2(_region.CentreX, _region.CentreY), _region.Width, _region.Height, _region.Angle)],
                image.Width,
                image.Height);

        if (ShapesIn.IsConnected && !shapes.MatchesFrame(image.Width, image.Height) && shapes.Count > 0)
        {
            context.Warn(
                $"The shapes were measured on a {shapes.FrameWidth} x {shapes.FrameHeight} image, not this " +
                $"{image.Width} x {image.Height} one, so they are measured in the wrong place.");
        }

        using var grey = Brightness(image);

        var measured = new List<Shape>(shapes.Count);
        var missed = 0;

        foreach (var shape in shapes.Shapes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (Measure(grey, shape, _strokeWidth) is { } reading)
            {
                var metrics = new Dictionary<string, double>(shape.Metrics, StringComparer.Ordinal)
                {
                    [ShapeMetric.Mean] = reading.Mean,
                    [ShapeMetric.StdDev] = reading.StdDev,
                    [ShapeMetric.Minimum] = reading.Min,
                    [ShapeMetric.Maximum] = reading.Max,
                };

                measured.Add(shape with { Metrics = metrics });
            }
            else
            {
                missed++;
                measured.Add(shape);
            }
        }

        if (missed > 0)
            context.Warn($"{missed} shape(s) lie outside the image and were not measured.");

        var first = measured.FirstOrDefault()?.Metrics;

        context.Set(MeanOut, first?.GetValueOrDefault(ShapeMetric.Mean) ?? 0d);
        context.Set(StdDevOut, first?.GetValueOrDefault(ShapeMetric.StdDev) ?? 0d);
        context.Set(MinimumOut, first?.GetValueOrDefault(ShapeMetric.Minimum) ?? 0d);
        context.Set(MaximumOut, first?.GetValueOrDefault(ShapeMetric.Maximum) ?? 0d);
        context.Set(ShapesOut, new ShapeSet(measured, image.Width, image.Height));
    }

    /// <summary>One shape's reading.</summary>
    public readonly record struct Reading(double Mean, double StdDev, double Min, double Max, int Pixels);

    /// <summary>
    /// The pixels of <paramref name="image"/> inside <paramref name="shape"/>,
    /// summed up - or null when none of them is inside the image.
    /// </summary>
    /// <remarks>
    /// Worked on the shape's own box rather than the whole frame: a mask the
    /// size of the image for each of three hundred blobs would cost more than
    /// finding them did.
    /// </remarks>
    public static Reading? Measure(Mat image, Shape shape, int strokeWidth)
    {
        var bounds = shape.Bounds;
        var pad = strokeWidth + 1;

        var box = Rectangle.Intersect(
            Rectangle.FromLTRB(
                (int)Math.Floor(bounds.X) - pad,
                (int)Math.Floor(bounds.Y) - pad,
                (int)Math.Ceiling(bounds.Right) + pad + 1,
                (int)Math.Ceiling(bounds.Bottom) + pad + 1),
            new Rectangle(0, 0, image.Width, image.Height));

        if (box.Width <= 0 || box.Height <= 0)
            return null;

        var local = ShapeMap.Translation(-box.X, -box.Y).Map(shape).ToArray();

        using var mask = ShapeRaster.Fill(new ShapeSet(local, box.Width, box.Height), box.Width, box.Height, strokeWidth);

        var pixels = CvInvoke.CountNonZero(mask);
        if (pixels == 0)
            return null;

        using var view = new Mat(image, box);

        var mean = new MCvScalar();
        var spread = new MCvScalar();
        CvInvoke.MeanStdDev(view, ref mean, ref spread, mask);

        double min = 0, max = 0;
        Point minAt = default, maxAt = default;
        CvInvoke.MinMaxLoc(view, ref min, ref max, ref minAt, ref maxAt, mask);

        return new Reading(mean.V0, spread.V0, min, max, pixels);
    }

    /// <summary>A single channel to measure: the image itself, or a colour image's brightness.</summary>
    private static Mat Brightness(ImageData image)
    {
        var grey = new Mat();

        switch (image.Channels)
        {
            case 3:
                CvInvoke.CvtColor(image.Mat, grey, ColorConversion.Bgr2Gray);
                break;
            case 4:
                CvInvoke.CvtColor(image.Mat, grey, ColorConversion.Bgra2Gray);
                break;
            default:
                image.Mat.CopyTo(grey);
                break;
        }

        return grey;
    }
}
