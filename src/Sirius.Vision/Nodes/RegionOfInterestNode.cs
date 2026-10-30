using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Sirius.Vision.Nodes;

/// <summary>What the region does to the pixels it cuts out.</summary>
public enum RoiAlignment
{
    /// <summary>
    /// Rotate the region upright, interpolating. The output is exactly the
    /// region's size, whatever angle it sat at.
    /// </summary>
    Straighten,

    /// <summary>
    /// Cut the smallest upright box containing the region, without
    /// interpolating. Nothing is resampled, so nothing is softened.
    /// </summary>
    BoundingBox,
}

/// <summary>
/// Cuts a region out of an image, optionally rotating it upright.
/// </summary>
/// <remarks>
/// <para>
/// The two modes follow HALCON's <c>crop_rectangle2</c>: aligned to the axes,
/// which must interpolate and fills outside the image with the fill value; or
/// the smallest surrounding upright box, which resamples nothing. The
/// straightening itself is what Cognex's Affine Transform tool does - take a
/// tilted region and hand the next tool an upright picture of it.
/// </para>
/// <para>
/// The region can come from a wire instead of the parameters. Feed it the
/// shape of a located part - Find Blobs, then Select Shape - and the cut
/// follows the part from frame to frame. That is the heart of what Cognex
/// calls fixturing, without needing a coordinate-space system to express it.
/// </para>
/// <para>
/// The region is reported on the Shapes output, so a viewer can show where
/// the cut was taken from on the original image.
/// </para>
/// </remarks>
[VisionNode(
    "Region of Interest",
    TypeKey = "basic.roi",
    Category = NodeCategory.Basic,
    Icon = "ph_roi",
    Description = "Cuts out a region, rotating it upright when it sits at an angle.")]
public sealed class RegionOfInterestNode : VisionNode
{
    private OrientedRect _rect = new(100, 100, 200, 100, 0);
    private double _scale = 1;
    private RoiAlignment _alignment = RoiAlignment.Straighten;
    private Inter _interpolation = Inter.Linear;
    private int _fill;

    public RegionOfInterestNode()
    {
        ImageIn = AddInput<ImageData>("In");
        RegionIn = AddInput<ShapeSet>("Region", isRequired: false);
        OverlayIn = AddInput<ShapeSet>("Overlay", isRequired: false);
        ImageOut = AddOutput<ImageData>("Out");
        ShapesOut = AddOutput<ShapeSet>("Shapes");
        OverlayOut = AddOutput<ShapeSet>("Overlay");
    }

    /// <summary>Shapes found on the input image, to be placed on the cut-out.</summary>
    public InputPort OverlayIn { get; }

    /// <summary>The same shapes, turned and moved with the region onto the cut-out.</summary>
    public OutputPort OverlayOut { get; }

    public InputPort ImageIn { get; }

    /// <summary>
    /// A shape to take the region from, instead of the parameters below.
    /// </summary>
    /// <remarks>
    /// Any shape will do: its minimum-area rectangle is used, so a located
    /// blob can drive the cut directly.
    /// </remarks>
    public InputPort RegionIn { get; }

    public OutputPort ImageOut { get; }

    /// <summary>Where the region was taken from, for drawing on the source image.</summary>
    public OutputPort ShapesOut { get; }

    [Param(
        "Rect",
        Group = "Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "The region to cut, as centre x, centre y, width, height " +
                      "and angle in degrees. Draw it on the input image with the " +
                      "button beside it.")]
    public OrientedRect Rect
    {
        get => _rect;
        set => SetParam(ref _rect, value);
    }

    [Param("Alignment",
        Group = "Output",
        Description = "Straighten rotates the region upright and must interpolate; "
                      + "Bounding Box cuts the surrounding upright box and resamples nothing.")]
    public RoiAlignment Alignment
    {
        get => _alignment;
        set => SetParam(ref _alignment, value);
    }

    [Param("Scale",
        Group = "Output",
        Min = 0.1,
        Max = 8,
        Step = 0.1,
        Description = "Enlarges or shrinks the cut-out. Only applies when straightening.")]
    public double Scale
    {
        get => _scale;
        set => SetParam(ref _scale, Math.Clamp(value, 0.1, 8));
    }

    [Param("Interpolation",
        Group = "Output",
        Description = "How pixels are reconstructed when straightening.")]
    public Inter Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    [Param("Fill",
        Group = "Output",
        Min = 0,
        Max = 255,
        Description = "Grey level for parts of the region that fall outside the image.")]
    public int Fill
    {
        get => _fill;
        set => SetParam(ref _fill, Math.Clamp(value, 0, 255));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;
        var region = ResolveRegion(context, source);

        context.Set(
            ShapesOut,
            new ShapeSet([region], source.Width, source.Height));

        ShapeMap map;
        var cut = _alignment == RoiAlignment.Straighten
            ? Straighten(source, region, context, out map)
            : CutBoundingBox(source, region, context, out map);

        context.Set(ImageOut, ImageData.Adopt(cut));
        Overlays.Carry(context, OverlayIn, OverlayOut, map, source, cut.Width, cut.Height);
    }

    /// <summary>
    /// The region to cut: from the wire when one is attached, otherwise from
    /// the parameters.
    /// </summary>
    private OrientedRectShape ResolveRegion(EvalContext context, ImageData source)
    {
        if (context.Get<ShapeSet>(RegionIn) is { Count: > 0 } wired)
        {
            var shape = wired.Shapes[0];

            if (wired.Count > 1)
                context.Warn($"{wired.Count} shapes on the Region input; used the first.");

            if (!wired.MatchesFrame(source.Width, source.Height))
            {
                context.Warn(
                    $"The region was measured on a {wired.FrameWidth} x {wired.FrameHeight} image "
                    + "but this one is a different size, so it will not sit where it was found.");
            }

            if (shape is OrientedRectShape oriented)
                return oriented;

            var bounds = shape.Bounds;

            return new OrientedRectShape(
                bounds.Centre,
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height),
                0);
        }

        var rect = context.Param(nameof(Rect), _rect);

        return new OrientedRectShape(
            new Point2(rect.CentreX, rect.CentreY),
            rect.Width,
            rect.Height,
            rect.Angle);
    }

    /// <summary>
    /// Rotates the region upright into an output of exactly its size.
    /// </summary>
    /// <remarks>
    /// The affine goes straight from source to output, rather than rotating
    /// the whole image and then cropping. On a large frame that is the
    /// difference between warping a few hundred pixels and warping millions.
    /// </remarks>
    private Mat Straighten(ImageData source, OrientedRectShape region, EvalContext context, out ShapeMap map)
    {
        var width = Math.Max(1, (int)Math.Round(region.Width * _scale));
        var height = Math.Max(1, (int)Math.Round(region.Height * _scale));

        var centre = new System.Drawing.PointF((float)region.Centre.X, (float)region.Centre.Y);

        using var affine = new Matrix<double>(2, 3);
        CvInvoke.GetRotationMatrix2D(centre, region.AngleDegrees, _scale, affine);

        // Shift the result so the region's centre lands in the middle of the
        // output rather than staying where it was in the source.
        affine.Data[0, 2] += (width / 2.0) - region.Centre.X;
        affine.Data[1, 2] += (height / 2.0) - region.Centre.Y;

        // The very matrix the pixels go through, so the shapes go the same way.
        var m = affine.Data;
        map = ShapeMap.Affine(m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2]);

        var destination = new Mat();

        try
        {
            CvInvoke.WarpAffine(
                source.Mat,
                destination,
                affine,
                new System.Drawing.Size(width, height),
                _interpolation,
                Warp.Default,
                BorderType.Constant,
                new MCvScalar(_fill, _fill, _fill, _fill));

            WarnIfOutside(source, region, context);

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Cuts the smallest upright box containing the region, clipped to the
    /// image. Nothing is resampled.
    /// </summary>
    private Mat CutBoundingBox(ImageData source, OrientedRectShape region, EvalContext context, out ShapeMap map)
    {
        var bounds = region.Bounds;

        var left = (int)Math.Floor(bounds.X);
        var top = (int)Math.Floor(bounds.Y);
        var right = (int)Math.Ceiling(bounds.Right);
        var bottom = (int)Math.Ceiling(bounds.Bottom);

        var clipped = DrawingRectangle.FromLTRB(
            Math.Clamp(left, 0, source.Width),
            Math.Clamp(top, 0, source.Height),
            Math.Clamp(right, 0, source.Width),
            Math.Clamp(bottom, 0, source.Height));

        if (clipped.Width <= 0 || clipped.Height <= 0)
            throw new InvalidOperationException("The region lies entirely outside the image.");

        map = ShapeMap.Translation(-clipped.X, -clipped.Y);

        WarnIfOutside(source, region, context);

        // A Mat over someone else's pixels would die with the source, so the
        // region is copied out.
        using var view = new Mat(source.Mat, clipped);
        var destination = new Mat();
        view.CopyTo(destination);

        return destination;
    }

    private static void WarnIfOutside(ImageData source, OrientedRectShape region, EvalContext context)
    {
        var bounds = region.Bounds;

        if (bounds.X < 0 || bounds.Y < 0 ||
            bounds.Right > source.Width || bounds.Bottom > source.Height)
        {
            context.Warn("Part of the region lies outside the image.");
        }
    }
}
