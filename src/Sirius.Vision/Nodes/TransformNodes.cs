using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Unwraps a ring of the image into a straight strip.
/// </summary>
/// <remarks>
/// <para>
/// Useful where parts are round but the inspection is not - a label around a
/// cap, a ring of pins, the wall of a bore - which become a strip that the
/// ordinary filters and finders can work along.
/// </para>
/// <para>
/// The band is the same drawable ring Find Circle searches in, so the two are
/// set up the same way. Or it follows something found: wire in a circle and
/// the band sits on its edge, so a part that moves between frames is
/// unwrapped in the same place on it every time.
/// </para>
/// <para>
/// Angle runs left to right, from <see cref="StartAngle"/> clockwise, one
/// pixel per pixel of arc at the band's middle. Radius runs top to bottom,
/// inside to outside, one pixel per pixel.
/// </para>
/// </remarks>
[VisionNode(
    "Polar to Linear",
    TypeKey = "transform.polarToLinear",
    Category = NodeCategory.Transformation,
    Icon = "ph_polar",
    Description = "Unwraps a ring of the image into a straight strip: angle left to right, radius top to bottom. " +
                  "Draw the ring on the image, or wire in a circle to unwrap around its edge.")]
public sealed class PolarToLinearNode : VisionNode
{
    /// <summary>
    /// The largest strip made, in pixels. A ring typed with a radius of a
    /// million would otherwise ask for gigabytes.
    /// </summary>
    internal const long MaximumPixels = 50_000_000;

    private Ring _band;
    private double _bandWidth = 40;
    private double _offset;
    private double _startAngle;
    private double _sweepAngle = 360;
    private Inter _interpolation = Inter.Linear;

    public PolarToLinearNode()
    {
        ImageIn = AddInput<ImageData>("In");
        CircleIn = AddInput<ShapeSet>("Circle", isRequired: false);
        ImageOut = AddOutput<ImageData>("Out");
        BandOut = AddOutput<ShapeSet>("Band");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// A circle to unwrap around - Find Circle's, say - or a point to centre
    /// the band on.
    /// </summary>
    public InputPort CircleIn { get; }

    public OutputPort ImageOut { get; }

    /// <summary>
    /// Where the strip came from, drawn on the input: the inner and outer
    /// edges, and a line where it starts.
    /// </summary>
    public OutputPort BandOut { get; }

    public override InputPort? PrimaryInput => ImageIn;

    public override OutputPort? PrimaryOutput => ImageOut;

    // ---- where -------------------------------------------------------------

    [Param("Band",
        Editor = ParamEditor.Ring,
        Connectable = true,
        Description = "The ring to unwrap, as x, y, inner, outer. Draw it on the image: centre, then the edge, " +
                      "then the width. Left empty, it is the largest circle that fits the image. A wired point " +
                      "moves it; a wired circle replaces it.")]
    public Ring Band
    {
        get => _band;
        set => SetParam(ref _band, value);
    }

    [Param("Band Width", Group = "Around a circle", Min = 1, Max = 400, Step = 1, Connectable = true,
        Description = "With a circle wired in: how wide a band to unwrap, centred on its edge.")]
    public double BandWidth
    {
        get => _bandWidth;
        set => SetParam(ref _bandWidth, Math.Max(1, value));
    }

    [Param("Offset", Group = "Around a circle", Min = -400, Max = 400, Step = 1, Connectable = true,
        Description = "With a circle wired in: moves the band outward from its edge, or inward when negative - " +
                      "to unwrap a groove just inside a bore, say.")]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, value);
    }

    [Param("Start Angle", Group = "Ring", Min = -360, Max = 360, Step = 1,
        Description = "Where the strip starts: degrees clockwise from the positive X axis.")]
    public double StartAngle
    {
        get => _startAngle;
        set => SetParam(ref _startAngle, value);
    }

    [Param("Sweep", Group = "Ring", Min = 1, Max = 360, Step = 1,
        Description = "How much of the ring to unwrap. Less than 360 unwraps an arc.")]
    public double SweepAngle
    {
        get => _sweepAngle;
        set => SetParam(ref _sweepAngle, Math.Clamp(value, 1, 360));
    }

    [Param("Interpolation")]
    public Inter Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var image = context.Get<ImageData>(ImageIn)!;
        var band = BandFor(context, image);

        var sweep = _sweepAngle * Math.PI / 180;
        var start = _startAngle * Math.PI / 180;

        var width = Math.Max(1, (int)Math.Round(sweep * band.MeanRadius));
        var height = Math.Max(1, (int)Math.Round(band.Width));

        if ((long)width * height > MaximumPixels)
        {
            throw new InvalidOperationException(
                $"A band of radius {band.OuterRadius:0} would unwrap to {width} x {height} pixels, which is too large.");
        }

        using var mapX = new Matrix<float>(height, width);
        using var mapY = new Matrix<float>(height, width);

        // Sampled at pixel centres: a strip one pixel high takes the middle
        // of the band, not its inner edge.
        for (var row = 0; row < height; row++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var radius = band.InnerRadius + ((row + 0.5) * band.Width / height);

            for (var column = 0; column < width; column++)
            {
                var angle = start + ((column + 0.5) * sweep / width);

                // y grows downward on an image, so this turns clockwise.
                mapX.Data[row, column] = (float)(band.CentreX + (radius * Math.Cos(angle)));
                mapY.Data[row, column] = (float)(band.CentreY + (radius * Math.Sin(angle)));
            }
        }

        var strip = new Mat();

        try
        {
            // Off the image is black rather than a smear of the edge pixel,
            // so a band hanging over the border reads as missing.
            CvInvoke.Remap(image.Mat, strip, mapX, mapY, _interpolation, BorderType.Constant, new MCvScalar(0));
            context.Set(ImageOut, ImageData.Adopt(strip, image.Space));
        }
        catch
        {
            strip.Dispose();
            throw;
        }

        context.Set(BandOut, Outline(band, start, image));
    }

    /// <summary>
    /// The band this run unwraps: the drawn one, moved to a wired point, or
    /// replaced by one around a wired circle.
    /// </summary>
    internal Ring BandFor(EvalContext context, ImageData image)
    {
        var band = context.Param(nameof(Band), _band);

        // An empty band is the largest circle that fits, about the image's
        // centre - which is also what a recipe from before the band existed
        // asked for with its zeros. A band at the origin is the image
        // centre too, as those recipes' zero centre meant.
        var empty = band.OuterRadius <= 1;
        var centreX = band.CentreX == 0 && band.CentreY == 0 ? image.Width / 2.0 : band.CentreX;
        var centreY = band.CentreX == 0 && band.CentreY == 0 ? image.Height / 2.0 : band.CentreY;

        if (context.Get<ShapeSet>(CircleIn) is not { Count: > 0 } shapes)
        {
            return empty
                ? new Ring(centreX, centreY, 0, Math.Min(image.Width, image.Height) / 2.0)
                : band with { CentreX = centreX, CentreY = centreY };
        }

        if (shapes.Count > 1)
            context.Warn($"{shapes.Count} shapes arrived on Circle; the band follows the first.");

        var width = context.Param(nameof(BandWidth), _bandWidth);
        var offset = context.Param(nameof(Offset), _offset);

        switch (shapes.Shapes[0])
        {
            case CircleShape circle:
                return Ring.FromMean(circle.Centre.X, circle.Centre.Y, circle.Radius + offset, width / 2);

            // A fitted ellipse is near enough a circle for this to be the
            // useful reading of it; anything rounder than that would be a
            // circle in the first place.
            case EllipseShape ellipse:
                return Ring.FromMean(
                    ellipse.Centre.X, ellipse.Centre.Y, ((ellipse.MajorAxis + ellipse.MinorAxis) / 4) + offset, width / 2);

            // A point, or any other shape: its centre, with the band's own
            // radii - or, with none drawn, the largest circle that fits there.
            case var other:
            {
                var centre = other is PointShape point ? point.At : other.Bounds.Centre;

                if (!empty)
                    return band with { CentreX = centre.X, CentreY = centre.Y };

                var reach = Math.Min(
                    Math.Min(centre.X, image.Width - centre.X),
                    Math.Min(centre.Y, image.Height - centre.Y));

                return new Ring(centre.X, centre.Y, 0, Math.Max(1, reach));
            }
        }
    }

    /// <summary>The band drawn on the input image, so where the strip came from can be seen.</summary>
    private static ShapeSet Outline(Ring band, double start, ImageData image)
    {
        var centre = new Point2(band.CentreX, band.CentreY);

        Point2 At(double radius) => new(
            band.CentreX + (radius * Math.Cos(start)),
            band.CentreY + (radius * Math.Sin(start)));

        var shapes = new List<Shape>
        {
            new CircleShape(centre, band.OuterRadius),
            new LineShape(At(band.InnerRadius), At(band.OuterRadius)) { Label = "start" },
        };

        if (band.InnerRadius > 0)
            shapes.Insert(0, new CircleShape(centre, band.InnerRadius));

        return new ShapeSet(shapes, image.Width, image.Height);
    }
}
