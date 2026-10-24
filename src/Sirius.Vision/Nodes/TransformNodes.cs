using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Unwraps a circular feature into a straight band.
/// </summary>
/// <remarks>
/// Useful where parts are round but the inspection is not - a rotated label or
/// a ring of pins becomes a strip that ordinary filters can work along.
/// </remarks>
[VisionNode(
    "Polar to Linear",
    TypeKey = "transform.polarToLinear",
    Category = NodeCategory.Transformation,
    Icon = "ph_polar",
    Description = "Unwraps the image around a centre point into a linear strip.")]
public sealed class PolarToLinearNode : ImageFilterNode
{
    private double _centreX;
    private double _centreY;
    private double _maxRadius;
    private Inter _interpolation = Inter.Linear;
    private Warp _warp = Warp.FillOutliers;

    /// <summary>
    /// Centre of the unwrap. Zero means the image centre, resolved per
    /// evaluation.
    /// </summary>
    /// <remarks>
    /// Sirius Studio computed these defaults in the constructor from
    /// <c>InputImage</c>, which is always null at that point - so the defaults
    /// never actually applied. Resolving at evaluation time also keeps the node
    /// correct when the image size changes upstream.
    /// </remarks>
    [Param("Centre X", Description = "0 uses the image centre.", Connectable = true)]
    public double CentreX
    {
        get => _centreX;
        set => SetParam(ref _centreX, value);
    }

    [Param("Centre Y", Description = "0 uses the image centre.", Connectable = true)]
    public double CentreY
    {
        get => _centreY;
        set => SetParam(ref _centreY, value);
    }

    [Param("Max Radius", Description = "0 fits the largest circle inside the image.", Connectable = true)]
    public double MaxRadius
    {
        get => _maxRadius;
        set => SetParam(ref _maxRadius, value);
    }

    [Param("Interpolation")]
    public Inter Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    [Param("Warp")]
    public Warp Warp
    {
        get => _warp;
        set => SetParam(ref _warp, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var centreX = context.Param(nameof(CentreX), _centreX);
        var centreY = context.Param(nameof(CentreY), _centreY);
        var radius = context.Param(nameof(MaxRadius), _maxRadius);

        if (centreX <= 0)
            centreX = source.Width / 2.0;

        if (centreY <= 0)
            centreY = source.Height / 2.0;

        if (radius <= 0)
            radius = Math.Min(source.Width, source.Height) / 2.0;

        CvInvoke.LinearPolar(
            source.Mat,
            destination,
            new PointF((float)centreX, (float)centreY),
            radius,
            _interpolation,
            _warp);
    }
}
