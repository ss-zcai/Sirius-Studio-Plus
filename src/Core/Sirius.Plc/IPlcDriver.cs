namespace Sirius.Plc;

/// <summary>
/// One PLC, as a controller's workflow talks to it: connect, read and
/// write a value at an address. A brand of PLC is a class implementing
/// this; the workflow's nodes are written once, against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Addresses are the brand's own</b> - <c>DB34.20.0</c>, <c>40001</c>,
/// <c>D100</c> - and only the driver reads them. Nothing else in the
/// program parses an address.
/// </para>
/// <para>
/// <b>Called from more than one thread at once</b>: a Monitor polls while
/// a workflow's step reads. A driver keeps itself safe, by a lock around
/// each exchange with the PLC if nothing better.
/// </para>
/// <para>
/// <b>Failures are <see cref="PlcException"/>s</b>, with a sentence saying
/// what went wrong. A lost connection sets <see cref="IsConnected"/>
/// false; the runner connects again by itself.
/// </para>
/// </remarks>
public interface IPlcDriver : IAsyncDisposable
{
    /// <summary>What and where, for the log and the screen: "Siemens S7-1200 at 192.168.0.10".</summary>
    string Description { get; }

    bool IsConnected { get; }

    /// <summary>Connects, or throws <see cref="PlcException"/> saying why not.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Disconnects. Never throws.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// What is wrong with an address, in a sentence - "DB34.20.9: a bit
    /// is 0 to 7" - or null when the driver can use it. Asked when a
    /// workflow is checked, before anything is read.
    /// </summary>
    string? AddressProblem(PlcVariable variable);

    /// <summary>The value at an address, as <see cref="PlcValues.ClrType"/> of its type carries it.</summary>
    Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a value, already converted by <see cref="PlcValues.Convert"/>
    /// to the variable's type.
    /// </summary>
    Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken);

    /// <summary>
    /// Several values in one go. A driver whose PLC can read many addresses
    /// in one exchange does so; this one reads them in turn.
    /// </summary>
    async Task<IReadOnlyList<object>> ReadManyAsync(IReadOnlyList<PlcVariable> variables, CancellationToken cancellationToken)
    {
        var values = new object[variables.Count];

        for (var i = 0; i < variables.Count; i++)
            values[i] = await ReadAsync(variables[i], cancellationToken).ConfigureAwait(false);

        return values;
    }
}
