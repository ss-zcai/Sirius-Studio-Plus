using Clipper2Lib;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What a corner becomes when a rectangle or an outline is offset.</summary>
public enum OffsetCorners
{
    /// <summary>
    /// Kept sharp: the sides move out and meet where they did before. A
    /// rectangle stays a rectangle.
    /// </summary>
    Sharp,

    /// <summary>
    /// Rounded, every point of the new outline the offset's distance from the
    /// old one - the shape version of a dilation with a round element. A
    /// rectangle becomes an outline.
    /// </summary>
    Round,
}

/// <summary>What offsetting does to a line.</summary>
public enum OffsetLines
{
    /// <summary>Moves it sideways, parallel: positive to its right, as seen on the image going from its start to its end.</summary>
    Shift,

    /// <summary>Lengthens it at both ends; a negative offset shortens it.</summary>
    Extend,
}

/// <summary>
/// Grows or shrinks shapes by a distance: the shape version of Dilate and
/// Erode.
/// </summary>
/// <remarks>
/// <para>
/// A circle's radius changes by the offset, exactly. A rectangle's sides
/// move out by it, so its width and height change by twice the offset.
/// An outline is offset by Clipper2, which keeps every point of the new
/// outline the given distance from the old: a concave outline can split as
/// it shrinks, and every piece is kept. A point grows into a circle.
/// </para>
/// <para>
/// An ellipse stays an ellipse, both axes changed by twice the offset. The
/// curve truly the offset's distance from an ellipse is not quite an
/// ellipse, but the two differ by a fraction of a pixel unless the offset
/// is a good part of the minor axis - and an ellipse is what every tool
/// downstream reads best.
/// </para>
/// <para>
/// Measurements that depend on the size - area, perimeter, radius, axes,
/// circularity, and what was measured inside the old shape - are measured
/// again where that is exact and otherwise dropped, so an offset shape
/// never carries a number that belonged to the shape before it. How the
/// shape was found - its score, contrast, fit error - is kept.
/// </para>
/// </remarks>
[VisionNode(
    "Offset Shapes",
    TypeKey = "shape.offset",
    Category = NodeCategory.Blob,
    Icon = "ph_dilate",
    Description = "Grows or shrinks shapes by a distance in pixels - a circle's radius, a rectangle's sides, an outline all round.")]
public sealed class OffsetShapesNode : VisionNode
{
    private double _offset = 2;
    private OffsetCorners _corners = OffsetCorners.Sharp;
    private OffsetLines _lines = OffsetLines.Shift;

    public OffsetShapesNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
        ShapesOut = AddOutput<ShapeSet>("Out");
    }

    public InputPort ShapesIn { get; }

    public OutputPort ShapesOut { get; }

    [Param("Offset",
        Min = -500,
        Max = 500,
        Step = 0.5,
        Connectable = true,
        Description = "How far to move the outline, in pixels: positive grows, negative shrinks. " +
                      "A shape shrunk to nothing is dropped.")]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, Math.Clamp(value, -500, 500));
    }

    [Param("Corners",
        Description = "Sharp keeps a rectangle's and an outline's corners square; " +
                      "Round keeps every point the offset's distance from the shape, so corners round off as they grow.")]
    public OffsetCorners Corners
    {
        get => _corners;
        set => SetParam(ref _corners, value);
    }

    [Param("Lines",
        Description = "Shift moves a line sideways, positive to its right going from its start to its end; " +
                      "Extend lengthens it at both ends.")]
    public OffsetLines Lines
    {
        get => _lines;
        set => SetParam(ref _lines, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var set = context.Get<ShapeSet>(ShapesIn)!;
        var offset = context.Param(nameof(Offset), _offset);

        var report = new Report();
        var offsetShapes = new List<Shape>(set.Count);

        foreach (var shape in set.Shapes)
            offsetShapes.AddRange(OffsetOne(shape, offset, report));

        report.Warn(context);
        context.Set(ShapesOut, new ShapeSet(offsetShapes, set.FrameWidth, set.FrameHeight));
    }

    /// <summary>One shape offset: usually one shape, none when it vanished, several when an outline split.</summary>
    private IEnumerable<Shape> OffsetOne(Shape shape, double d, Report report)
    {
        switch (shape)
        {
            case CircleShape circle:
            {
                var radius = circle.Radius + d;

                if (radius <= 0)
                    return report.Vanished();

                var grown = circle with { Radius = radius };
                return [grown with { Metrics = Remeasure(circle, grown) }];
            }

            case EllipseShape ellipse:
            {
                var major = ellipse.MajorAxis + (2 * d);
                var minor = ellipse.MinorAxis + (2 * d);

                if (minor <= 0)
                    return report.Vanished();

                var grown = ellipse with { MajorAxis = major, MinorAxis = minor };
                return [grown with { Metrics = Remeasure(ellipse, grown) }];
            }

            case OrientedRectShape rect when _corners == OffsetCorners.Sharp || d < 0:
            {
                if (rect.Width + (2 * d) <= 0 || rect.Height + (2 * d) <= 0)
                    return report.Vanished();

                var grown = rect with { Width = rect.Width + (2 * d), Height = rect.Height + (2 * d) };
                return [grown with { Metrics = Remeasure(rect, grown) }];
            }

            case RectShape rect when _corners == OffsetCorners.Sharp || d < 0:
            {
                var bounds = rect.Rect;

                if (bounds.Width + (2 * d) <= 0 || bounds.Height + (2 * d) <= 0)
                    return report.Vanished();

                var grown = rect with
                {
                    Rect = new Bounds(bounds.X - d, bounds.Y - d, bounds.Width + (2 * d), bounds.Height + (2 * d)),
                };
                return [grown with { Metrics = Remeasure(rect, grown) }];
            }

            // Rounded corners make a rectangle an outline.
            case OrientedRectShape rect:
                return Outline(rect, rect.Corners(), d, report);

            case RectShape rect:
                return Outline(
                    rect,
                    [
                        new Point2(rect.Rect.X, rect.Rect.Y),
                        new Point2(rect.Rect.Right, rect.Rect.Y),
                        new Point2(rect.Rect.Right, rect.Rect.Bottom),
                        new Point2(rect.Rect.X, rect.Rect.Bottom),
                    ],
                    d,
                    report);

            case PolygonShape { Closed: true, Points.Count: >= 3 } polygon:
                return Outline(polygon, polygon.Points, d, report);

            case PolygonShape:
                report.OpenPaths++;
                return [shape];

            case LineShape line:
                return OffsetLine(line, d, report);

            // A point grown is the circle round it; shrunk, it stays where it is.
            case PointShape point when d > 0:
            {
                var circle = Dress(new CircleShape(point.At, d), point);
                return [circle with { Metrics = Remeasure(point, circle) }];
            }

            default:
                return [shape];
        }
    }

    private IEnumerable<Shape> OffsetLine(LineShape line, double d, Report report)
    {
        var length = line.Length;

        if (length < 1e-9)
            return [line];

        var (ux, uy) = ((line.End.X - line.Start.X) / length, (line.End.Y - line.Start.Y) / length);

        LineShape moved;

        if (_lines == OffsetLines.Extend)
        {
            if (length + (2 * d) <= 0)
                return report.Vanished();

            moved = line with
            {
                Start = new Point2(line.Start.X - (ux * d), line.Start.Y - (uy * d)),
                End = new Point2(line.End.X + (ux * d), line.End.Y + (uy * d)),
            };
        }
        else
        {
            // Right of the direction of travel, on an image whose y runs down.
            var (nx, ny) = (-uy * d, ux * d);

            moved = line with
            {
                Start = new Point2(line.Start.X + nx, line.Start.Y + ny),
                End = new Point2(line.End.X + nx, line.End.Y + ny),
            };
        }

        return [moved with { Metrics = Remeasure(line, moved) }];
    }

    /// <summary>
    /// An outline offset by Clipper2 and measured again as a closed outline
    /// is everywhere else.
    /// </summary>
    /// <remarks>
    /// Offsetting can split a concave outline into pieces as it shrinks,
    /// each kept; and growing one round on itself can enclose a hole, which
    /// a single outline cannot carry, so the hole is left out and the run
    /// says so.
    /// </remarks>
    private IEnumerable<Shape> Outline(Shape original, IReadOnlyList<Point2> points, double d, Report report)
    {
        var path = new PathD(points.Count);

        foreach (var point in points)
            path.Add(new PointD(point.X, point.Y));

        var join = _corners == OffsetCorners.Round ? JoinType.Round : JoinType.Miter;

        // Three decimal places is a thousandth of a pixel; a round corner is
        // followed to within a twentieth.
        var pieces = Clipper.InflatePaths([path], d, join, EndType.Polygon, miterLimit: 2, precision: 3, arcTolerance: 0.05);

        if (pieces.Count == 0)
            return report.Vanished();

        // Outer pieces wind one way and holes the other; the largest piece
        // is outer, whatever way round the original was drawn.
        var outer = Math.Sign(Clipper.Area(pieces.MaxBy(piece => Math.Abs(Clipper.Area(piece)))!));

        var results = new List<Shape>(pieces.Count);

        foreach (var piece in pieces)
        {
            if (Math.Sign(Clipper.Area(piece)) != outer)
            {
                report.Holes++;
                continue;
            }

            var outline = Nodes.Outline.Through(piece.Select(p => new Point2(p.x, p.y)).ToArray(), closed: true, original.Colour);

            if (outline is null)
                continue;

            results.Add(Dress(outline, original) with { Metrics = Merge(original, outline.Metrics) });
        }

        return results.Count == 0 ? report.Vanished() : results;
    }

    // ---- measurements -----------------------------------------------------

    /// <summary>
    /// What changes with a shape's size or place, or was measured inside it:
    /// not carried over, because it would describe the shape before the
    /// offset.
    /// </summary>
    private static readonly HashSet<string> SizeDependent = new(StringComparer.Ordinal)
    {
        ShapeMetric.Area, ShapeMetric.Perimeter,
        ShapeMetric.BoundsX, ShapeMetric.BoundsY, ShapeMetric.BoundsWidth, ShapeMetric.BoundsHeight,
        ShapeMetric.Extent, ShapeMetric.Circularity, ShapeMetric.RadialCircularity, ShapeMetric.Roundness,
        ShapeMetric.Solidity, ShapeMetric.AspectRatio, ShapeMetric.Width, ShapeMetric.Length,
        ShapeMetric.Radius, ShapeMetric.MajorAxis, ShapeMetric.MinorAxis,
        ShapeMetric.HoleCount, ShapeMetric.TouchesBorder,
        ShapeMetric.Mean, ShapeMetric.StdDev, ShapeMetric.Minimum, ShapeMetric.Maximum,
    };

    /// <summary>The original's measurements that still hold, with the new geometry's own.</summary>
    private static IReadOnlyDictionary<string, double> Merge(Shape original, IReadOnlyDictionary<string, double> measured)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (key, value) in original.Metrics)
        {
            if (!SizeDependent.Contains(key) && key is not (ShapeMetric.CentroidX or ShapeMetric.CentroidY))
                metrics[key] = value;
        }

        foreach (var (key, value) in measured)
            metrics[key] = value;

        return metrics;
    }

    /// <summary>
    /// An offset shape's measurements: the original's that still hold, and
    /// what can be measured exactly of the new one.
    /// </summary>
    private static IReadOnlyDictionary<string, double> Remeasure(Shape original, Shape offset)
    {
        var measured = new Dictionary<string, double>(StringComparer.Ordinal);

        void Exactly(string key, double value)
        {
            // Only what the producer reported to begin with, so an offset
            // set reads in the Shape Table with the columns it had - plus
            // the radius a grown point now has.
            if (original.Metrics.ContainsKey(key) || original is PointShape)
                measured[key] = value;
        }

        switch (offset)
        {
            case CircleShape circle:
                Exactly(ShapeMetric.Radius, circle.Radius);
                Exactly(ShapeMetric.Area, Math.PI * circle.Radius * circle.Radius);
                Exactly(ShapeMetric.Perimeter, 2 * Math.PI * circle.Radius);
                break;

            case EllipseShape ellipse:
                Exactly(ShapeMetric.MajorAxis, ellipse.MajorAxis);
                Exactly(ShapeMetric.MinorAxis, ellipse.MinorAxis);
                Exactly(ShapeMetric.Area, Math.PI * ellipse.MajorAxis * ellipse.MinorAxis / 4);
                break;

            case OrientedRectShape rect:
                Exactly(ShapeMetric.Width, Math.Min(rect.Width, rect.Height));
                Exactly(ShapeMetric.Length, Math.Max(rect.Width, rect.Height));
                Exactly(ShapeMetric.Area, rect.Width * rect.Height);
                Exactly(ShapeMetric.Perimeter, 2 * (rect.Width + rect.Height));
                break;

            case RectShape rect:
                Exactly(ShapeMetric.Area, rect.Rect.Area);
                Exactly(ShapeMetric.Perimeter, 2 * (rect.Rect.Width + rect.Rect.Height));
                break;
        }

        if (original.Metrics.ContainsKey(ShapeMetric.BoundsX))
        {
            var bounds = offset.Bounds;
            measured[ShapeMetric.BoundsX] = bounds.X;
            measured[ShapeMetric.BoundsY] = bounds.Y;
            measured[ShapeMetric.BoundsWidth] = bounds.Width;
            measured[ShapeMetric.BoundsHeight] = bounds.Height;
        }

        var metrics = Merge(original, measured).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        // Growing all round leaves the centre where it was; shifting a line
        // moves it.
        if (original.Metrics.ContainsKey(ShapeMetric.CentroidX))
        {
            var centre = offset is LineShape line
                ? new Point2((line.Start.X + line.End.X) / 2, (line.Start.Y + line.End.Y) / 2)
                : ShapeGeometry.Centre(original);
            metrics[ShapeMetric.CentroidX] = centre.X;
            metrics[ShapeMetric.CentroidY] = centre.Y;
        }

        return metrics;
    }

    /// <summary>A new shape dressed as the one it was made from.</summary>
    private static Shape Dress(Shape made, Shape from) => made with
    {
        Colour = from.Colour,
        StrokeWidth = from.StrokeWidth,
        Label = from.Label,
        LabelSize = from.LabelSize,
        LabelFont = from.LabelFont,
    };

    /// <summary>What happened along the way, said once per run rather than once per shape.</summary>
    private sealed class Report
    {
        public int Gone { get; private set; }

        public int OpenPaths { get; set; }

        public int Holes { get; set; }

        public IEnumerable<Shape> Vanished()
        {
            Gone++;
            return [];
        }

        public void Warn(EvalContext context)
        {
            var notes = new List<string>();

            if (Gone > 0)
                notes.Add(Gone == 1 ? "1 shape shrank to nothing and was dropped" : $"{Gone} shapes shrank to nothing and were dropped");

            if (OpenPaths > 0)
                notes.Add(OpenPaths == 1 ? "1 open path has no inside to grow and was passed on unchanged" : $"{OpenPaths} open paths have no inside to grow and were passed on unchanged");

            if (Holes > 0)
                notes.Add(Holes == 1 ? "1 hole formed as an outline grew round on itself, and was left out" : $"{Holes} holes formed as outlines grew round on themselves, and were left out");

            if (notes.Count > 0)
                context.Warn(string.Join("; ", notes) + ".");
        }
    }
}

/// <summary>What a shape is turned and scaled about.</summary>
public enum TransformPivot
{
    /// <summary>Each shape about its own centre, so each stays where it is.</summary>
    EachCentre,

    /// <summary>All of them about one point, so the set turns and scales as one.</summary>
    Point,
}

/// <summary>Moves, turns and scales shapes.</summary>
/// <remarks>
/// <para>
/// Scaling is proportional, where Offset Shapes adds a distance: a circle
/// scaled 1.1 grows by a tenth of its radius, offset by 2 it grows by two
/// pixels whatever its size.
/// </para>
/// <para>
/// Through <see cref="ShapeMap"/>, the same mapping that carries shapes
/// through Resize and Region of Interest, so a circle stays a circle and an
/// ellipse turns as it should, and lengths, areas and angles are converted
/// with it. The order is scale, then turn, about the pivot, then move.
/// </para>
/// </remarks>
[VisionNode(
    "Transform Shapes",
    TypeKey = "shape.transform",
    Category = NodeCategory.Blob,
    Icon = "ph_resize",
    Description = "Moves, turns and scales shapes, each about its own centre or all about one point.")]
public sealed class TransformShapesNode : VisionNode, IKeepsShapeOrder
{
    private double _moveX;
    private double _moveY;
    private double _rotate;
    private double _scale = 1;
    private TransformPivot _about = TransformPivot.EachCentre;
    private Point2 _pivot;

    public TransformShapesNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
        ShapesOut = AddOutput<ShapeSet>("Out");
    }

    public InputPort ShapesIn { get; }

    public OutputPort ShapesOut { get; }

    [Param("Move X", Min = -10000, Max = 10000, Step = 1, Connectable = true,
        Description = "Pixels to move right; negative moves left.")]
    public double MoveX
    {
        get => _moveX;
        set => SetParam(ref _moveX, value);
    }

    [Param("Move Y", Min = -10000, Max = 10000, Step = 1, Connectable = true,
        Description = "Pixels to move down; negative moves up.")]
    public double MoveY
    {
        get => _moveY;
        set => SetParam(ref _moveY, value);
    }

    [Param("Rotate", Min = -360, Max = 360, Step = 0.5, Connectable = true,
        Description = "Degrees to turn, clockwise on the image like every other angle here.")]
    public double Rotate
    {
        get => _rotate;
        set => SetParam(ref _rotate, value);
    }

    [Param("Scale", Min = 0.01, Max = 100, Step = 0.01, Connectable = true,
        Description = "Size as a multiple: 1.1 is a tenth bigger, 0.5 half the size.")]
    public double Scale
    {
        get => _scale;
        set => SetParam(ref _scale, Math.Clamp(value, 0.01, 100));
    }

    [Param("About", Description = "Turn and scale each shape about its own centre, or all of them about the Pivot.")]
    public TransformPivot About
    {
        get => _about;
        set => SetParam(ref _about, value);
    }

    [Param("Pivot", Connectable = true,
        Description = "The point to turn and scale about when About is Point, as \"x, y\". Wire a shape to use its centre.")]
    public Point2 Pivot
    {
        get => _pivot;
        set => SetParam(ref _pivot, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var set = context.Get<ShapeSet>(ShapesIn)!;

        var moveX = context.Param(nameof(MoveX), _moveX);
        var moveY = context.Param(nameof(MoveY), _moveY);
        var degrees = context.Param(nameof(Rotate), _rotate);
        var scale = context.Param(nameof(Scale), _scale);
        var pivot = context.Param(nameof(Pivot), _pivot);

        if (scale <= 0)
        {
            context.Warn($"A scale of {scale:0.###} would turn the shapes inside out; using 1.");
            scale = 1;
        }

        var radians = degrees * Math.PI / 180;
        var (a, b) = (scale * Math.Cos(radians), -scale * Math.Sin(radians));
        var (c, d) = (scale * Math.Sin(radians), scale * Math.Cos(radians));

        ShapeMap MapAbout(Point2 p) => ShapeMap.Affine(
            a, b, p.X - ((a * p.X) + (b * p.Y)) + moveX,
            c, d, p.Y - ((c * p.X) + (d * p.Y)) + moveY);

        var shared = _about == TransformPivot.Point ? MapAbout(pivot) : null;
        var moved = new List<Shape>(set.Count);

        foreach (var shape in set.Shapes)
        {
            var map = shared ?? MapAbout(ShapeGeometry.Centre(shape));

            // A straight-line map keeps every shape one shape.
            moved.Add(map.Map(shape).First());
        }

        context.Set(ShapesOut, new ShapeSet(moved, set.FrameWidth, set.FrameHeight));
    }
}

