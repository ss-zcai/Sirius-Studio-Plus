using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sirius.Plc;

namespace Sample.Drivers;

/// <summary>
/// The sample driver's factory: what Builder lists, what it needs set, and
/// how a driver is made. Copy it, and change the key, name, hint, examples
/// and settings for your brand.
/// </summary>
/// <remarks>
/// Found by the program because it is public, implements
/// <see cref="IPlcDriverFactory"/> and has a constructor taking nothing -
/// nothing else is needed. <see cref="Key"/> and each setting's name are
/// saved in projects: never change them once a project uses them.
/// </remarks>
public sealed class RegisterDriverFactory : IPlcDriverFactory
{
    public string Key => "sample-registers";

    public string Name => "Sample (registers)";

    public string AddressHint => "Any name: drivers with one name share one register device. A real driver says: the PLC's IP address, as 192.168.0.10.";

    public string AddressExamples => "R0.0 (a bit), R10 (a 16-bit register), R20 (32 bits: R20 and R21), R30 as a String";

    public IReadOnlyList<PlcDriverOption> Options { get; } =
    [
        new("wordOrder", "Word order", "big", "How a 32-bit value lies in two registers: big puts its high word first.", ["big", "little"]),
        new("answerMs", "Answer time (ms)", "5", "How long the device takes to answer - a real PLC is never instant."),
    ];

    public IPlcDriver Create(PlcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Settings never set arrive with their defaults: read them without checking.
        var bigEndian = connection.Option("wordOrder") switch
        {
            "big" => true,
            "little" => false,
            var other => throw new PlcException($"The word order '{other}' is not big or little."),
        };

        if (!int.TryParse(connection.Option("answerMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var answer) || answer < 0)
            throw new PlcException("The answer time is a whole number of milliseconds, 0 or more.");

        if (string.IsNullOrWhiteSpace(connection.Address))
            throw new PlcException("No address is given.");

        return new RegisterDriver(RegisterDevice.Named(connection.Address.Trim()), bigEndian, TimeSpan.FromMilliseconds(answer));
    }
}

/// <summary>
/// A driver for a device of 16-bit registers - as a Modbus device, or a
/// PLC's data memory, is. Everything a real driver must do is here: one
/// exchange at a time, addresses checked without the network, values
/// packed into registers and back, the exact .NET type returned, failures
/// as <see cref="PlcException"/>s in words, cancellation honoured, and
/// <see cref="IsConnected"/> telling the truth.
/// </summary>
/// <remarks>
/// A real driver replaces <see cref="RegisterDevice"/> with the brand's
/// client - HslCommunication's, say - and keeps the rest of the shape.
/// </remarks>
public sealed partial class RegisterDriver : IPlcDriver
{
    private readonly RegisterDevice _device;
    private readonly bool _bigEndian;
    private readonly TimeSpan _answer;

    // One exchange at a time: a PLC Monitor polls while a workflow's step reads.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _connected;

    public RegisterDriver(RegisterDevice device, bool bigEndian, TimeSpan answer)
    {
        _device = device;
        _bigEndian = bigEndian;
        _answer = answer;
    }

    /// <summary>What and where - shown in the log and on screen.</summary>
    public string Description => $"sample register device '{_device.Name}'";

    public bool IsConnected => _connected && !_device.IsUnplugged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(_answer, cancellationToken).ConfigureAwait(false);

            if (_device.IsUnplugged)
                throw new PlcException($"No answer from {Description}.");

            _connected = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Never throws, and may be called twice.</summary>
    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    // ---- addresses: checked without the network --------------------------------------

    [GeneratedRegex(@"^R(?<register>\d{1,5})(\.(?<bit>\d{1,2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    /// <summary>A register and bit, or why not - in a sentence saying what would be right.</summary>
    private static (int Register, int? Bit)? Parse(PlcVariable variable, out string? problem)
    {
        var match = Address().Match(variable.Address.Trim());

        if (!match.Success)
        {
            problem = $"'{variable.Address}' is not a register: write R10 for a register, R10.3 for a bit of one.";
            return null;
        }

        var register = int.Parse(match.Groups["register"].Value, CultureInfo.InvariantCulture);
        int? bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value, CultureInfo.InvariantCulture) : null;

        problem = (variable.Type, bit) switch
        {
            (PlcType.Bool, null) => $"{variable.Address} is a whole register: a Bool needs a bit of one, as {variable.Address}.0.",
            (PlcType.Bool, > 15) => $"{variable.Address}: a register has bits 0 to 15.",
            (not PlcType.Bool, not null) => $"{variable.Address} is a bit: a {variable.Type} needs a whole register, as R{register}.",
            (PlcType.String, _) when variable.Length <= 0 => $"{variable.Address}: a String needs a length.",
            _ when register + Registers(variable) > RegisterDevice.Size =>
                $"{variable.Address}: the device has registers R0 to R{RegisterDevice.Size - 1}, and a {variable.Type} here would run past the end.",
            _ => null,
        };

        return problem is null ? (register, bit) : null;
    }

    /// <summary>How many registers a value takes.</summary>
    private static int Registers(PlcVariable variable) => variable.Type switch
    {
        PlcType.Int32 or PlcType.UInt32 or PlcType.Real => 2,
        PlcType.LReal => 4,
        PlcType.String => (variable.Length + 1) / 2,
        _ => 1,
    };

    public string? AddressProblem(PlcVariable variable)
    {
        Parse(variable, out var problem);
        return problem;
    }

    // ---- reading and writing ----------------------------------------------------

    public Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken) =>
        ExchangeAsync(variable, (register, bit) =>
        {
            var words = _device.Read(register, Registers(variable));

            // The exact .NET type of the PlcType asked for - an Int16 is a short, never an int.
            return variable.Type switch
            {
                PlcType.Bool => (words[0] >> bit!.Value & 1) == 1,
                PlcType.Byte => (byte)(words[0] & 0xFF),
                PlcType.Int16 => (short)words[0],
                PlcType.UInt16 => words[0],
                PlcType.Int32 => (int)Join32(words),
                PlcType.UInt32 => Join32(words),
                PlcType.Real => BitConverter.UInt32BitsToSingle(Join32(words)),
                PlcType.LReal => BitConverter.UInt64BitsToDouble(Join64(words)),
                PlcType.String => Text(words, variable.Length),
                _ => throw new PlcException($"{variable.Type} is not a type this driver reads."),
            };
        }, cancellationToken);

    public async Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken) =>
        await ExchangeAsync(variable, (register, bit) =>
        {
            switch (value)
            {
                case bool flag:
                    var word = _device.Read(register, 1)[0];
                    var mask = (ushort)(1 << bit!.Value);
                    _device.Write(register, [flag ? (ushort)(word | mask) : (ushort)(word & ~mask)]);
                    break;

                case byte one: _device.Write(register, [one]); break;
                case short whole: _device.Write(register, [(ushort)whole]); break;
                case ushort whole: _device.Write(register, [whole]); break;
                case int whole: _device.Write(register, Split32((uint)whole)); break;
                case uint whole: _device.Write(register, Split32(whole)); break;
                case float number: _device.Write(register, Split32(BitConverter.SingleToUInt32Bits(number))); break;
                case double number: _device.Write(register, Split64(BitConverter.DoubleToUInt64Bits(number))); break;
                case string text: _device.Write(register, Words(text, variable.Length)); break;
                default: throw new PlcException($"A {value.GetType().Name} cannot be written as a {variable.Type}.");
            }

            return value;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>One exchange: in turn with any other, cancellable, its failures in words.</summary>
    private async Task<object> ExchangeAsync(PlcVariable variable, Func<int, int?, object> exchange, CancellationToken cancellationToken)
    {
        // A request already called off stops at once.
        cancellationToken.ThrowIfCancellationRequested();

        var address = Parse(variable, out var problem) ?? throw new PlcException(problem!);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_connected)
                throw new PlcException($"The {Description} is not connected.");

            await Task.Delay(_answer, cancellationToken).ConfigureAwait(false);

            if (_device.IsUnplugged)
            {
                // The connection is gone: say so, so the program connects again.
                _connected = false;
                throw new PlcException($"No answer from {Description}.");
            }

            return exchange(address.Register, address.Bit);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- packing values into registers ------------------------------------------------

    private uint Join32(ushort[] words) =>
        _bigEndian ? (uint)words[0] << 16 | words[1] : (uint)words[1] << 16 | words[0];

    private ushort[] Split32(uint value) =>
        _bigEndian ? [(ushort)(value >> 16), (ushort)value] : [(ushort)value, (ushort)(value >> 16)];

    private ulong Join64(ushort[] words) =>
        _bigEndian
            ? (ulong)Join32(words[..2]) << 32 | Join32(words[2..])
            : (ulong)Join32(words[2..]) << 32 | Join32(words[..2]);

    private ushort[] Split64(ulong value) =>
        _bigEndian
            ? [.. Split32((uint)(value >> 32)), .. Split32((uint)value)]
            : [.. Split32((uint)value), .. Split32((uint)(value >> 32))];

    /// <summary>Two characters to a register, high byte first; zeros end it.</summary>
    private static string Text(ushort[] words, int length)
    {
        var bytes = words.SelectMany(word => new[] { (byte)(word >> 8), (byte)word }).Take(length).TakeWhile(b => b != 0).ToArray();
        return Encoding.ASCII.GetString(bytes);
    }

    private static ushort[] Words(string text, int length)
    {
        var bytes = new byte[(length + 1) / 2 * 2];
        Encoding.ASCII.GetBytes(text.Length > length ? text[..length] : text).CopyTo(bytes, 0);
        return Enumerable.Range(0, bytes.Length / 2).Select(i => (ushort)(bytes[2 * i] << 8 | bytes[2 * i + 1])).ToArray();
    }
}

/// <summary>
/// The sample's stand-in for a PLC: 1000 registers, shared by name within
/// the program - so the workflow's connection and Builder's PLC window see
/// the same device - and a cable that can be pulled out.
/// </summary>
public sealed class RegisterDevice
{
    public const int Size = 1000;

    private static readonly ConcurrentDictionary<string, RegisterDevice> Devices = new(StringComparer.OrdinalIgnoreCase);

    private readonly ushort[] _registers = new ushort[Size];
    private readonly object _gate = new();

    private RegisterDevice(string name) => Name = name;

    public static RegisterDevice Named(string name) => Devices.GetOrAdd(name, static name => new RegisterDevice(name));

    public string Name { get; }

    /// <summary>The cable out: connections drop, and nothing answers until it is false again.</summary>
    public bool IsUnplugged { get; set; }

    public ushort[] Read(int register, int count)
    {
        lock (_gate)
            return _registers[register..(register + count)];
    }

    public void Write(int register, ushort[] words)
    {
        lock (_gate)
            words.CopyTo(_registers, register);
    }
}
