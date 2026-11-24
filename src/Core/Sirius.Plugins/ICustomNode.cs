namespace Sirius.Plugins;

/// <summary>
/// A tool written outside Sirius Studio Plus.
/// </summary>
/// <remarks>
/// <para>
/// Implement this, mark the class with <see cref="CustomNodeAttribute"/>,
/// expose its parameters as public properties marked with
/// <see cref="SettingAttribute"/>, and build it into a class library. The
/// application wraps each instance in a node of its own, so it appears in
/// the toolbox beside the built-in tools, its settings appear in the
/// properties panel, and recipes that use it save and load like any other.
/// </para>
/// <para>
/// The class needs a public parameterless constructor. One instance is made
/// for every node an operator places, so instance fields are per node.
/// </para>
/// </remarks>
public interface ICustomNode
{
    /// <summary>
    /// Declares the node's inputs and outputs.
    /// </summary>
    /// <remarks>
    /// Called once, when the node is created and before any
    /// <see cref="Run"/>. Keep the handles <paramref name="shape"/> returns in
    /// fields: they are how <see cref="Run"/> names a port.
    /// </remarks>
    void Declare(INodeShape shape);

    /// <summary>
    /// Produces outputs from inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on a worker thread, never the user interface thread, and other
    /// nodes - including other instances of this class - may be running at
    /// the same time. Instance state is safe; static mutable state needs a
    /// lock.
    /// </para>
    /// <para>
    /// The graph re-runs while an operator drags a slider, and abandons a run
    /// that has been overtaken. Long loops should call
    /// <see cref="IRunContext.ThrowIfCancelled"/>.
    /// </para>
    /// <para>
    /// Throwing is the right way to report a real failure. The node shows as
    /// failed, with the exception's message, and the rest of the application
    /// carries on.
    /// </para>
    /// </remarks>
    void Run(IRunContext context);
}

/// <summary>
/// What <see cref="ICustomNode.Declare"/> uses to give a node its ports.
/// </summary>
/// <remarks>
/// A port carries one of a fixed set of types, listed in
/// <see cref="CustomNodeTypes.PortTypes"/>. Anything else is refused when the
/// library loads, not when a recipe runs.
/// </remarks>
public interface INodeShape
{
    /// <summary>Adds an input.</summary>
    /// <param name="name">
    /// Written into saved recipes. Never change it once a recipe uses the
    /// node - change <paramref name="label"/> instead.
    /// </param>
    /// <param name="label">What the operator sees; defaults to the name.</param>
    /// <param name="required">
    /// When true, the node fails rather than runs if nothing is wired to it.
    /// When false, <see cref="IRunContext.Read{T}"/> returns null instead.
    /// </param>
    InPort<T> Input<T>(string name, string? label = null, bool required = true);

    /// <summary>Adds an output.</summary>
    /// <param name="name">Written into saved recipes; never change it once used.</param>
    /// <param name="label">What the operator sees; defaults to the name.</param>
    OutPort<T> Output<T>(string name, string? label = null);
}

/// <summary>An input declared by <see cref="INodeShape.Input{T}"/>.</summary>
public sealed class InPort<T>
{
    internal InPort(string name, string label, bool isRequired)
    {
        Name = name;
        Label = label;
        IsRequired = isRequired;
    }

    /// <summary>The stable name saved in recipes.</summary>
    public string Name { get; }

    /// <summary>What the operator sees.</summary>
    public string Label { get; }

    /// <summary>Whether the node refuses to run with nothing wired here.</summary>
    public bool IsRequired { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>An output declared by <see cref="INodeShape.Output{T}"/>.</summary>
public sealed class OutPort<T>
{
    internal OutPort(string name, string label)
    {
        Name = name;
        Label = label;
    }

    /// <summary>The stable name saved in recipes.</summary>
    public string Name { get; }

    /// <summary>What the operator sees.</summary>
    public string Label { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Everything a node may touch while it runs.
/// </summary>
public interface IRunContext
{
    /// <summary>
    /// Reads an input that carries a reference type - a
    /// <see cref="Picture"/>, a shape set, a matrix or a string.
    /// </summary>
    /// <returns>Null when nothing is wired, or the upstream node produced nothing.</returns>
    /// <remarks>
    /// What comes back is borrowed. Do not keep it beyond this call to
    /// <see cref="ICustomNode.Run"/>, and do not change it - every other node
    /// wired to the same output is reading the same object. An input
    /// <see cref="Picture"/> is read-only and stops working when
    /// <see cref="ICustomNode.Run"/> returns.
    /// </remarks>
    T? Read<T>(InPort<T> port) where T : class;

    /// <summary>
    /// Reads an input that carries a value type - a number, a point, a
    /// calibration.
    /// </summary>
    /// <returns>Null when nothing is wired, or the upstream node produced nothing.</returns>
    /// <remarks>
    /// A number arriving as a different numeric type is converted, so an
    /// integer output can feed a <c>double</c> input.
    /// </remarks>
    T? ReadValue<T>(InPort<T> port) where T : struct;

    /// <summary>Whether anything is wired to <paramref name="port"/>.</summary>
    bool IsWired<T>(InPort<T> port);

    /// <summary>
    /// The value of a <see cref="SettingAttribute.Connectable"/> setting for
    /// this run: what arrives on its wire if one is attached, otherwise
    /// <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Read a connectable setting through this rather than directly - the
    /// property holds what was typed into the properties panel, which a wire
    /// overrides. If a setting is wired and this is never called for it, the
    /// node shows a warning saying the wire was ignored.
    /// </remarks>
    /// <example><c>var gain = context.Resolve(nameof(Gain), Gain);</c></example>
    T Resolve<T>(string settingName, T current);

    /// <summary>Sets an output.</summary>
    /// <remarks>
    /// A <see cref="Picture"/> from <see cref="Picture.Allocate(int, int, int, PixelType, ColourLayout)"/>
    /// is copied into the application when published, so publishing the same
    /// one to two outputs is fine. Publishing an input picture passes it
    /// straight through without copying.
    /// </remarks>
    void Publish<T>(OutPort<T> port, T value);

    /// <summary>
    /// Reports something worth knowing that is not a failure. The node still
    /// produces its outputs, and shows amber with this message.
    /// </summary>
    void Warn(string message);

    /// <summary>Signalled when this run has been overtaken and its results are no longer wanted.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Whether this run has been overtaken.</summary>
    bool IsCancelled => Cancellation.IsCancellationRequested;

    /// <summary>Stops the run if it has been overtaken. Call it inside long loops.</summary>
    void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();
}
