using System.Globalization;

namespace Sirius.Core.Graph;

/// <summary>
/// Everything a node is allowed to touch during one evaluation: its resolved
/// input values, a sink for its outputs, and a cancellation token.
/// </summary>
/// <remarks>
/// Inputs are <b>borrowed</b> - do not dispose them, and do not mutate them
/// in place, because the same instance is handed to every other consumer of
/// that output port.
/// </remarks>
public sealed class EvalContext
{
    private readonly Dictionary<InputPort, object?> _inputs;
    private readonly Dictionary<OutputPort, PortValue> _outputs = new();

    internal EvalContext(
        VisionNode node,
        Dictionary<InputPort, object?> inputs,
        CancellationToken cancellationToken,
        bool isInspection = false)
    {
        Node = node;
        _inputs = inputs;
        CancellationToken = cancellationToken;
        IsInspection = isInspection;
    }

    public VisionNode Node { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// True when this run inspects a part - a Run, a folder run, a camera
    /// frame - rather than re-running after an edit. See
    /// <see cref="IInspectionNode"/>.
    /// </summary>
    public bool IsInspection { get; }

    internal IReadOnlyDictionary<OutputPort, PortValue> Outputs => _outputs;

    internal string? Warning { get; private set; }

    private Dictionary<string, Placement>? _placements;

    /// <summary>What this run placed, by parameter name. See <see cref="Place"/>.</summary>
    internal IReadOnlyDictionary<string, Placement> Placements =>
        _placements ?? VisionNode.NothingPlaced;

    /// <summary>
    /// Records where this run put a parameter's geometry, when something
    /// wired in moved it from where it was drawn - so the image shows the
    /// band or region actually searched.
    /// </summary>
    public void Place(string parameterName, Placement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        (_placements ??= new Dictionary<string, Placement>(StringComparer.Ordinal))[parameterName] = placement;
    }

    /// <summary>
    /// Reads an input. Returns null when nothing is connected or the upstream
    /// node produced null. Throws if <paramref name="port"/> is not an input of
    /// this node, which is a wiring bug rather than a runtime condition.
    /// </summary>
    public T? Get<T>(InputPort port) where T : class
    {
        if (!_inputs.TryGetValue(port, out var value))
            throw new ArgumentException($"'{port.Name}' is not an input of node '{Node.Name}'.", nameof(port));

        return value as T;
    }

    /// <summary>
    /// Reads an input whose type is a value type, or null when nothing is
    /// wired to it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Get{T}"/> because that one answers "or
    /// null" with a reference, which a struct cannot be. The compound
    /// types - a ring, a rectangle, a calibration - have reached nodes
    /// as connectable parameters until now, where
    /// <see cref="Param{T}"/> handles them; this is for the ones that
    /// arrive as an ordinary port.
    /// </remarks>
    public T? GetValue<T>(InputPort port) where T : struct
    {
        if (!_inputs.TryGetValue(port, out var value))
            throw new ArgumentException($"'{port.Name}' is not an input of node '{Node.Name}'.", nameof(port));

        return value is T typed ? typed : null;
    }

    /// <summary>Reads an input, reporting whether a usable value was present.</summary>
    public bool TryGet<T>(InputPort port, out T value) where T : class
    {
        var result = Get<T>(port);
        value = result!;
        return result is not null;
    }

    /// <summary>
    /// Publishes a freshly allocated output. The scheduler takes ownership and
    /// will dispose it when the value is superseded or evicted.
    /// </summary>
    public void Set(OutputPort port, object? value)
    {
        Validate(port);
        _outputs[port] = new PortValue(value, isOwned: true);
    }

    /// <summary>
    /// Publishes a value this node did not allocate - typically an input echoed
    /// straight through. The scheduler will not dispose it.
    /// </summary>
    public void Forward(OutputPort port, object? borrowed)
    {
        Validate(port);
        _outputs[port] = new PortValue(borrowed, isOwned: false);
    }

    /// <summary>
    /// Resolves a connectable parameter: the value arriving on its input port
    /// if one is wired, otherwise <paramref name="fallback"/> - the value from
    /// the inspector.
    /// </summary>
    /// <remarks>
    /// Numeric types are converted rather than required to match, so an
    /// integer output can drive a double parameter without an adapter node.
    /// A value that cannot be converted falls back rather than throwing; the
    /// connection rules should have prevented it reaching here.
    /// </remarks>
    public T Param<T>(string parameterName, T fallback)
    {
        var value = Resolve(parameterName, fallback);

        return value is not null && Shapes.ReferencePose.CanMove(typeof(T))
            ? (T)Follow(parameterName, value)
            : value;
    }

    /// <summary>Whether this run taught the node where its reference is.</summary>
    internal bool TaughtReference { get; private set; }

    /// <summary>
    /// A region, moved and turned as the node's reference has moved and
    /// turned since it was wired - or as it was drawn, with nothing wired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first run after the reference is wired takes where it is then as
    /// the place the regions were drawn relative to, so wiring it moves
    /// nothing; every run after carries them along.
    /// </para>
    /// <para>
    /// Nothing arriving on a wired reference fails the node rather than
    /// searching where the region was drawn. The part is not where the
    /// region expects it, and an inspection that quietly looks at the empty
    /// fixture instead would pass parts it never saw.
    /// </para>
    /// </remarks>
    private object Follow(string parameterName, object region)
    {
        if (Node.RegionReference is not { IsConnected: true } port)
            return region;

        if (Get<Shapes.ShapeSet>(port) is not { Count: > 0 } shapes)
            throw new InvalidOperationException(
                "Nothing arrived on Reference, so there is nowhere to put the regions that follow it.");

        if (shapes.Count > 1)
            Warn($"{shapes.Count} shapes arrived on Reference; the regions follow the first.");

        var now = Shapes.ReferencePose.Of(shapes.Shapes[0]);

        if (!Shapes.ReferencePose.TryParse(Node.TaughtReference, out var taught))
        {
            Node.TeachReference(now);
            TaughtReference = true;
            taught = now;
        }

        var degrees = Node.FollowReferenceAngle ? Shapes.ReferencePose.Turn(taught, now) : 0;
        var moved = Shapes.ReferencePose.Move(region, taught, now, degrees);

        Place(parameterName, new Placement(
            moved,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Follows the reference: moved {now.X - taught.X:0.#}, {now.Y - taught.Y:0.#} px") +
            (degrees == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" and turned {degrees:0.#}°")) +
            " from where it was when wired. Drawing it sets where it is relative to the reference.")
        {
            // Back into the place the region was drawn relative to.
            Redraw = drawn => new Redrawn(
                new Dictionary<string, object> { [parameterName] = Shapes.ReferencePose.Move(drawn, now, taught, -degrees) },
                drawn),
        });

        return moved;
    }

    private T Resolve<T>(string parameterName, T fallback)
    {
        var port = Node.FindInput(parameterName);
        if (port is null)
            return fallback;

        if (!_inputs.TryGetValue(port, out var value) || value is null)
            return fallback;

        if (value is T typed)
            return typed;

        // A point is a two-value vector, so a Matrix can drive one. Length
        // is checked here rather than at connect time, where a matrix does
        // not yet have one.
        if (typeof(T) == typeof(Shapes.Point2) && value is Nodes.NumberMatrix matrix)
        {
            if (matrix.Count != 2)
            {
                Warn($"'{parameterName}' needs two numbers for a point, but was given " +
                     $"{matrix.Rows}x{matrix.Columns}; using the editor value.");

                return fallback;
            }

            return (T)(object)new Shapes.Point2(matrix.At(0), matrix.At(1));
        }

        // A region is a shape: a found box, line or circle says where the
        // next tool looks, and the editor value says what the shape leaves
        // out - see ShapeRegions.
        if (value is Shapes.ShapeSet shapes && Shapes.ShapeRegions.CanDrive(typeof(T)))
        {
            if (shapes.Count == 0)
            {
                Warn($"No shape arrived on '{parameterName}'; using the editor value.");
                return fallback;
            }

            if (shapes.Count > 1)
                Warn($"{shapes.Count} shapes arrived on '{parameterName}'; using the first.");

            var placed = (T)Shapes.ShapeRegions.From(shapes.Shapes[0], typeof(T), fallback);

            if (placed is not Shapes.Point2)
            {
                Place(parameterName, new Placement(
                    placed,
                    "Placed by the shape wired into it. Drawing it sets only what the shape leaves out, such as its width."));
            }

            return placed;
        }

        try
        {
            return (T)System.Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            Warn($"'{parameterName}' received a {value.GetType().Name} that could not be used; using the editor value.");
            return fallback;
        }
    }

    /// <summary>True when a connectable parameter is being driven by a wire.</summary>
    public bool IsDriven(string parameterName)
    {
        var port = Node.FindInput(parameterName);
        return port is not null && _inputs.TryGetValue(port, out var value) && value is not null;
    }

    /// <summary>
    /// Records a non-fatal problem. The node still reports a result, but ends up
    /// in <see cref="NodeStatus.Warning"/> with this message.
    /// </summary>
    public void Warn(string message) => Warning = message;

    private void Validate(OutputPort port)
    {
        if (!ReferenceEquals(port.Node, Node))
            throw new ArgumentException($"'{port.Name}' is not an output of node '{Node.Name}'.", nameof(port));
    }
}
