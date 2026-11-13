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
        PlcDrivers.Register(new SimulatedPlcDriverFactory());
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

/// <summary>
/// The kinds of PLC this program can talk to - the simulated one, and each
/// driver library found (<see cref="PlcDriverLoader"/>) - by key.
/// </summary>
public static class PlcDrivers
{
    private static readonly Dictionary<string, IPlcDriverFactory> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> LoadProblems = [];
    private static readonly object Gate = new();

    /// <summary>The drivers known: key and the name a person reads, in the order they were found.</summary>
    public static IReadOnlyList<(string Key, string Name)> All
    {
        get
        {
            lock (Gate)
                return Known.Values.Select(factory => (factory.Key, factory.Name)).ToArray();
        }
    }

    /// <summary>Every driver known.</summary>
    public static IReadOnlyList<IPlcDriverFactory> Factories
    {
        get
        {
            lock (Gate)
                return Known.Values.ToArray();
        }
    }

    /// <summary>What went wrong finding driver libraries - a sentence each.</summary>
    public static IReadOnlyList<string> Problems
    {
        get
        {
            lock (Gate)
                return LoadProblems.ToArray();
        }
    }

    /// <summary>The driver of this key, or null.</summary>
    public static IPlcDriverFactory? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (Gate)
            return Known.GetValueOrDefault(key.Trim());
    }

    /// <summary>
    /// Makes a driver known. A key given again is replaced - a newer copy of
    /// a library loaded over an older.
    /// </summary>
    public static void Register(IPlcDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.Key);

        lock (Gate)
            Known[factory.Key.Trim()] = factory;
    }

    /// <summary>Makes a driver known from a function - for tests, and a driver built into a program.</summary>
    public static void Register(string key, string name, Func<PlcConnection, IPlcDriver> create) =>
        Register(new FunctionFactory(key, name, create));

    internal static void Problem(string problem)
    {
        lock (Gate)
            LoadProblems.Add(problem);
    }

    /// <summary>A driver for a connection, not yet connected.</summary>
    /// <exception cref="PlcException">No driver of that key is installed, or its settings cannot work.</exception>
    public static IPlcDriver Create(PlcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var factory = Find(connection.Driver)
                      ?? throw new PlcException($"No driver called '{connection.Driver}' is installed.");

        // Settings never set take the driver's defaults, so a driver reads
        // every option it declares without checking for it.
        var options = factory.Options.ToDictionary(
            option => option.Name,
            option => connection.Option(option.Name) ?? option.Default,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in connection.Options ?? new Dictionary<string, string>())
            options.TryAdd(name, value);

        try
        {
            return factory.Create(connection with { Options = options });
        }
        catch (PlcException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PlcException($"The {factory.Name} driver could not be made: {ex.Message}", ex);
        }
    }

    private sealed class FunctionFactory(string key, string name, Func<PlcConnection, IPlcDriver> create) : IPlcDriverFactory
    {
        public string Key => key;

        public string Name => name;

        public string AddressHint => "Where the PLC is.";

        public string AddressExamples => string.Empty;

        public IReadOnlyList<PlcDriverOption> Options { get; } = [];

        public IPlcDriver Create(PlcConnection connection) => create(connection);
    }
}
