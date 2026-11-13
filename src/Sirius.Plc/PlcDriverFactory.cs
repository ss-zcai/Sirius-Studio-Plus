namespace Sirius.Plc;

/// <summary>
/// One setting a driver needs beyond the PLC's address - an S7's CPU type,
/// rack and slot; a Modbus unit ID - shown in Builder's PLC window and kept
/// with the controller in the project.
/// </summary>
/// <param name="Name">The key it is kept under: "cpu", "rack". Never change it once released.</param>
/// <param name="Label">What Builder calls it: "CPU", "Rack".</param>
/// <param name="Default">Its value until someone sets it.</param>
/// <param name="Description">A sentence saying what it is and what to put.</param>
/// <param name="Choices">The values it may take, offered as a list; null for free text.</param>
public sealed record PlcDriverOption(
    string Name,
    string Label,
    string Default,
    string? Description = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>
/// A kind of PLC this program can talk to: what it is called, what it
/// needs set, and how to make a driver for one. A driver library holds one
/// or more public classes implementing this, each with a constructor that
/// takes nothing, and nothing else is needed for the program to find them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is saved in every project</b> using the driver - it is what
/// <c>"driver": "siemens-s7"</c> in a <c>.sproj</c> names - so it never
/// changes once released. Lower case, a vendor and a family:
/// <c>siemens-s7</c>, <c>modbus-tcp</c>, <c>mitsubishi-mc</c>.
/// </para>
/// <para>
/// <see cref="Create"/> makes a driver without connecting: the program
/// connects when it is ready, and again whenever the connection is lost.
/// It throws <see cref="PlcException"/> for settings that cannot work - a
/// CPU type it does not know - with a sentence saying so.
/// </para>
/// </remarks>
public interface IPlcDriverFactory
{
    /// <summary>"siemens-s7". Saved in projects: never changed once released.</summary>
    string Key { get; }

    /// <summary>"Siemens S7" - what Builder lists.</summary>
    string Name { get; }

    /// <summary>What the PLC's address is: "The PLC's IP address, as 192.168.0.10."</summary>
    string AddressHint { get; }

    /// <summary>Examples of its addresses, for the PLC window: "M0.0, MW10, MD20, DB34.20.0".</summary>
    string AddressExamples { get; }

    /// <summary>Its settings beyond the address, in the order Builder shows them.</summary>
    IReadOnlyList<PlcDriverOption> Options { get; }

    /// <summary>A driver for one PLC, not yet connected.</summary>
    /// <exception cref="PlcException">The connection's settings cannot work.</exception>
    IPlcDriver Create(PlcConnection connection);
}

/// <summary>The version of the PLC driver interface this program offers.</summary>
/// <remarks>
/// A driver library built against a different major version, or a newer
/// minor one, is refused with a sentence saying which to rebuild against -
/// before any of its code runs. Kept with <c>Sirius.Plc</c>'s own assembly
/// version.
/// </remarks>
public static class PlcDriverInterface
{
    public static Version Version { get; } = typeof(IPlcDriver).Assembly.GetName().Version ?? new Version(1, 0);
}

/// <summary>The simulated PLC, as a driver: a memory shared by name within the program.</summary>
public sealed class SimulatedPlcDriverFactory : IPlcDriverFactory
{
    public string Key => SimulatedPlcDriver.Key;

    public string Name => "Simulated PLC";

    public string AddressHint => "Any name: two controllers with one name share one simulated PLC.";

    public string AddressExamples => "Any word with no spaces: M0.0, MW10, MD20, DB34.20.0";

    public IReadOnlyList<PlcDriverOption> Options { get; } = [];

    public IPlcDriver Create(PlcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new SimulatedPlc(SimulatedPlcMemory.Named(connection.Address));
    }
}
