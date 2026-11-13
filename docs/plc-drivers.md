# PLC drivers: the developer's guide

For the team writing PLC drivers - Siemens S7 first, then Modbus TCP,
Mitsubishi, Omron, Rockwell, Beckhoff and whatever customers have. A
driver is a small .NET library; drop it in a folder and Sirius Builder and
Sirius Edge talk to that brand of PLC, in every project, with nothing
else changed.

How workflows use a PLC - the nodes, how a pass runs - is in
`plc-workflows.md`. Why the design is as it is: decisions 74 to 76.

Driver interface **1.0**.

---

## 1. Where a driver sits

```
  Sirius Builder / Sirius Edge
  ┌─────────────────────────────────────────────────────────────────┐
  │  Controller "Line 1 PLC"                                        │
  │    workflow:  PLC Monitor → PLC Read → Gather → Inspect → ...   │  nodes: written once,
  │                     │          │                     │          │  for every brand
  │                     ▼          ▼                     ▼          │
  │               IPlcDriver: Connect, Read, Write, AddressProblem  │  ← the only thing
  │                     │                                           │    a driver writes
  └─────────────────────┼───────────────────────────────────────────┘
                        ▼
        your driver  (e.g. on HslCommunication)  ──── Ethernet ────  PLC
```

- **The nodes never change per brand.** PLC Read reads "an address as an
  Int16"; your driver decides what `DB34.20` means on the wire.
- **Only the driver reads an address.** Nothing else in the program
  parses one. Addresses are the brand's own words, as the PLC programmer
  writes them in TIA Portal, GX Works or Sysmac.
- **The connection is the project's**: which driver, the PLC's address,
  and the driver's own settings (CPU, rack, slot) are kept with the
  controller in the `.sproj`, set in Builder's PLC window.
- **The program connects, and reconnects.** A driver connects when asked
  and says when it is not connected; the workflow runner retries every
  two seconds and logs it once.

---

## 2. The interface

All in `Sirius.Plc.dll`, namespace `Sirius.Plc`.

### 2.1 `IPlcDriverFactory` - what the program finds

A public class with a constructor that takes nothing. A library may hold
several, one per brand or family.

```csharp
public interface IPlcDriverFactory
{
    string Key { get; }                              // "siemens-s7" - saved in projects, never changed
    string Name { get; }                             // "Siemens S7" - what Builder lists
    string AddressHint { get; }                      // "The PLC's IP address, as 192.168.0.10."
    string AddressExamples { get; }                  // "M0.0, MW10, MD20, DB34.DBX20.0, DB34.DBW22"
    IReadOnlyList<PlcDriverOption> Options { get; }  // CPU, rack, slot...
    IPlcDriver Create(PlcConnection connection);     // a driver, not yet connected
}
```

- **`Key`** is written into every project that uses the driver
  (`"driver": "siemens-s7"`), so it is permanent. Lower case letters,
  digits and `-`, starting with a letter: `siemens-s7`, `modbus-tcp`,
  `mitsubishi-mc`, `omron-fins`, `rockwell-ab`, `beckhoff-ads`.
  `simulated` is taken.
- **`Options`** are the driver's settings beyond the address. Each is a
  `PlcDriverOption(Name, Label, Default, Description, Choices)`; `Name` is
  the key it is saved under, so it is permanent too. With `Choices`,
  Builder offers a list; without, a box.
- **`Create`** makes a driver and returns at once - no connecting, no
  network. Every option you declared is in `connection.Options`, set or
  defaulted, so read them with `connection.Option("rack")` without
  checking. Throw `PlcException` for settings that cannot work ("The CPU
  'S5' is not one this driver knows").

### 2.2 `IPlcDriver` - one PLC

```csharp
public interface IPlcDriver : IAsyncDisposable
{
    string Description { get; }        // "Siemens S7-1200 at 192.168.0.10" - in the log and on screen
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();            // never throws
    string? AddressProblem(PlcVariable variable);   // null when usable, else a sentence
    Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken);
    Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ReadManyAsync(...);  // has a default: one read at a time
}
```

**`PlcVariable(Address, Type, Length)`** - an address, what is kept there,
and for a `String` how many characters.

**`PlcType`** and the .NET type each is carried as - what `ReadAsync`
must return, exactly, and what `WriteAsync` is given:

| `PlcType` | IEC 61131-3 | .NET | Siemens | Builder shows |
|---|---|---|---|---|
| `Bool` | BOOL | `bool` | `M0.0`, `DB1.DBX0.0` | Bool (bit) |
| `Byte` | BYTE | `byte` | `MB10` | Byte |
| `Int16` | INT | `short` | `MW10` | Int16 (INT) |
| `UInt16` | WORD/UINT | `ushort` | `MW10` | UInt16 (WORD) |
| `Int32` | DINT | `int` | `MD20` | Int32 (DINT) |
| `UInt32` | DWORD/UDINT | `uint` | `MD20` | UInt32 (DWORD) |
| `Real` | REAL | `float` | `MD20` | Real (32-bit) |
| `LReal` | LREAL | `double` | `DB1.DBX0.0`+8 bytes | LReal (64-bit) |
| `String` | STRING | `string` | `DB1.DBB0` | String |

Returning an `int` for an `Int16` is the most common first mistake; the
conformance check (§6) catches it.

**`PlcValues`** converts between them: `PlcValues.Convert(value, type)`
turns "12", 12.0 or true into what a type holds, or throws a
`PlcException` saying it cannot; `PlcValues.ClrType(type)`;
`PlcValues.Text(value)`.

**`PlcException`** - everything that is the line's fault rather than the
program's: no answer, an address the PLC has not got, a value it would
not take. The message is shown as it is, in the workflow's log and in
Builder, so it is a sentence a commissioning engineer understands:

- "No answer from 192.168.0.10 within 3 s." - not "SocketException 10060".
- "DB999 does not exist in the PLC." - not "Error code 0x0A".
- "The PLC refused the write to DB34.DBW20: the DB is write-protected."

### 2.3 The rules

1. **Return the exact .NET type** of the `PlcType` asked for.
2. **Throw `PlcException`, and only that**, for anything the PLC or the
   network did. Another exception type means a bug in the driver, and is
   reported as one.
3. **Honour cancellation.** A token already cancelled stops at once;
   one cancelled during a read stops waiting (the runner uses this for
   every step's time limit). If your library's calls cannot be cancelled,
   `await call.WaitAsync(cancellationToken)` (§4.3).
4. **Be safe to call from several threads at once.** A PLC Monitor polls
   while a workflow's step reads. A lock around each exchange is enough.
5. **`IsConnected` tells the truth.** Set it false the moment an exchange
   fails because the connection is gone, so the runner reconnects.
6. **`AddressProblem` answers without the network.** It is asked when a
   workflow is checked - in Builder, as it is edited - so it parses only.
   Say what is wrong and what would be right: "MW10 is a word (16 bits);
   an Int32 needs MD10."
7. **`DisconnectAsync` and `DisposeAsync` never throw**, and may be called
   twice.
8. **No user interface, no message boxes, no console output.** Problems
   are exceptions; Builder and Edge show them.

---

## 3. Setting up a driver library

### 3.1 The project

Until the start kit's NuGet package exists (backlog F23e), a driver
library references the two DLLs from an installed Sirius - Builder's
folder - without copying them:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>Insvis.Drivers.Siemens</AssemblyName>
    <Version>1.0.0</Version>
    <SiriusFolder>C:\Program Files\Sirius\</SiriusFolder>
  </PropertyGroup>

  <ItemGroup>
    <!-- The program's own: referenced, never copied. -->
    <Reference Include="Sirius.Plc" HintPath="$(SiriusFolder)Sirius.Plc.dll" Private="false" />
    <Reference Include="Sirius.Core" HintPath="$(SiriusFolder)Sirius.Core.dll" Private="false" />

    <!-- The brand's protocol. Copied beside the driver. -->
    <PackageReference Include="HslCommunication" Version="..." />
  </ItemGroup>
</Project>
```

- **`x64`** - the programs are 64-bit only.
- **`EnableDynamicLoading`** writes the library's dependencies and its
  `.deps.json` beside it, which is how HslCommunication is found.
- **`Private="false"`** keeps `Sirius.Plc.dll` and `Sirius.Core.dll` out
  of the output. A copy left beside the driver by mistake is ignored: the
  program's own are always used, or it could not recognise your
  `IPlcDriverFactory` as its own.
- **`Version`** - bump it with each build handed to anyone.

### 3.2 Where it goes

| Folder | For |
|---|---|
| `plc-drivers\` beside `SiriusBuilder.exe` and `SiriusEdge.exe` | every user of that PC - how a machine is set up |
| `%APPDATA%\SiriusStudioPlus\plc-drivers\` | one user - for development |

Copy the whole build output folder - the driver, HslCommunication.dll and
the `.deps.json` - into a subfolder: `plc-drivers\Siemens\`. Folders are
searched at any depth. A DLL is recognised as a driver library by its
reference to `Sirius.Plc`, read without loading it, so HslCommunication
and other dependencies beside it are passed over.

**Restart Builder** to use a new build. Files are read into memory, not
locked, so you can rebuild with Builder open.

### 3.3 How it is loaded

- **Each library in a context of its own**, so two libraries may carry two
  versions of HslCommunication. `Sirius.Plc`, `Sirius.Core` and
  `CommunityToolkit.Mvvm` are always the program's - and so is anything
  else the program has already loaded, so its own assemblies are never
  loaded twice. What only the driver brings is the driver's.
- **The version is checked before any of your code runs.** The
  `Sirius.Plc` you built against must have the program's major version,
  and a minor no newer: a library built for 1.3 is refused by a 1.2
  program, with a sentence saying to update one or rebuild the other.
- **A library that will not load is skipped**, the others load. What
  went wrong is a sentence in Builder's PLC window ("What was done").
- **A key already taken** - by another library - is refused, naming who
  has it.

---

## 4. Walkthrough: Siemens S7 on HslCommunication

The company holds HslCommunication's Enterprise licence (`licences.md`
L5). HSL already speaks S7 - and Mitsubishi, Omron, Modbus, Rockwell,
Beckhoff and more - through clients that share one shape
(`IReadWriteNet`: `ReadBool`, `ReadInt16`, ... `Write`), so the work is a
thin layer: one base class for every HSL brand, and a few lines per brand.

> HslCommunication's classes and members below are as its documentation
> and samples give them. Check them against the version you build with -
> the compiler will tell you at once - and keep this guide in step.

### 4.1 The activation code

HSL stops working after a trial period (24 hours by its price page, 8 by
its README) without its activation code, which must be set once before
any client is used. **The code is a secret**: never in source, never in
the repository, never in the start kit.

```csharp
internal static class HslLicence
{
    private static readonly Lazy<bool> Activated = new(() =>
    {
        // Put there by whoever installs the machine - not in source.
        var code = Environment.GetEnvironmentVariable("SIRIUS_HSL_CODE");
        return !string.IsNullOrWhiteSpace(code) && HslCommunication.Authorization.SetAuthorizationCode(code);
    });

    /// <summary>Throws in words when HSL is not activated, rather than a line stopping hours later.</summary>
    public static void Ensure()
    {
        if (!Activated.Value)
            throw new PlcException(
                "HslCommunication is not activated: set SIRIUS_HSL_CODE on this PC. " +
                "Without it the PLC connection stops working after a trial period.");
    }
}
```

`Create` calls `HslLicence.Ensure()`, so a machine set up without the
code fails at once in Builder's PLC window, saying so - not on the line
at 3 a.m. An environment variable is one way; a file outside the
program's folder, read the same way, is another. Agree one for the
company and write it in the install instructions.

### 4.2 Siemens addresses

Engineers write TIA Portal's addresses; HSL wants its own forms. The
driver takes both and translates:

| Written | Means | HSL form |
|---|---|---|
| `M0.0`, `I0.0` / `E0.0`, `Q0.0` / `A0.0` | a bit in memory, inputs, outputs | `M0.0`, `I0.0`, `Q0.0` |
| `MB10`, `MW10`, `MD10` | byte, word, double word at 10 | `M10` |
| `IW64`, `QW64` | input / output word | `I64`, `Q64` |
| `DB34.DBX20.0` | bit 0 of byte 20 of DB34 | `DB34.20.0` |
| `DB34.DBB20`, `DB34.DBW20`, `DB34.DBD20` | byte, word, double word at 20 | `DB34.20` |
| `DB34.20.0`, `DB34.20` | the same, as HSL and the PLC team's prototype write it | unchanged |

The size letter (B, W, D, X) must agree with the type: `MW10` read as an
`Int32` is a mistake worth saying.

```csharp
using System.Text.RegularExpressions;

/// <summary>Siemens addresses, as TIA Portal writes them or as HSL does, in HSL's form.</summary>
internal static partial class S7Address
{
    [GeneratedRegex(@"^(?<area>[IEQAM])(?<size>[BWD])?(?<offset>\d+)(\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Area();

    [GeneratedRegex(@"^DB(?<db>\d+)\.(DB(?<size>[XBWD]))?(?<offset>\d+)(\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    /// <summary>HSL's form of the address, or null and why not.</summary>
    public static string? ToHsl(PlcVariable variable, out string? problem)
    {
        var text = variable.Address.Trim();
        var block = Block().Match(text);
        var area = block.Success ? Match.Empty : Area().Match(text);
        var found = block.Success ? block : area;

        if (!found.Success)
        {
            problem = $"'{text}' is not a Siemens address: write M0.0, MW10, MD20, I0.0, Q0.0, DB34.DBX20.0 or DB34.DBW20.";
            return null;
        }

        var size = found.Groups["size"].Value.ToUpperInvariant();
        var hasBit = found.Groups["bit"].Success;
        var offset = found.Groups["offset"].Value;

        problem = Agrees(text, variable.Type, size, hasBit);
        if (problem is not null)
            return null;

        var prefix = block.Success
            ? $"DB{block.Groups["db"].Value}."
            : found.Groups["area"].Value.ToUpperInvariant() switch { "E" => "I", "A" => "Q", var letter => letter };

        return hasBit ? $"{prefix}{offset}.{found.Groups["bit"].Value}" : $"{prefix}{offset}";
    }

    /// <summary>Whether the address's size and the type asked for agree.</summary>
    private static string? Agrees(string text, PlcType type, string size, bool hasBit)
    {
        if (type == PlcType.Bool)
            return hasBit ? null : $"{text} is not a bit: a Bool needs one, as M0.0 or DB34.DBX20.0.";

        if (hasBit || size == "X")
            return $"{text} is a bit: a {type} needs a byte address, as MW10 or DB34.DBW20.";

        var bits = type switch
        {
            PlcType.Byte => 8,
            PlcType.Int16 or PlcType.UInt16 => 16,
            PlcType.Int32 or PlcType.UInt32 or PlcType.Real => 32,
            _ => 0,                      // LReal, String: no size letter of their own
        };

        var given = size switch { "B" => 8, "W" => 16, "D" => 32, _ => 0 };

        if (given == 0 || bits == given)
            return null;

        var letter = bits switch { 8 => "B", 16 => "W", 32 => "D", _ => "" };
        return bits == 0
            ? $"{text} is {given} bits; write a {type} without the size letter, as DB34.20."
            : $"{text} is {given} bits; a {type} is {bits} - write {letter} rather than {size}.";
    }
}
```

**Strings.** An S7 `STRING` has two header bytes - maximum and actual
length - before its characters. HSL reads a Siemens string with its
header by one call and raw bytes by another (`ReadString(address,
length)`); check which your HSL version offers, and say in
`AddressExamples` which the driver reads.

### 4.3 A base for every HSL brand

HSL's clients share `IReadWriteNet`, so reading and writing every type
is written once. What differs per brand - making the client, opening it,
translating an address - is three methods.

```csharp
using HslCommunication;
using HslCommunication.Core;
using Sirius.Plc;

/// <summary>An IPlcDriver over one HslCommunication client: reading, writing, locking and errors, for every brand.</summary>
public abstract class HslDriver<TClient> : IPlcDriver
    where TClient : class, IReadWriteNet
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TClient? _client;
    private volatile bool _connected;

    protected HslDriver(PlcConnection connection) => Connection = connection;

    protected PlcConnection Connection { get; }

    public abstract string Description { get; }

    public bool IsConnected => _connected;

    /// <summary>A new client for the PLC, with its settings - not yet connected.</summary>
    protected abstract TClient MakeClient();

    /// <summary>Opens the client's connection: HSL's ConnectServer.</summary>
    protected abstract OperateResult Open(TClient client);

    /// <summary>Closes it: HSL's ConnectClose.</summary>
    protected abstract void Close(TClient client);

    /// <summary>HSL's form of an address, or null and why not - the brand's own rules.</summary>
    protected abstract string? ToHsl(PlcVariable variable, out string? problem);

    public string? AddressProblem(PlcVariable variable)
    {
        if (variable.Type == PlcType.String && variable.Length <= 0)
            return $"{variable.Address}: a String needs a length.";

        ToHsl(variable, out var problem);
        return problem;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_connected)
                return;

            _client ??= MakeClient();
            var client = _client;

            // HSL's calls take no token: waited on with one, so a stop is not held up.
            var opened = await Task.Run(() => Open(client), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

            if (!opened.IsSuccess)
                throw new PlcException($"Cannot connect to {Description}: {opened.Message}");

            _connected = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_client is { } client)
            {
                try { Close(client); }
                catch (Exception) { /* Never throws: there is nothing to tell. */ }
            }

            _connected = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken) =>
        Content(await ExchangeAsync(variable, client => Read(client, Address(variable), variable), cancellationToken).ConfigureAwait(false));

    public async Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken) =>
        await ExchangeAsync(variable, client => Write(client, Address(variable), variable, value), cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // ---- the exchange: one at a time, errors in words ----

    private async Task<OperateResult> ExchangeAsync(PlcVariable variable, Func<TClient, OperateResult> exchange, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_connected || _client is not { } client)
                throw new PlcException($"{Description} is not connected.");

            var result = await Task.Run(() => exchange(client), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                // A failure the connection caused: say so, so the runner connects again.
                if (LostConnection(result))
                    _connected = false;

                throw new PlcException($"{variable.Address}: {result.Message}");
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What a read gave, as the .NET type its PlcType is carried as.</summary>
    private static object Content(OperateResult result) => result switch
    {
        OperateResult<bool> read => read.Content,
        OperateResult<byte> read => read.Content,
        OperateResult<short> read => read.Content,
        OperateResult<ushort> read => read.Content,
        OperateResult<int> read => read.Content,
        OperateResult<uint> read => read.Content,
        OperateResult<float> read => read.Content,
        OperateResult<double> read => read.Content,
        OperateResult<string> read => read.Content ?? string.Empty,
        _ => throw new PlcException("The PLC's answer was not one this driver understands."),
    };

    /// <summary>
    /// Whether a failed result means the connection is gone. HSL reports
    /// socket failures with negative error codes in the versions we have
    /// seen; check yours, and override per brand if need be.
    /// </summary>
    protected virtual bool LostConnection(OperateResult result) => result.ErrorCode < 0;

    private string Address(PlcVariable variable) =>
        ToHsl(variable, out var problem) ?? throw new PlcException(problem!);

    private static OperateResult Read(TClient client, string address, PlcVariable variable) => variable.Type switch
    {
        PlcType.Bool => client.ReadBool(address),
        PlcType.Int16 => client.ReadInt16(address),
        PlcType.UInt16 => client.ReadUInt16(address),
        PlcType.Int32 => client.ReadInt32(address),
        PlcType.UInt32 => client.ReadUInt32(address),
        PlcType.Real => client.ReadFloat(address),
        PlcType.LReal => client.ReadDouble(address),
        PlcType.String => client.ReadString(address, (ushort)variable.Length),
        PlcType.Byte => ReadByte(client, address),
        _ => throw new PlcException($"{variable.Type} is not a type this driver reads."),
    };

    /// <summary>A byte: the first of what Read returns, since not every brand's client reads one alone.</summary>
    private static OperateResult ReadByte(TClient client, string address)
    {
        var read = client.Read(address, 1);
        return read.IsSuccess ? OperateResult.CreateSuccessResult(read.Content[0]) : read;
    }

    private static OperateResult Write(TClient client, string address, PlcVariable variable, object value) => value switch
    {
        bool flag => client.Write(address, flag),
        byte one => client.Write(address, new[] { one }),
        short whole => client.Write(address, whole),
        ushort whole => client.Write(address, whole),
        int whole => client.Write(address, whole),
        uint whole => client.Write(address, whole),
        float number => client.Write(address, number),
        double number => client.Write(address, number),
        string text => client.Write(address, text.Length > variable.Length && variable.Length > 0 ? text[..variable.Length] : text),
        _ => throw new PlcException($"A {value.GetType().Name} cannot be written as a {variable.Type}."),
    };
}
```

One place to settle against your HSL version, marked in the code: which
error codes mean the connection is gone (`LostConnection`). The cable-pull
test in §6.2 tells you when it is wrong: after the cable goes back in, the
workflow must carry on by itself.

### 4.4 The S7 driver itself

```csharp
using HslCommunication;
using HslCommunication.Profinet.Siemens;
using Sirius.Plc;

public sealed class SiemensS7DriverFactory : IPlcDriverFactory
{
    private static readonly Dictionary<string, SiemensPLCS> Cpus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S7-1200"] = SiemensPLCS.S1200,
        ["S7-1500"] = SiemensPLCS.S1500,
        ["S7-300"] = SiemensPLCS.S300,
        ["S7-400"] = SiemensPLCS.S400,
        ["S7-200 SMART"] = SiemensPLCS.S200Smart,
    };

    public string Key => "siemens-s7";

    public string Name => "Siemens S7";

    public string AddressHint => "The PLC's IP address, as 192.168.0.10. In TIA Portal, allow PUT/GET access, and use non-optimised DBs.";

    public string AddressExamples => "M0.0, MW10, MD20, I0.0, Q0.0, DB34.DBX20.0, DB34.DBW22, DB34.20.0";

    public IReadOnlyList<PlcDriverOption> Options { get; } =
    [
        new("cpu", "CPU", "S7-1200", "Which family of CPU.", ["S7-1200", "S7-1500", "S7-300", "S7-400", "S7-200 SMART"]),
        new("rack", "Rack", "0", "The CPU's rack: 0, nearly always."),
        new("slot", "Slot", "1", "The CPU's slot: 1 for an S7-1200 or S7-1500, 2 for an S7-300."),
        new("timeout", "Timeout (ms)", "3000", "How long to wait for the PLC to answer."),
    ];

    public IPlcDriver Create(PlcConnection connection)
    {
        HslLicence.Ensure();

        if (!Cpus.TryGetValue(connection.Option("cpu") ?? "", out var cpu))
            throw new PlcException($"The CPU '{connection.Option("cpu")}' is not one this driver knows: {string.Join(", ", Cpus.Keys)}.");

        if (!byte.TryParse(connection.Option("rack"), out var rack) || !byte.TryParse(connection.Option("slot"), out var slot))
            throw new PlcException("Rack and slot are whole numbers, as 0 and 1.");

        if (!int.TryParse(connection.Option("timeout"), out var timeout) || timeout < 100)
            throw new PlcException("The timeout is a number of milliseconds, at least 100.");

        if (string.IsNullOrWhiteSpace(connection.Address))
            throw new PlcException("No IP address is given.");

        return new SiemensS7Driver(connection, cpu, rack, slot, timeout);
    }
}

internal sealed class SiemensS7Driver(PlcConnection connection, SiemensPLCS cpu, byte rack, byte slot, int timeout)
    : HslDriver<SiemensS7Net>(connection)
{
    public override string Description => $"Siemens {connection.Option("cpu")} at {connection.Address}";

    protected override SiemensS7Net MakeClient() => new(cpu, connection.Address.Trim())
    {
        Rack = rack,
        Slot = slot,
        ConnectTimeOut = timeout,
        ReceiveTimeOut = timeout,
    };

    protected override OperateResult Open(SiemensS7Net client) => client.ConnectServer();

    protected override void Close(SiemensS7Net client) => client.ConnectClose();

    protected override string? ToHsl(PlcVariable variable, out string? problem) => S7Address.ToHsl(variable, out problem);
}
```

**On the PLC side** (say it in `AddressHint`, and in the install notes):

- S7-1200 and S7-1500: in TIA Portal, the CPU's *Protection & Security*
  must allow **PUT/GET communication from remote partners**, and a DB read
  by absolute address must have **optimised block access switched off**.
  Without the first, every read fails; without the second, DB addresses
  mean nothing.
- Port 102 open between the PC and the PLC.

---

## 5. Other brands

The same base class; per brand, the client and its address rules. The
HSL class names are as its documentation gives them - check them against
your version.

| Brand | Key | HSL client | Settings to offer | Addresses, as engineers write them |
|---|---|---|---|---|
| Siemens S7 | `siemens-s7` | `SiemensS7Net` | CPU, rack, slot | `M0.0`, `MW10`, `DB34.DBW20` |
| Modbus TCP (any brand) | `modbus-tcp` | `ModbusTcpNet` | port (502), unit ID, word order, whether addresses start at 0 | `40001` / `100` holding registers; coils for bits |
| Mitsubishi MC (Q, L, iQ-R, FX5) | `mitsubishi-mc` | `MelsecMcNet` | port, binary or ASCII | `D100`, `M100`, `X10`, `Y10` |
| Omron FINS (CJ, CP, NJ/NX) | `omron-fins` | `OmronFinsNet` | port, node numbers | `D100`, `D100.1`, `CIO100.0`, `W100` |
| Rockwell / Allen-Bradley (ControlLogix, CompactLogix) | `rockwell-ab` | `AllenBradleyNet` | slot | tag names: `Trigger`, `Program:Main.Count` |
| Beckhoff TwinCAT | `beckhoff-ads` | `BeckhoffAdsNet` | AMS Net IDs | symbols: `MAIN.bTrigger` |
| Keyence, Panasonic, Inovance, Delta... | `keyence-mc`, ... | see HSL | | |

Things that differ between brands, to decide per driver and say in
`AddressHint` and `AddressExamples`:

- **Word and byte order.** Modbus devices disagree on the order of the two
  words of a 32-bit value; offer it as a setting (`ABCD`, `CDAB`,
  `BADC`, `DCBA`), defaulting to the brand's own.
- **Where numbering starts.** Modbus `40001` is register 0 to some
  devices and 1 to others.
- **Tags or addresses.** Rockwell and Beckhoff read by name. A name is an
  address as far as Sirius is concerned; `AddressProblem` can only check
  its shape.
- **Bits in words.** `D100.1` is legal on an Omron, not on every brand.

**Without HSL.** An open library is as good, and needs no activation
code: S7.Net Plus (MIT) for Siemens, FluentModbus or NModbus (MIT) for
Modbus TCP, libplctag (MPL 2.0 / LGPL) for Rockwell. Write `IPlcDriver`
directly; everything in §2.3 still applies. Check a library's licence
before it ships (`licences.md`).

**OPC UA** (`opc-ua`) suits a PLC that offers it: one driver for every
brand that speaks it, addresses as node IDs (`ns=3;s="DB34"."Trigger"`).
The OPC Foundation's .NET Standard stack is the usual library; it is
heavier than a native protocol, and certificates need setting up.

---

## 6. Testing a driver

### 6.1 The conformance check

`PlcDriverCheck.RunAsync` runs what every driver must do against a real
PLC on the bench: connects; knows its own addresses; writes and reads back
each type, getting back the exact .NET type; reads many at once; takes
several requests at once; stops when cancelled; refuses an address the
PLC has not got with a `PlcException`; disconnects and connects again;
lets go.

```csharp
[Fact]
public async Task The_s7_driver_passes_the_conformance_check()
{
    var plan = new PlcDriverCheckPlan
    {
        // Addresses in the bench PLC nothing else uses - they are written.
        WriteAndReadBack =
        {
            new(new PlcVariable("DB100.DBX0.0", PlcType.Bool), true),
            new(new PlcVariable("DB100.DBW2", PlcType.Int16), (short)-1234),
            new(new PlcVariable("DB100.DBW4", PlcType.UInt16), (ushort)54321),
            new(new PlcVariable("DB100.DBD6", PlcType.Int32), 123456789),
            new(new PlcVariable("DB100.DBD10", PlcType.Real), 3.25f),
        },
        ReadOnly = { new PlcVariable("I0.0", PlcType.Bool) },
        Missing = new PlcVariable("DB999.DBW0", PlcType.Int16),   // a DB the PLC has not got
    };

    var connection = new PlcConnection("siemens-s7", "192.168.0.10",
        new Dictionary<string, string> { ["cpu"] = "S7-1200", ["rack"] = "0", ["slot"] = "1", ["timeout"] = "3000" });

    var results = await PlcDriverCheck.RunAsync(() => new SiemensS7DriverFactory().Create(connection), plan);

    Assert.All(results, result => Assert.True(result.Passed, result.ToString()));
}
```

Each failure says what is wrong in a sentence: "read back a Int32, not
the Int16 a Int16 is carried as", "threw InvalidOperationException: ... -
a driver says what went wrong with a PlcException", "read with a
cancelled token instead of stopping". The simulated PLC passes every
check; the check itself is tested against a deliberately sloppy driver.

### 6.2 On the bench, by hand

What a program cannot do to a PLC - do these with each new driver, and
each new brand of PLC, before it goes to a customer:

| Do | Expect |
|---|---|
| Builder ▸ controller ▸ PLC… ▸ Test Connection, PLC on | "Connected to ..., in N ms." |
| The same, with a wrong IP address | "Could not connect: ..." within the timeout - no hang |
| Read and Write each type in the PLC window, and check in TIA Portal / the PLC's own tool | the same values both sides |
| Test Run a workflow, then **pull the Ethernet cable** | the log says the connection was lost, once; the screen shows "not connected" |
| Put the cable back | "Connected to ..." again within a few seconds, without restarting; the next trigger works |
| Switch the PLC to STOP, then RUN | reads may fail while stopped, in words; it carries on after |
| Switch the PLC off and on | as the cable |
| A workflow with a PLC Monitor polling every 20 ms for an hour | no growth in memory, no errors in the log |
| HSL without its activation code (`SIRIUS_HSL_CODE` unset) | Test Connection says HSL is not activated - at once |

### 6.3 Without a PLC

Builder's simulated PLC stands in for one while workflows are built:
choose *Simulated PLC* as the driver, and its memory is in the PLC window,
a bit flipped with a click. A workflow built against it runs unchanged
on the real PLC once the driver and address are changed - as long as the
addresses are written as the real PLC's are.

---

## 7. Before handing a driver over

- [ ] `Key` and every option's `Name` are final: they are saved in projects.
- [ ] The conformance check passes on a real PLC of each family it claims.
- [ ] The bench checks in §6.2 are done, the cable pull above all.
- [ ] Every `PlcException` message reads well to someone who did not write the driver.
- [ ] `AddressHint` says what the PLC needs set on its side (PUT/GET, ports).
- [ ] `AddressExamples` shows the forms the driver accepts.
- [ ] The activation code is nowhere in the source or the output folder.
- [ ] `Version` is bumped.
- [ ] The output folder - driver, dependencies, `.deps.json` - goes into
      `plc-drivers\<Brand>\`; nothing else is needed.
- [ ] Each library it ships is in `THIRD-PARTY-NOTICES.txt`
      (HslCommunication: "used under a commercial licence").

---

## 8. What is built, and what is not

Built (decisions 74-76): the interface and its version check, discovery
in `plc-drivers\` folders with each library isolated, driver settings in
Builder's PLC window and the project file, the simulated PLC, the
conformance check, and Builder and Edge running controllers.

The smallest working library is `tests/TestPlcDriver` in the repository:
a project file as §3.1's, one factory, one driver - loaded by the tests
from a folder, as a real one is.

Not yet (backlog F23d-e):

- the S7 and Modbus drivers themselves, built and checked against real
  PLCs - §4 is their design;
- the start kit: `Sirius.Plc` as a NuGet package, a template driver
  project and this guide, zipped as the custom-node SDK is - until then,
  §3.1's references to an installed Sirius;
- reading many addresses in one exchange: the default reads them in
  turn, which is fine for a handful;
- a PLC's own subscriptions in place of polling.

Changes to the interface follow the custom-node rules: additions bump
the minor version and break nothing; anything that would break a driver
waits for a major version, with notice.
