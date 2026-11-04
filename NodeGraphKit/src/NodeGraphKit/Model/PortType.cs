namespace NodeGraphKit.Model;

/// <summary>How a socket is drawn.</summary>
public enum PortShape
{
    /// <summary>A circle: the default, for values.</summary>
    Round,

    /// <summary>A square: for the one or two "heavy" types a graph is built around (images, in the vision app).</summary>
    Square,
}

/// <summary>
/// The kind of data a port carries. It decides the socket's colour and
/// shape, and which outputs may be wired to which inputs.
/// </summary>
/// <remarks>
/// Colour and shape carry the type so the port's label is free to say what
/// the port is for - "Source", "Mask", "Result" - rather than restating it.
/// Compatible pairs are visible before a drag is attempted.
/// </remarks>
public sealed class PortType
{
    internal PortType(string name, string colour, PortShape shape, bool isAny)
    {
        Name = name;
        Colour = colour;
        Shape = shape;
        IsAny = isAny;
    }

    /// <summary>The type's name, as shown in socket tooltips.</summary>
    public string Name { get; }

    /// <summary>The socket colour, as #RRGGBB.</summary>
    public string Colour { get; }

    public PortShape Shape { get; }

    /// <summary>An input of this type accepts anything; an output of it goes anywhere.</summary>
    public bool IsAny { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The port types an application uses, registered once at start-up.
/// </summary>
/// <example>
/// <code>
/// public static readonly PortType Image = PortTypes.Register("Image", "#60A5FA", PortShape.Square);
/// public static readonly PortType Number = PortTypes.Register("Number", "#FBBF24");
/// </code>
/// </example>
public static class PortTypes
{
    private static readonly Dictionary<string, PortType> Registered = new(StringComparer.Ordinal);

    /// <summary>Accepts, or feeds, every other type. Grey.</summary>
    public static PortType Any { get; } = Add(new PortType("Any", "#9CA3AF", PortShape.Round, isAny: true));

    /// <summary>
    /// Decides whether an output may feed an input. The default allows the
    /// same type, or either end being <see cref="Any"/>; replace it for
    /// conversions such as integer into number.
    /// </summary>
    public static Func<PortType, PortType, bool> Compatibility { get; set; } = DefaultCompatibility;

    /// <summary>Registers a type, or returns the one already registered under that name.</summary>
    /// <param name="colour">Socket colour, #RRGGBB. The palette in docs/style-guide.md reads well on the canvas.</param>
    public static PortType Register(string name, string colour, PortShape shape = PortShape.Round)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (Registered)
        {
            return Registered.TryGetValue(name, out var existing)
                ? existing
                : Add(new PortType(name, colour, shape, isAny: false));
        }
    }

    /// <summary>A registered type by name, or null.</summary>
    public static PortType? Find(string name)
    {
        lock (Registered)
            return Registered.GetValueOrDefault(name);
    }

    public static bool CanConnect(PortType output, PortType input) => Compatibility(output, input);

    public static bool DefaultCompatibility(PortType output, PortType input) =>
        ReferenceEquals(output, input) || output.IsAny || input.IsAny;

    private static PortType Add(PortType type)
    {
        Registered[type.Name] = type;
        return type;
    }
}
