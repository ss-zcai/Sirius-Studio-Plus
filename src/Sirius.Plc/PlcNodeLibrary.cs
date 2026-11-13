using Sirius.Core.Nodes;

namespace Sirius.Plc;

/// <summary>The workflow nodes, and the drivers that come with the program.</summary>
public static class PlcNodeLibrary
{
    /// <summary>
    /// Registers the workflow nodes with the node catalogue, and the
    /// simulated PLC with the drivers. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// Their categories - <see cref="NodeCategory.Plc"/> and
    /// <see cref="NodeCategory.Stations"/> - are a workflow's palette; a
    /// recipe's leaves them out.
    /// </remarks>
    public static void Register()
    {
        NodeCatalog.Register(typeof(PlcNodeLibrary).Assembly);
        PlcDrivers.Register(SimulatedPlcDriver.Key, "Simulated PLC", connection => new SimulatedPlc($"Simulated PLC '{connection.Address}'"));
    }

    /// <summary>Whether a node type belongs in a workflow's palette.</summary>
    public static bool IsWorkflowCategory(NodeCategory category) => category.IsWorkflow();
}

/// <summary>The simulated PLC's driver key.</summary>
public static class SimulatedPlcDriver
{
    public const string Key = "simulated";
}

/// <summary>Where a controller's PLC is: which driver, what address, and the driver's own settings.</summary>
/// <param name="Driver">The driver's key: "simulated", "siemens-s7".</param>
/// <param name="Address">Where the PLC is - an IP address for most.</param>
/// <param name="Options">The driver's own settings by name - a rack and slot, a unit ID.</param>
public sealed record PlcConnection(string Driver, string Address, IReadOnlyDictionary<string, string>? Options = null)
{
    public string? Option(string name) => Options?.GetValueOrDefault(name);
}

/// <summary>The kinds of PLC this program can talk to, each a driver made from a <see cref="PlcConnection"/>.</summary>
public static class PlcDrivers
{
    private static readonly Dictionary<string, (string Name, Func<PlcConnection, IPlcDriver> Create)> Known =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>The drivers known: key and the name a person reads.</summary>
    public static IReadOnlyList<(string Key, string Name)> All
    {
        get
        {
            lock (Gate)
                return Known.Select(pair => (pair.Key, pair.Value.Name)).ToArray();
        }
    }

    /// <summary>Makes a driver known. A key given again is replaced - a newer driver library loaded over an older.</summary>
    public static void Register(string key, string name, Func<PlcConnection, IPlcDriver> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(create);

        lock (Gate)
            Known[key] = (name, create);
    }

    /// <summary>A driver for a connection, not yet connected.</summary>
    /// <exception cref="PlcException">No driver of that key is known.</exception>
    public static IPlcDriver Create(PlcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Func<PlcConnection, IPlcDriver> create;

        lock (Gate)
        {
            if (!Known.TryGetValue(connection.Driver, out var known))
                throw new PlcException($"No driver called '{connection.Driver}' is installed.");

            create = known.Create;
        }

        return create(connection);
    }
}
