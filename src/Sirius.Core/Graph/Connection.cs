namespace Sirius.Core.Graph;

/// <summary>
/// A directed edge carrying one value from an output socket to an input socket.
/// Created only through <see cref="VisionGraph.TryConnect"/>, which enforces the
/// type and acyclicity rules.
/// </summary>
public sealed class Connection
{
    internal Connection(OutputPort source, InputPort target)
    {
        Source = source;
        Target = target;
    }

    public OutputPort Source { get; }

    public InputPort Target { get; }

    public override string ToString() => $"{Source} -> {Target}";
}
