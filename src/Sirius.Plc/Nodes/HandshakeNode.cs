using Sirius.Core.Nodes;
using Sirius.Core.Runtime;
using Sirius.Plc.Workflows;

namespace Sirius.Plc.Nodes;

/// <summary>
/// What a controller tells its PLC about itself, as the PLC waits on it
/// (F25): Ready, Busy, and an error code.
/// </summary>
public static class HandshakeCodes
{
    /// <summary>Nothing wrong: the last part was judged, pass or fail.</summary>
    public const short Ok = 0;

    /// <summary>A station could not inspect: faulted, busy, or not there.</summary>
    public const short StationFault = 1;

    /// <summary>A station gave no result by its deadline.</summary>
    public const short Late = 2;

    /// <summary>The camera, or whatever takes the picture, failed.</summary>
    public const short Camera = 3;

    /// <summary>The recipe could not judge the picture: a node failed, or no verdict.</summary>
    public const short Recipe = 4;

    /// <summary>The code a part leaves.</summary>
    public static short For(PartRecord part)
    {
        ArgumentNullException.ThrowIfNull(part);

        return part.Verdict switch
        {
            PartVerdict.Late => Late,
            PartVerdict.NotInspected => StationFault,
            _ when part.Shots.Any(shot => shot.SourceFailed) => Camera,
            _ when part.Shots.Any(shot => shot.NodeErrors > 0 || shot.Passed is null) => Recipe,
            _ => Ok,
        };
    }
}

/// <summary>
/// Keeps the PLC told how the controller is: Ready, Busy, and an error code
/// - written whenever they change, with nothing to wire.
/// </summary>
/// <remarks>
/// It watches rather than starts passes. Ready is on while the PLC is
/// connected, every station the workflow has inspect is ready, and no part
/// is in hand; Busy while one is. The error code is the last part's - or,
/// while a station is faulted, that.
/// </remarks>
[VisionNode(
    "Handshake",
    TypeKey = "plc.handshake",
    Icon = "ph_handshake",
    Category = NodeCategory.Plc,
    Description = "Keeps the PLC told how the vision system is, with nothing to wire: Ready while every station " +
                  "is ready and no part is in hand, Busy while one is, and an error code - 0 OK, 1 a station " +
                  "faulted or not inspecting, 2 late, 3 camera, 4 recipe. Leave an address blank to not write it.")]
public sealed class HandshakeNode : WorkflowEventNode
{
    private string _ready = string.Empty;
    private string _busy = string.Empty;
    private string _error = string.Empty;
    private int _pollInterval = 20;

    [Param("Ready", Group = "Addresses", Description = "A bit, on while the vision system can take a part: DB34.20.3.")]
    public string ReadyAddress
    {
        get => _ready;
        set => SetParam(ref _ready, value?.Trim() ?? string.Empty);
    }

    [Param("Busy", Group = "Addresses", Description = "A bit, on from a part's trigger to its answer written: DB34.20.4.")]
    public string BusyAddress
    {
        get => _busy;
        set => SetParam(ref _busy, value?.Trim() ?? string.Empty);
    }

    [Param("Error Code", Group = "Addresses", Description = "A word (Int16): 0 OK, 1 station faulted, 2 late, 3 camera, 4 recipe.")]
    public string ErrorAddress
    {
        get => _error;
        set => SetParam(ref _error, value?.Trim() ?? string.Empty);
    }

    [Param("Poll Interval (ms)", Min = 5, Max = 1000, Description = "How often it looks for a change to write.")]
    public int PollInterval
    {
        get => _pollInterval;
        set => SetParam(ref _pollInterval, Math.Clamp(value, 5, 1000));
    }

    private IEnumerable<PlcVariable> Variables()
    {
        if (ReadyAddress.Length > 0)
            yield return new PlcVariable(ReadyAddress, PlcType.Bool);

        if (BusyAddress.Length > 0)
            yield return new PlcVariable(BusyAddress, PlcType.Bool);

        if (ErrorAddress.Length > 0)
            yield return new PlcVariable(ErrorAddress, PlcType.Int16);
    }

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (!Variables().Any())
            yield return "No address is given: Ready, Busy or Error Code.";

        foreach (var variable in Variables())
        {
            if (check.AddressProblem(variable) is { } problem)
                yield return problem;
        }
    }

    /// <summary>How the controller is now, as the PLC is told it.</summary>
    internal static (bool Ready, bool Busy, short Error) Now(WorkflowWatch watch)
    {
        var busy = watch.IsBusy;
        var states = watch.StationsInspected.Select(station => watch.Stations?.StateOf(station)).ToArray();
        var faulted = states.Any(state => state is StationState.Faulted);
        var stationsReady = states.All(state => state is null or StationState.Ready or StationState.Running or StationState.Inspecting);

        var error = faulted
            ? HandshakeCodes.StationFault
            : watch.LastPart is { } last ? HandshakeCodes.For(last) : HandshakeCodes.Ok;

        return (watch.Driver.IsConnected && stationsReady && !faulted && !busy, busy, error);
    }

    protected internal override async Task WatchAsync(WorkflowWatch watch, CancellationToken cancellationToken)
    {
        // What the PLC was last told; forgotten when it is lost, so all is told again.
        (bool Ready, bool Busy, short Error)? told = null;
        string? problem = null;
        var poll = TimeSpan.FromMilliseconds(PollInterval);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!watch.Driver.IsConnected)
            {
                told = null;
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var now = Now(watch);

            if (now != told)
            {
                try
                {
                    if (ReadyAddress.Length > 0 && now.Ready != told?.Ready)
                        await watch.Driver.WriteAsync(new PlcVariable(ReadyAddress, PlcType.Bool), now.Ready, cancellationToken).ConfigureAwait(false);

                    if (BusyAddress.Length > 0 && now.Busy != told?.Busy)
                        await watch.Driver.WriteAsync(new PlcVariable(BusyAddress, PlcType.Bool), now.Busy, cancellationToken).ConfigureAwait(false);

                    if (ErrorAddress.Length > 0 && now.Error != told?.Error)
                        await watch.Driver.WriteAsync(new PlcVariable(ErrorAddress, PlcType.Int16), now.Error, cancellationToken).ConfigureAwait(false);

                    told = now;
                    problem = null;
                }
                catch (PlcException ex)
                {
                    // Told again in full on the next look; said once.
                    told = null;

                    if (ex.Message != problem)
                    {
                        problem = ex.Message;
                        watch.Warn($"Cannot tell the PLC: {ex.Message}");
                    }
                }
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }
}
