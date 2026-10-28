namespace Sirius.Plugins.Testing;

/// <summary>
/// Runs a custom node without the application, for unit tests.
/// </summary>
/// <remarks>
/// <para>
/// Supply inputs by name, execute, and read what was published. It applies
/// the application's rules - the same port and setting checks, read-only
/// input pictures that stop working when the run ends, a warning when a
/// wired setting is never resolved - so a node that passes here behaves the
/// same way in a recipe.
/// </para>
/// <code>
/// var run = TestRun.For(new GammaNode { Gamma = 2.0 })
///     .Supply("In", picture)
///     .Execute();
///
/// var output = run.Published&lt;Picture&gt;("Out");
/// </code>
/// </remarks>
public sealed class TestRun : IRunContext
{
    private readonly ICustomNode _node;
    private readonly ShapeRecorder _shape = new();
    private readonly IReadOnlyList<DeclaredSetting> _settings;

    private readonly Dictionary<string, object?> _supplied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _wired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _published = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private TestRun(ICustomNode node)
    {
        _node = node;

        var problems = new List<string>();
        _settings = SettingScanner.Scan(node.GetType(), problems);

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

        node.Declare(_shape);
        _shape.Close();
    }

    /// <summary>Prepares to run <paramref name="node"/>, declaring its ports as the application would.</summary>
    public static TestRun For(ICustomNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new TestRun(node);
    }

    /// <summary>What every run sees on input <paramref name="input"/>. Null leaves it unwired.</summary>
    /// <remarks>A picture is copied and handed over read-only, as the application hands one over.</remarks>
    public TestRun Supply(string input, object? value)
    {
        var port = _shape.Inputs.FirstOrDefault(candidate => candidate.Name == input)
                   ?? throw new ArgumentException(
                       $"{_node.GetType().Name} has no input called '{input}'. " +
                       $"Its inputs are: {Names(_shape.Inputs)}.",
                       nameof(input));

        if (value is not null && !Values.TryConvert(value, port.Type, out _))
        {
            throw new ArgumentException(
                $"Input '{input}' carries {port.Type.Name}; a {value.GetType().Name} cannot be wired to it.",
                nameof(value));
        }

        _supplied[input] = value is Picture picture ? picture.AsInput() : value;
        return this;
    }

    /// <summary>
    /// Wires a value into a <see cref="SettingAttribute.Connectable"/>
    /// setting, as another node driving it would.
    /// </summary>
    public TestRun Wire(string setting, object? value)
    {
        var declared = _settings.FirstOrDefault(candidate => candidate.Name == setting)
                       ?? throw new ArgumentException(
                           $"{_node.GetType().Name} has no setting called '{setting}'.", nameof(setting));

        if (!declared.Attribute.Connectable)
        {
            throw new InvalidOperationException(
                $"Setting '{setting}' is not connectable, so nothing can be wired to it.");
        }

        _wired[setting] = value;
        return this;
    }

    /// <summary>The token <see cref="Cancellation"/> reports. Set it to test a node's cancellation.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Runs the node once over what has been supplied.</summary>
    /// <exception cref="InvalidOperationException">A required input has not been supplied.</exception>
    public TestRun Execute()
    {
        _published.Clear();
        _resolved.Clear();
        _warnings.Clear();
        _current.Clear();

        var missing = _shape.Inputs
            .Where(port => port.IsRequired && _supplied.GetValueOrDefault(port.Name) is null)
            .Select(port => port.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Required input{(missing.Length == 1 ? "" : "s")} not supplied: {string.Join(", ", missing)}. " +
                "In the application the node would fail without running.");
        }

        // A fresh view per run, so a node that kept one from last time
        // finds it expired rather than quietly reading the new run's data.
        foreach (var (name, value) in _supplied)
            _current[name] = value is Picture picture ? picture.AsInput() : value;

        try
        {
            _node.Run(this);
        }
        finally
        {
            foreach (var picture in _current.Values.OfType<Picture>())
                picture.Expire();
        }

        foreach (var setting in _wired.Keys.Where(name => _wired[name] is not null && !_resolved.Contains(name)))
            _warnings.Add(Unresolved(setting));

        return this;
    }

    /// <summary>What the last run published on <paramref name="output"/>.</summary>
    /// <exception cref="InvalidOperationException">The node did not publish it.</exception>
    public T Published<T>(string output)
    {
        RequireOutput(output);

        if (!_published.TryGetValue(output, out var value))
            throw new InvalidOperationException($"The node did not publish '{output}'.");

        return value is T typed
            ? typed
            : throw new InvalidOperationException(
                $"'{output}' holds {value?.GetType().Name ?? "null"}, not {typeof(T).Name}.");
    }

    /// <summary>Whether the last run published anything on <paramref name="output"/>.</summary>
    public bool WasPublished(string output)
    {
        RequireOutput(output);
        return _published.ContainsKey(output);
    }

    /// <summary>The warnings the last run raised, in order.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The message shown when a wired setting was never resolved.</summary>
    internal static string Unresolved(string setting) =>
        $"'{setting}' is wired, but the node never read it with Resolve, so the wire was ignored.";

    // ---- IRunContext ----------------------------------------------------------

    T? IRunContext.Read<T>(InPort<T> port) where T : class =>
        Value(port) is { } value && Values.TryConvert(value, typeof(T), out var converted)
            ? (T?)converted
            : null;

    T? IRunContext.ReadValue<T>(InPort<T> port) where T : struct =>
        Value(port) is { } value && Values.TryConvert(value, typeof(T), out var converted)
            ? (T)converted!
            : null;

    bool IRunContext.IsWired<T>(InPort<T> port) => Value(port) is not null;

    T IRunContext.Resolve<T>(string settingName, T current)
    {
        var declared = _settings.FirstOrDefault(candidate => candidate.Name == settingName);
        if (declared is null || !declared.Attribute.Connectable)
        {
            throw new ArgumentException(
                $"'{settingName}' is not a connectable setting of {_node.GetType().Name}.", nameof(settingName));
        }

        _resolved.Add(settingName);

        return _wired.GetValueOrDefault(settingName) is { } wired && Values.TryConvert(wired, typeof(T), out var value)
            ? (T)value!
            : current;
    }

    void IRunContext.Publish<T>(OutPort<T> port, T value)
    {
        if (_shape.Find(port) is null)
            throw new ArgumentException($"'{port.Name}' is not an output of {_node.GetType().Name}.", nameof(port));

        // Copied, as the application copies, so a node that goes on writing
        // into a buffer after publishing it cannot change the result.
        _published[port.Name] = value is Picture picture ? picture.Copy() : value;
    }

    void IRunContext.Warn(string message) => _warnings.Add(message);

    // ---- helpers --------------------------------------------------------------

    private object? Value<T>(InPort<T> port)
    {
        if (_shape.Find(port) is null)
            throw new ArgumentException($"'{port.Name}' is not an input of {_node.GetType().Name}.", nameof(port));

        return _current.GetValueOrDefault(port.Name);
    }

    private void RequireOutput(string output)
    {
        if (_shape.Outputs.All(port => port.Name != output))
        {
            throw new ArgumentException(
                $"{_node.GetType().Name} has no output called '{output}'. " +
                $"Its outputs are: {Names(_shape.Outputs)}.",
                nameof(output));
        }
    }

    private static string Names(IEnumerable<DeclaredPort> ports)
    {
        var names = string.Join(", ", ports.Select(port => port.Name));
        return names.Length == 0 ? "(none)" : names;
    }
}
