using System.Collections.Concurrent;

namespace Sirius.Plc;

/// <summary>
/// The simulated PLC itself: a memory - any address, any type, kept as
/// written - that any number of <see cref="SimulatedPlc"/> connections
/// read and write, as a real PLC is talked to by several programs.
/// </summary>
/// <remarks>
/// <para>
/// Addresses are any text without spaces, compared ignoring case and the
/// spaces around them. One never written reads as nothing written: false,
/// 0, or empty text. A value is kept as written and converted to the type
/// asked for when read, so <c>M10</c> written as an Int16 reads back as an
/// Int32 too.
/// </para>
/// <para>
/// <see cref="Named"/> gives the one memory of that name in this program:
/// the controller's workflow and Builder's PLC window, both connected to
/// "Line 1", see the same bits.
/// </para>
/// </remarks>
public sealed class SimulatedPlcMemory
{
    private static readonly ConcurrentDictionary<string, SimulatedPlcMemory> Shared = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, object> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly List<(string Address, object Value)> _writes = [];
    private readonly object _gate = new();
    private bool _unplugged;
    private int _reads;

    public SimulatedPlcMemory(string name = "Simulated PLC")
    {
        Name = name;
    }

    /// <summary>The memory of this name in this program, made the first time it is asked for.</summary>
    public static SimulatedPlcMemory Named(string name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? "Simulated PLC" : name.Trim();
        return Shared.GetOrAdd(key, static key => new SimulatedPlcMemory(key));
    }

    public string Name { get; }

    /// <summary>Raised when an address is written - by a workflow or by hand - with its new value. Not on the screen's thread.</summary>
    public event EventHandler<(string Address, object Value)>? Changed;

    /// <summary>Raised when the cable is pulled out or put back.</summary>
    public event EventHandler? PluggedChanged;

    public bool IsUnplugged
    {
        get
        {
            lock (_gate)
                return _unplugged;
        }
    }

    /// <summary>Reads answered so far - how a test knows a Monitor has started watching.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Every write a connection made, in order - what a test looks at.</summary>
    public IReadOnlyList<(string Address, object Value)> Writes
    {
        get
        {
            lock (_gate)
                return _writes.ToArray();
        }
    }

    /// <summary>Every address written so far and its value, in the order first written.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Values
    {
        get
        {
            lock (_gate)
                return _order.Select(address => new KeyValuePair<string, object>(address, _memory[address])).ToArray();
        }
    }

    /// <summary>Sets an address, as the PLC's own program would - whether anything is connected or not.</summary>
    public void Set(string address, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(value);

        (string, object) written;

        lock (_gate)
            written = Store(address, value, recordWrite: false);

        Changed?.Invoke(this, written);
    }

    /// <summary>What an address holds, as <paramref name="type"/>.</summary>
    public object Get(string address, PlcType type = PlcType.Bool, int length = 0)
    {
        lock (_gate)
            return Value(new PlcVariable(address, type, length));
    }

    /// <summary>Forgets every address.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _memory.Clear();
            _order.Clear();
            _writes.Clear();
        }

        Changed?.Invoke(this, (string.Empty, false));
    }

    /// <summary>The cable pulled out: connections drop, and nothing answers until <see cref="PlugIn"/>.</summary>
    public void Unplug()
    {
        lock (_gate)
            _unplugged = true;

        PluggedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The cable put back. A controller connects again by itself.</summary>
    public void PlugIn()
    {
        lock (_gate)
            _unplugged = false;

        PluggedChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- for its connections ---------------------------------------------------------

    internal object Read(PlcVariable variable)
    {
        lock (_gate)
        {
            EnsurePlugged();
            Interlocked.Increment(ref _reads);
            return Value(variable);
        }
    }

    internal void Write(PlcVariable variable, object value)
    {
        (string, object) written;

        lock (_gate)
        {
            EnsurePlugged();
            written = Store(variable.Address, PlcValues.Convert(value, variable.Type), recordWrite: true);
        }

        // Outside the lock: a panel listening may well read the memory back.
        Changed?.Invoke(this, written);
    }

    internal void EnsurePlugged()
    {
        lock (_gate)
        {
            if (_unplugged)
                throw new PlcException($"{Name} does not answer.");
        }
    }

    private object Value(PlcVariable variable)
    {
        var value = _memory.TryGetValue(Key(variable.Address), out var kept) ? kept : PlcValues.Default(variable.Type);
        var converted = PlcValues.Convert(value, variable.Type);

        return variable.Type == PlcType.String && variable.Length > 0 && converted is string text && text.Length > variable.Length
            ? text[..variable.Length]
            : converted;
    }

    private (string, object) Store(string address, object value, bool recordWrite)
    {
        var key = Key(address);

        if (_memory.TryAdd(key, value))
            _order.Add(key);
        else
            _memory[key] = value;

        if (recordWrite)
            _writes.Add((key, value));

        return (key, value);
    }

    private static string Key(string address) => address.Trim();
}

/// <summary>
/// A connection to a simulated PLC - for the desk, for tests, and for
/// trying a workflow before the real PLC is wired up.
/// </summary>
/// <remarks>
/// Each connection connects and disconnects on its own; the memory is
/// shared (<see cref="SimulatedPlcMemory"/>). An address no real PLC could
/// have - one with a space in it - is refused, so a workflow's mistakes
/// show at the desk.
/// </remarks>
public sealed class SimulatedPlc : IPlcDriver
{
    private bool _connected;

    /// <summary>A connection to a memory of its own - a test's.</summary>
    public SimulatedPlc(string name = "Simulated PLC")
        : this(new SimulatedPlcMemory(name))
    {
    }

    /// <summary>A connection to <paramref name="memory"/>, which others may share.</summary>
    public SimulatedPlc(SimulatedPlcMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public SimulatedPlcMemory Memory { get; }

    public string Description => $"simulated PLC '{Memory.Name}'";

    /// <summary>How long each read and write takes, as a real PLC's answer would.</summary>
    public TimeSpan Latency { get; set; }

    public bool IsConnected => Volatile.Read(ref _connected) && !Memory.IsUnplugged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Wait(cancellationToken).ConfigureAwait(false);

        Memory.EnsurePlugged();
        Volatile.Write(ref _connected, true);
    }

    public Task DisconnectAsync()
    {
        Volatile.Write(ref _connected, false);
        return Task.CompletedTask;
    }

    public string? AddressProblem(PlcVariable variable)
    {
        if (string.IsNullOrWhiteSpace(variable.Address))
            return "No address is given.";

        if (variable.Address.Trim().Any(char.IsWhiteSpace))
            return $"'{variable.Address}' has a space in it.";

        if (variable.Type == PlcType.String && variable.Length <= 0)
            return $"{variable.Address}: a String needs a length.";

        return null;
    }

    public async Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken)
    {
        await Wait(cancellationToken).ConfigureAwait(false);
        Check(variable);
        return Memory.Read(variable);
    }

    public async Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken)
    {
        await Wait(cancellationToken).ConfigureAwait(false);
        Check(variable);
        Memory.Write(variable, value);
    }

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _connected, false);
        return ValueTask.CompletedTask;
    }

    // ---- the memory, by hand: what a test does to it --------------------------------------

    /// <summary>Reads answered so far, by every connection to this memory.</summary>
    public int Reads => Memory.Reads;

    /// <summary>Every write made through any connection to this memory, in order.</summary>
    public IReadOnlyList<(string Address, object Value)> Writes => Memory.Writes;

    /// <summary>Sets an address, as the PLC's own program would.</summary>
    public void Set(string address, object value) => Memory.Set(address, value);

    /// <summary>What an address holds, as <paramref name="type"/>.</summary>
    public object Get(string address, PlcType type = PlcType.Bool, int length = 0) => Memory.Get(address, type, length);

    /// <summary>The cable pulled out.</summary>
    public void Unplug() => Memory.Unplug();

    /// <summary>The cable put back.</summary>
    public void PlugIn() => Memory.PlugIn();

    private void Check(PlcVariable variable)
    {
        if (!Volatile.Read(ref _connected))
            throw new PlcException($"The {Description} is not connected.");

        if (AddressProblem(variable) is { } problem)
            throw new PlcException(problem);
    }

    private Task Wait(CancellationToken cancellationToken) =>
        Latency > TimeSpan.Zero ? Task.Delay(Latency, cancellationToken) : Task.CompletedTask;
}
