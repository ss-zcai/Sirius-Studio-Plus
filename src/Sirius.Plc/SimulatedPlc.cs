namespace Sirius.Plc;

/// <summary>
/// A PLC that is only a memory: any address, any type, kept as written.
/// For the desk, for tests, and for trying a workflow before the real PLC
/// is wired up - a panel shows its memory and changes it by hand.
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
/// <see cref="Unplug"/> stands in for a cable pulled out: what is
/// connected drops, and reads, writes and connecting fail until
/// <see cref="PlugIn"/>.
/// </para>
/// </remarks>
public sealed class SimulatedPlc : IPlcDriver
{
    private readonly Dictionary<string, object> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Address, object Value)> _writes = [];
    private readonly object _gate = new();
    private bool _connected;
    private bool _unplugged;
    private int _reads;

    public SimulatedPlc(string name = "Simulated PLC")
    {
        Description = name;
    }

    public string Description { get; }

    /// <summary>How long each read and write takes, as a real PLC's answer would.</summary>
    public TimeSpan Latency { get; set; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
    }

    /// <summary>Reads answered so far - how a test knows a Monitor has started watching.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Raised when an address is written - by a workflow or by hand - with its new value.</summary>
    public event EventHandler<(string Address, object Value)>? Changed;

    /// <summary>Every write the workflow made, in order - what a test looks at.</summary>
    public IReadOnlyList<(string Address, object Value)> Writes
    {
        get
        {
            lock (_gate)
                return _writes.ToArray();
        }
    }

    /// <summary>Every address written so far, and its value.</summary>
    public IReadOnlyDictionary<string, object> Memory
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, object>(_memory, StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Wait(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_unplugged)
                throw new PlcException($"{Description} does not answer.");

            _connected = true;
        }
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
            _connected = false;

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

        lock (_gate)
        {
            EnsureConnected();
            EnsureAddress(variable);
            Interlocked.Increment(ref _reads);
            return Value(variable);
        }
    }

    public async Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken)
    {
        await Wait(cancellationToken).ConfigureAwait(false);

        (string, object) written;

        lock (_gate)
        {
            EnsureConnected();
            EnsureAddress(variable);
            written = Store(variable.Address, PlcValues.Convert(value, variable.Type), recordWrite: true);
        }

        // Outside the lock: a panel listening may well read the memory back.
        Changed?.Invoke(this, written);
    }

    // ---- by hand: the panel, and tests ----------------------------------------------

    /// <summary>Sets an address, as the PLC's own program would - connected or not.</summary>
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

    /// <summary>The cable pulled out: disconnected, and nothing answers until <see cref="PlugIn"/>.</summary>
    public void Unplug()
    {
        lock (_gate)
        {
            _unplugged = true;
            _connected = false;
        }
    }

    /// <summary>The cable put back. The runner connects again by itself.</summary>
    public void PlugIn()
    {
        lock (_gate)
            _unplugged = false;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            _connected = false;

        return ValueTask.CompletedTask;
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
        _memory[key] = value;

        if (recordWrite)
            _writes.Add((key, value));

        return (key, value);
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new PlcException(_unplugged ? $"{Description} does not answer." : $"{Description} is not connected.");
    }

    /// <summary>A real PLC refuses an address it has not got; this one, an address no driver could read.</summary>
    private void EnsureAddress(PlcVariable variable)
    {
        if (AddressProblem(variable) is { } problem)
            throw new PlcException(problem);
    }

    private Task Wait(CancellationToken cancellationToken) =>
        Latency > TimeSpan.Zero ? Task.Delay(Latency, cancellationToken) : Task.CompletedTask;

    private static string Key(string address) => address.Trim();
}
