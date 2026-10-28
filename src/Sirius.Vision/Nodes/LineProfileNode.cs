using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Samples intensity along a line between two points.
/// </summary>
/// <remarks>
/// <para>
/// Carries forward the old app's line intensity profile, which lived in
/// <c>MainForm.UpdateLineProfileChart</c>. That version walked a Bresenham line
/// (<c>GetPointsOnLine</c>) and read whole pixels with <c>Col(x).Row(y)</c>, so
/// a line at a shallow angle produced visible stair stepping, the sample count
/// depended on the angle, and points outside the image were skipped - silently
/// shortening the profile rather than reporting a gap.
/// </para>
/// <para>
/// The sampling itself lives in <see cref="LineProfileSource"/>, which the
/// image viewer's profile tool also uses, so a line drawn on the image and the
/// same line typed in here give identical readings.
/// </para>
/// <para>
/// Retired from the toolbox: a profile line drawn on any image viewer does
/// the same job without a node. Kept so graphs that have one still load,
/// run and show their chart.
/// </para>
/// </remarks>
[VisionNode(
    "Line Profile",
    TypeKey = "analysis.lineProfile",
    Category = NodeCategory.Analysis,
    Icon = "ph_line_profile",
    Retired = true,
    Description = "Intensity sampled along a line, for the profile panel.")]
public sealed class LineProfileNode : VisionNode
{
    private int _startX;
    private int _startY;
    private int _endX = 100;
    private int _endY = 100;
    private double _spacing = 1.0;
    private int _thickness = 1;
    private ProfileInterpolation _interpolation = ProfileInterpolation.Linear;

    public LineProfileNode()
    {
        ImageIn = AddInput<ImageData>("In");
        ProfileOut = AddOutput<ProfileData>("Profile");
    }

    public InputPort ImageIn { get; }

    public OutputPort ProfileOut { get; }

    [Param("Start X", Group = "Line")]
    public int StartX
    {
        get => _startX;
        set => SetParam(ref _startX, value);
    }

    [Param("Start Y", Group = "Line")]
    public int StartY
    {
        get => _startY;
        set => SetParam(ref _startY, value);
    }

    [Param("End X", Group = "Line")]
    public int EndX
    {
        get => _endX;
        set => SetParam(ref _endX, value);
    }

    [Param("End Y", Group = "Line")]
    public int EndY
    {
        get => _endY;
        set => SetParam(ref _endY, value);
    }

    [Param(
        "Sample Spacing",
        Group = "Sampling",
        Editor = ParamEditor.TextBox,
        Description = "Distance along the line between samples, in pixels. " +
                      "Below 1 oversamples, for locating an edge to sub-pixel precision.")]
    public double Spacing
    {
        // Clamped here rather than only in the editor, so a value arriving from
        // a saved graph cannot ask for a hundred million samples.
        get => _spacing;
        set => SetParam(
            ref _spacing,
            Math.Clamp(value, LineProfileSource.MinSpacing, LineProfileSource.MaxSpacing));
    }

    [Param(
        "Thickness",
        Group = "Sampling",
        Min = 1,
        Max = 101,
        OddOnly = true,
        Description = "Width of the band averaged perpendicular to the line. " +
                      "Wider cuts noise without moving an edge.")]
    public int Thickness
    {
        get => _thickness;
        set => SetParam(ref _thickness, Math.Max(1, value) | 1);
    }

    [Param(
        "Interpolation",
        Group = "Sampling",
        Description = "How values between pixels are reconstructed.")]
    public ProfileInterpolation Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;

        // Prepared and thrown away each pass: a node has no frame to keep it
        // alive between evaluations, unlike the viewer.
        using var prepared = LineProfileSource.From(source);

        var profile = prepared.Sample(
            new PointF(_startX, _startY),
            new PointF(_endX, _endY),
            _spacing,
            _thickness,
            _interpolation,
            context.Warn,
            context.CancellationToken);

        context.Set(ProfileOut, profile);
    }
}
