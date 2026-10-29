using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How the one shape is chosen out of the set.</summary>
public enum ShapeSelection
{
    /// <summary>By position in the set, in whatever order the producer sorted it.</summary>
    Index,

    /// <summary>The one with the greatest value of the chosen measure.</summary>
    Largest,

    /// <summary>The one with the least value of the chosen measure.</summary>
    Smallest,
}

/// <summary>
/// Picks one shape out of a set, and reports one of its measurements.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns a set of found features into a number a recipe can act
/// on: find the blobs, take the largest, read its area. The Value output is
/// the bridge from detection to a decision.
/// </para>
/// <para>
/// Index counts from one, and follows the order the producer sorted by - so
/// setting Find Blobs to sort by Position makes "blob 3" mean the same
/// feature on every part, which sorting by size cannot promise.
/// </para>
/// </remarks>
[VisionNode(
    "Select Shape",
    TypeKey = "shape.select",
    Category = NodeCategory.Blob,
    Icon = "ph_select_shape",
    Description = "Picks one shape from a set, by position or by a measurement.")]
public sealed class SelectShapeNode : VisionNode
{
    private ShapeSelection _mode = ShapeSelection.Largest;
    private ShapeMeasure _measure = ShapeMeasure.Area;
    private int _index = 1;

    public SelectShapeNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
        ShapesOut = AddOutput<ShapeSet>("Out");
        ValueOut = AddOutput<double>("Value");
    }

    public InputPort ShapesIn { get; }

    /// <summary>The chosen shape, alone, so it can be drawn or used downstream.</summary>
    public OutputPort ShapesOut { get; }

    /// <summary>The chosen shape's measurement.</summary>
    public OutputPort ValueOut { get; }

    [Param("Mode", Description = "Pick by position in the set, or by a measurement.")]
    public ShapeSelection Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
    }

    [Param("Measure",
        Description = "Which measurement to compare, and to report on the Value output.")]
    public ShapeMeasure Measure
    {
        get => _measure;
        set => SetParam(ref _measure, value);
    }

    [Param("Index",
        Description = "Which shape to take, counting from one.",
        Connectable = true)]
    public int Index
    {
        get => _index;
        set => SetParam(ref _index, Math.Max(1, value));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var set = context.Get<ShapeSet>(ShapesIn)!;

        if (set.Count == 0)
        {
            context.Warn("Nothing to select from; the set is empty.");
            context.Set(ShapesOut, new ShapeSet([], set.FrameWidth, set.FrameHeight));
            context.Set(ValueOut, 0.0);
            return;
        }

        var metric = ShapeMeasures.MetricFor(_measure);
        var chosen = Choose(set, metric, context);

        context.Set(ShapesOut, new ShapeSet([chosen], set.FrameWidth, set.FrameHeight));
        context.Set(ValueOut, metric is null ? 0.0 : chosen.Metric(metric) ?? 0.0);
    }

    private Shape Choose(ShapeSet set, string? metric, EvalContext context)
    {
        if (_mode == ShapeSelection.Index || metric is null)
        {
            var index = context.Param(nameof(Index), _index);

            if (index > set.Count)
            {
                context.Warn($"Asked for shape {index} of {set.Count}; took the last.");
                index = set.Count;
            }

            return set.Shapes[Math.Max(1, index) - 1];
        }

        // A measure the producer never computed cannot be compared on. This is
        // reachable by design: the blob finder skips measures nothing asked
        // for, so selecting by one of those needs its filter set upstream.
        if (set.Shapes.All(shape => shape.Metric(metric) is null))
        {
            context.Warn(
                $"No shape carries a '{_measure}' measurement, so the first was taken. " +
                "Set that filter on the node that produced them, so it is measured.");

            return set.Shapes[0];
        }

        var ordered = set.Shapes.OrderBy(shape => shape.Metric(metric) ?? double.MinValue);

        return _mode == ShapeSelection.Largest ? ordered.Last() : ordered.First();
    }
}

/// <summary>
/// Keeps the shapes whose measurement lies in a range, from any producer.
/// </summary>
/// <remarks>
/// <para>
/// Find Blobs filters as it finds; this filters anything that carries a
/// measurement - edge points by deviation, circles by radius, matches by
/// score. For several conditions, chain one per condition.
/// </para>
/// <para>
/// A shape that does not carry the measurement is rejected: it has not
/// been shown to be in range. Both halves come out, so what was rejected
/// can be drawn in red beside what was kept.
/// </para>
/// </remarks>
[VisionNode(
    "Filter Shapes",
    TypeKey = "shape.filter",
    Category = NodeCategory.Blob,
    Icon = "ph_filter_shapes",
    Description = "Keeps the shapes whose measurement lies in a range - from any producer - and gives what " +
                  "was rejected as well.")]
public sealed class FilterShapesNode : VisionNode
{
    private ShapeMeasure _measure = ShapeMeasure.Area;
    private ValueRange _range = ValueRange.Unbounded;

    public FilterShapesNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
        KeptOut = AddOutput<ShapeSet>("Kept");
        RejectedOut = AddOutput<ShapeSet>("Rejected");
        CountOut = AddOutput<int>("Count");
    }

    public InputPort ShapesIn { get; }

    /// <summary>The shapes in range, in the order they came.</summary>
    public OutputPort KeptOut { get; }

    /// <summary>The rest.</summary>
    public OutputPort RejectedOut { get; }

    /// <summary>How many were kept.</summary>
    public OutputPort CountOut { get; }

    [Param("Measure", Description = "Which measurement to filter on.")]
    public ShapeMeasure Measure
    {
        get => _measure;
        set => SetParam(ref _measure, value);
    }

    [Param(
        "Range",
        Min = -1_000_000,
        Max = 10_000_000,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Connectable = true,
        Description = "Kept when the measurement is within this, ends included. Leave an end blank for no limit.")]
    public ValueRange Range
    {
        get => _range;
        set => SetParam(ref _range, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var set = context.Get<ShapeSet>(ShapesIn)!;
        var range = context.Param(nameof(Range), _range);
        var metric = ShapeMeasures.MetricFor(_measure);

        if (metric is null)
            throw new InvalidOperationException($"{_measure} is not a single number a shape can be filtered on.");

        if (set.Count > 0 && set.Shapes.All(shape => shape.Metric(metric) is null))
            context.Warn($"No shape carries a '{_measure}' measurement, so none was kept.");

        var kept = new List<Shape>();
        var rejected = new List<Shape>();

        foreach (var shape in set.Shapes)
        {
            if (shape.Metric(metric) is { } value && range.Contains(value))
                kept.Add(shape);
            else
                rejected.Add(shape);
        }

        context.Set(KeptOut, new ShapeSet(kept, set.FrameWidth, set.FrameHeight));
        context.Set(RejectedOut, new ShapeSet(rejected, set.FrameWidth, set.FrameHeight));
        context.Set(CountOut, kept.Count);
    }
}
