using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Keeps the part of an image a mask marks, and replaces the rest.
/// </summary>
/// <remarks>
/// <para>
/// The mask can be a binary image - the output of a threshold, a colour
/// range or a logic node - or a shape set, which is filled in. The second
/// is what makes a found feature usable as a region: locate the board with
/// Find Blobs, mask to it, and everything downstream sees only the board.
/// </para>
/// <para>
/// Distinct from Region of Interest, which <em>cuts</em> a rectangle out
/// and changes the frame's size and coordinates. This keeps the frame
/// exactly as it is, so everything measured afterwards is still in the
/// coordinates of the original image. That difference is the whole reason
/// to have both.
/// </para>
/// </remarks>
[VisionNode(
    "Mask",
    TypeKey = "combine.mask",
    Category = NodeCategory.Combine,
    Icon = "ph_mask",
    Description = "Keeps what a mask image or a shape marks; fills the rest.")]
public sealed class MaskNode : VisionNode
{
    private int _fill;
    private bool _invert;
    private int _thickness = 1;

    public MaskNode()
    {
        ImageIn = AddInput<ImageData>("In");
        MaskIn = AddInput<ImageData>("Mask", isRequired: false);
        ShapesIn = AddInput<ShapeSet>("Shapes", isRequired: false);
        ImageOut = AddOutput<ImageData>("Out");
        MaskOut = AddOutput<ImageData>("Mask");
    }

    public InputPort ImageIn { get; }

    /// <summary>Any non-zero pixel marks what to keep.</summary>
    public InputPort MaskIn { get; }

    /// <summary>Geometry to fill in, as an alternative or an addition.</summary>
    public InputPort ShapesIn { get; }

    public OutputPort ImageOut { get; }

    /// <summary>
    /// The mask actually used, so it can be looked at or combined further.
    /// </summary>
    /// <remarks>
    /// Worth emitting because a mask built from shapes is invisible
    /// otherwise, and a mask that is subtly wrong looks exactly like a
    /// detection that is subtly wrong.
    /// </remarks>
    public OutputPort MaskOut { get; }

    [Param(
        "Fill",
        Min = 0,
        Max = 255,
        Editor = ParamEditor.Slider,
        Description = "What the masked-out pixels become.")]
    public int Fill
    {
        get => _fill;
        set => SetParam(ref _fill, Math.Clamp(value, 0, 255));
    }

    [Param("Invert", Description = "Keep what the mask does not mark instead.")]
    public bool Invert
    {
        get => _invert;
        set => SetParam(ref _invert, value);
    }

    [Param(
        "Stroke",
        Group = "Shapes",
        Min = 1,
        Max = 101,
        Editor = ParamEditor.Slider,
        Description = "How wide a line or a point is drawn. Shapes with an " +
                      "inside are filled and ignore this.")]
    public int Thickness
    {
        get => _thickness;
        set => SetParam(ref _thickness, Math.Max(1, value));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;
        var wiredMask = context.Get<ImageData>(MaskIn);
        var shapes = context.Get<ShapeSet>(ShapesIn);

        if (wiredMask is null && shapes is null)
        {
            throw new InvalidOperationException(
                "Mask needs something to mask with: wire a binary image into Mask, " +
                "or a shape set into Shapes.");
        }

        using var mask = BuildMask(source, wiredMask, shapes, context);

        var destination = new Mat();

        try
        {
            // Filled everywhere first, then the kept part copied over it.
            // The alternative - copying the source and then clearing the
            // rest - needs the inverse mask as well, for the same result.
            destination.Create(source.Height, source.Width, source.Depth, source.Channels);
            destination.SetTo(new MCvScalar(_fill, _fill, _fill, _fill));
            source.Mat.CopyTo(destination, mask);
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        context.Set(ImageOut, ImageData.Adopt(destination, source.Space));
        context.Set(MaskOut, ImageData.CopyOf(mask, ColourSpace.Gray));
    }

    /// <summary>
    /// The mask to keep, as a single-channel 0-or-255 image the size of the
    /// source.
    /// </summary>
    private Mat BuildMask(
        ImageData source, ImageData? wired, ShapeSet? shapes, EvalContext context)
    {
        var mask = new Mat(source.Height, source.Width, DepthType.Cv8U, 1);

        try
        {
            mask.SetTo(new MCvScalar(0));

            if (wired is not null)
                AddWiredMask(mask, wired, source, context);

            if (shapes is not null)
                AddShapes(mask, shapes, source, context);

            if (_invert)
                CvInvoke.BitwiseNot(mask, mask);

            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static void AddWiredMask(
        Mat mask, ImageData wired, ImageData source, EvalContext context)
    {
        if (wired.Width != source.Width || wired.Height != source.Height)
        {
            throw new InvalidOperationException(
                $"The mask is {wired.Width}x{wired.Height} but the image is " +
                $"{source.Width}x{source.Height}. They have to match, or the mask " +
                "would mark the wrong pixels.");
        }

        using var single = new Mat();

        // Any non-zero pixel marks. Reducing a colour mask by its maximum
        // rather than its luminance, so a mask drawn in pure blue counts
        // for as much as one drawn in white.
        if (wired.Channels > 1)
        {
            using var channels = new Emgu.CV.Util.VectorOfMat();
            CvInvoke.Split(wired.Mat, channels);
            channels[0].CopyTo(single);

            for (var i = 1; i < channels.Size; i++)
                CvInvoke.Max(single, channels[i], single);
        }
        else
        {
            wired.Mat.CopyTo(single);
        }

        using var marked = new Mat();
        CvInvoke.Compare(single, new ScalarArray(new MCvScalar(0)), marked, CmpType.NotEqual);
        CvInvoke.BitwiseOr(mask, marked, mask);

        if (CvInvoke.CountNonZero(marked) == 0)
            context.Warn("The mask image is entirely zero, so nothing is kept.");
    }

    private void AddShapes(Mat mask, ShapeSet shapes, ImageData source, EvalContext context)
    {
        if (!shapes.MatchesFrame(source.Width, source.Height))
        {
            context.Warn(
                $"The shapes were measured on a {shapes.FrameWidth} x {shapes.FrameHeight} " +
                "image but this one is a different size, so the mask will not sit where " +
                "they were found.");
        }

        using var drawn = ShapeRaster.Fill(shapes, source.Width, source.Height, _thickness);
        CvInvoke.BitwiseOr(mask, drawn, mask);
    }
}
