using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Runtime;
using Sirius.Plc.Workflows;

namespace Sirius.Plc.Nodes;

/// <summary>Which change of a watched address starts a pass.</summary>
public enum MonitorEdge
{
    /// <summary>Off to on: a bit set by the PLC.</summary>
    Rising,

    /// <summary>On to off.</summary>
    Falling,

    /// <summary>Any change of value - for a number or text.</summary>
    Change,
}

/// <summary>
/// The settings every node working on one PLC address has: where, and as
/// what.
/// </summary>
public abstract class PlcAddressNode : WorkflowNode
{
    private string _address = string.Empty;
    private PlcType _type = PlcType.Bool;
    private int _length = 20;

    [Param("Address", Description = "Where in the PLC, in its own words: DB34.20.0, M10.0, 40001, D100.")]
    public string Address
    {
        get => _address;
        set => SetParam(ref _address, value?.Trim() ?? string.Empty);
    }

    [Param("Type", Description = "What is kept there: a Bool for a bit, Int16 for a word, Real for a number with a point, String for text.")]
    public PlcType Type
    {
        get => _type;
        set => SetParam(ref _type, value);
    }

    [Param("Length", Min = 1, Max = 254, Description = "For a String: how many characters.")]
    public int Length
    {
        get => _length;
        set => SetParam(ref _length, value);
    }

    /// <summary>The address and type, as a driver takes them.</summary>
    public PlcVariable Variable => new(Address, Type, Type == PlcType.String ? Length : 0);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Address.Length == 0)
            yield return "No address is given.";
        else if (check.AddressProblem(Variable) is { } problem)
            yield return problem;
    }
}

/// <summary>Watches an address and starts a pass when it changes - the PLC saying "now".</summary>
[VisionNode(
    "PLC Monitor",
    TypeKey = "plc.monitor",
    Icon = "ph_plc_monitor",
    Category = NodeCategory.Plc,
    Description = "Watches an address in the PLC and starts the workflow when it changes - a bit rising, " +
                  "falling, or any change of a value. Watches again once what it started has finished.")]
public sealed class PlcMonitorNode : WorkflowEventNode
{
    private string _address = string.Empty;
    private PlcType _type = PlcType.Bool;
    private int _length = 20;
    private MonitorEdge _edge = MonitorEdge.Rising;
    private int _pollInterval = 20;

    public PlcMonitorNode()
    {
        Triggered = AddFired("Triggered");
        ValueOut = AddOutput<object>("Value");
        CountOut = AddOutput<int>("Count");
    }

    public OutputPort Triggered { get; }

    /// <summary>The value that changed, as it now is.</summary>
    public OutputPort ValueOut { get; }

    /// <summary>How many times it has fired since the controller started.</summary>
    public OutputPort CountOut { get; }

    [Param("Address", Description = "Where in the PLC, in its own words: DB34.20.0, M10.0, 40001, D100.")]
    public string Address
    {
        get => _address;
        set => SetParam(ref _address, value?.Trim() ?? string.Empty);
    }

    [Param("Type", Description = "What is kept there: a Bool for a bit, a number or text for Change.")]
    public PlcType Type
    {
        get => _type;
        set => SetParam(ref _type, value);
    }

    [Param("Length", Min = 1, Max = 254, Description = "For a String: how many characters.")]
    public int Length
    {
        get => _length;
        set => SetParam(ref _length, value);
    }

    [Param("Edge", Description = "Rising: off to on. Falling: on to off. Change: any new value.")]
    public MonitorEdge Edge
    {
        get => _edge;
        set => SetParam(ref _edge, value);
    }

    [Param("Poll Interval (ms)", Min = 5, Max = 10000, Description = "How often the address is read.")]
    public int PollInterval
    {
        get => _pollInterval;
        set => SetParam(ref _pollInterval, Math.Clamp(value, 5, 10000));
    }

    public PlcVariable Variable => new(Address, Type, Type == PlcType.String ? Length : 0);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Address.Length == 0)
            yield return "No address is given.";
        else if (check.AddressProblem(Variable) is { } problem)
            yield return problem;
    }

    /// <remarks>
    /// The first value read is where it starts from: a bit already on when
    /// the controller starts does not fire - the PLC has to raise it again.
    /// A change while the pass it started is running is not seen; a PLC
    /// waiting for an answer holds its bit until it has one.
    /// </remarks>
    protected internal override async Task WatchAsync(WorkflowWatch watch, CancellationToken cancellationToken)
    {
        object? last = null;
        var count = 0;
        string? problem = null;
        var poll = TimeSpan.FromMilliseconds(PollInterval);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!watch.Driver.IsConnected)
            {
                // Starts afresh when the PLC is back: an edge across a lost connection is no edge.
                last = null;
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                continue;
            }

            object now;

            try
            {
                now = await watch.Driver.ReadAsync(Variable, cancellationToken).ConfigureAwait(false);
                problem = null;
            }
            catch (PlcException ex)
            {
                if (ex.Message != problem)
                {
                    problem = ex.Message;
                    watch.Warn($"Cannot read {Variable}: {ex.Message}");
                }

                await Task.Delay(poll < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : poll, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (last is not null && Fires(last, now))
            {
                var fired = ++count;
                var value = now;

                await watch.RaiseAsync(
                        step =>
                        {
                            step.Set(ValueOut, value);
                            step.Set(CountOut, fired);
                            step.Fire(Triggered);
                        },
                        $"{EdgeWord} on {Address} (event {fired})")
                    .ConfigureAwait(false);
            }

            last = now;
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool Fires(object before, object now) => Edge switch
    {
        MonitorEdge.Rising => !PlcValues.IsOn(before) && PlcValues.IsOn(now),
        MonitorEdge.Falling => PlcValues.IsOn(before) && !PlcValues.IsOn(now),
        _ => PlcValues.Text(before) != PlcValues.Text(now),
    };

    private string EdgeWord => Edge switch
    {
        MonitorEdge.Rising => "rising edge",
        MonitorEdge.Falling => "falling edge",
        _ => "change",
    };
}

/// <summary>Reads an address when triggered.</summary>
[VisionNode(
    "PLC Read",
    TypeKey = "plc.read",
    Icon = "ph_plc_read",
    Category = NodeCategory.Plc,
    Description = "Reads an address in the PLC when triggered - a tray number, a position, a mode - " +
                  "and gives its value. Done when read; Failed when the PLC did not answer.")]
public sealed class PlcReadNode : PlcAddressNode
{
    public PlcReadNode()
    {
        Trigger = AddTrigger();
        ValueOut = AddOutput<object>("Value");
        Done = AddFired("Done");
        Failed = AddFired("Failed");
    }

    public InputPort Trigger { get; }

    public OutputPort ValueOut { get; }

    public OutputPort Done { get; }

    public OutputPort Failed { get; }

    public override OutputPort? FailedOutput => Failed;

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        var value = await step.Driver.ReadAsync(Variable, step.CancellationToken).ConfigureAwait(false);

        step.Set(ValueOut, value);
        step.Fire(Done);
        step.Trace($"Read {Variable}: {PlcValues.Text(value)}.");
    }
}

/// <summary>Writes a value to an address when triggered.</summary>
[VisionNode(
    "PLC Write",
    TypeKey = "plc.write",
    Icon = "ph_plc_write",
    Category = NodeCategory.Plc,
    Description = "Writes to an address in the PLC when triggered: the value wired in, or the one set here. " +
                  "Done when written; Failed when the PLC did not take it.")]
public sealed class PlcWriteNode : PlcAddressNode
{
    private string _value = "true";

    public PlcWriteNode()
    {
        Trigger = AddTrigger();
        ValueIn = AddInput<object>("Value", isRequired: false);
        Done = AddFired("Done");
        Failed = AddFired("Failed");
    }

    public InputPort Trigger { get; }

    /// <summary>What to write; unwired, <see cref="Value"/> is written.</summary>
    public InputPort ValueIn { get; }

    public OutputPort Done { get; }

    public OutputPort Failed { get; }

    public override OutputPort? FailedOutput => Failed;

    [Param("Value", Description = "What is written when nothing is wired to Value: true, 1, 3.5, or text.")]
    public string Value
    {
        get => _value;
        set => SetParam(ref _value, value ?? string.Empty);
    }

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        var given = step.IsWired(ValueIn) ? step.Get(ValueIn) : Value;
        var value = PlcValues.Convert(given, Type);

        await step.Driver.WriteAsync(Variable, value, step.CancellationToken).ConfigureAwait(false);

        step.Fire(Done);
        step.Trace($"Wrote {Variable}: {PlcValues.Text(value)}.");
    }
}

/// <summary>Sets a bit, waits, and clears it: "done", said to the PLC.</summary>
[VisionNode(
    "PLC Pulse",
    TypeKey = "plc.pulse",
    Icon = "ph_plc_pulse",
    Category = NodeCategory.Plc,
    Description = "Sets a bit in the PLC, waits, and clears it again - a pulse the PLC sees as \"done\".")]
public sealed class PlcPulseNode : WorkflowNode
{
    private string _address = string.Empty;
    private int _width = 200;

    public PlcPulseNode()
    {
        Trigger = AddTrigger();
        Done = AddFired("Done");
        Failed = AddFired("Failed");
    }

    public InputPort Trigger { get; }

    public OutputPort Done { get; }

    public OutputPort Failed { get; }

    public override OutputPort? FailedOutput => Failed;

    [Param("Address", Description = "The bit: DB34.20.1, M10.1, Q0.0.")]
    public string Address
    {
        get => _address;
        set => SetParam(ref _address, value?.Trim() ?? string.Empty);
    }

    [Param("Width (ms)", Min = 10, Max = 60000, Description = "How long the bit stays on - long enough for the PLC's scan to see it.")]
    public int Width
    {
        get => _width;
        set => SetParam(ref _width, Math.Clamp(value, 10, 60000));
    }

    public PlcVariable Variable => new(Address, PlcType.Bool);

    public override TimeSpan? StepTimeout => TimeSpan.FromMilliseconds(Width) + TimeSpan.FromSeconds(5);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Address.Length == 0)
            yield return "No address is given.";
        else if (check.AddressProblem(Variable) is { } problem)
            yield return problem;
    }

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        await step.Driver.WriteAsync(Variable, true, step.CancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Width, step.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cleared even when stopping part way: a bit left on would read as "done" for ever.
            await step.Driver.WriteAsync(Variable, false, CancellationToken.None).ConfigureAwait(false);
        }

        step.Fire(Done);
        step.Trace($"Pulsed {Address} for {Width} ms.");
    }
}

/// <summary>Waits for an address to hold a value.</summary>
[VisionNode(
    "PLC Wait For",
    TypeKey = "plc.waitFor",
    Icon = "ph_plc_wait",
    Category = NodeCategory.Plc,
    Description = "Waits until an address holds a value - the PLC acknowledging, a part in place. " +
                  "Done when it does; Timed Out when it has not within the time given.")]
public sealed class PlcWaitForNode : PlcAddressNode
{
    private string _value = "true";
    private int _timeout = 5000;
    private int _pollInterval = 20;

    public PlcWaitForNode()
    {
        Trigger = AddTrigger();
        ValueOut = AddOutput<object>("Value");
        Done = AddFired("Done");
        TimedOut = AddFired("Timed Out");
        Failed = AddFired("Failed");
    }

    public InputPort Trigger { get; }

    public OutputPort ValueOut { get; }

    public OutputPort Done { get; }

    public OutputPort TimedOut { get; }

    public OutputPort Failed { get; }

    public override OutputPort? FailedOutput => Failed;

    [Param("Value", Description = "What it waits for: true, 0, 3, or text.")]
    public string Value
    {
        get => _value;
        set => SetParam(ref _value, value ?? string.Empty);
    }

    [Param("Timeout (ms)", Min = 10, Max = 600000, Description = "How long to wait before Timed Out.")]
    public int Timeout
    {
        get => _timeout;
        set => SetParam(ref _timeout, Math.Clamp(value, 10, 600000));
    }

    [Param("Poll Interval (ms)", Min = 5, Max = 10000, Description = "How often the address is read while waiting.")]
    public int PollInterval
    {
        get => _pollInterval;
        set => SetParam(ref _pollInterval, Math.Clamp(value, 5, 10000));
    }

    public override TimeSpan? StepTimeout => TimeSpan.FromMilliseconds(Timeout) + TimeSpan.FromSeconds(5);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        foreach (var problem in base.Check(check))
            yield return problem;

        string? wrong = null;

        try
        {
            PlcValues.Convert(Value, Type);
        }
        catch (PlcException ex)
        {
            wrong = ex.Message;
        }

        if (wrong is not null)
            yield return wrong;
    }

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        var wanted = PlcValues.Text(PlcValues.Convert(Value, Type));
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(Timeout);

        while (true)
        {
            var now = await step.Driver.ReadAsync(Variable, step.CancellationToken).ConfigureAwait(false);

            if (PlcValues.Text(now) == wanted)
            {
                step.Set(ValueOut, now);
                step.Fire(Done);
                step.Trace($"{Variable} is {wanted}.");
                return;
            }

            if (DateTime.UtcNow >= until)
            {
                step.Set(ValueOut, now);
                step.Fire(TimedOut);
                step.Log($"{Address} was still {PlcValues.Text(now)} after {Timeout} ms, not {wanted}.");
                return;
            }

            await Task.Delay(PollInterval, step.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Gathers values under names - a pallet ID, a position, a run mode -
/// into what an inspection is told about its part.
/// </summary>
[VisionNode(
    "Gather",
    TypeKey = "plc.gather",
    Icon = "ph_gather",
    Category = NodeCategory.Plc,
    Description = "Puts values together under names - PalletId, PositionId, RunMode - as what an inspection " +
                  "is told about its part, kept with its picture. Runs once every value wired in has been read.")]
public sealed class GatherNode : WorkflowNode
{
    /// <summary>How many values it takes.</summary>
    public const int Slots = 8;

    private readonly string[] _names = Enumerable.Range(1, Slots).Select(i => $"Value {i}").ToArray();
    private readonly InputPort[] _values;

    public GatherNode()
    {
        Trigger = AddTrigger();
        _values = Enumerable.Range(1, Slots).Select(i => AddInput<object>($"Value {i}", isRequired: false)).ToArray();
        PartOut = AddOutput<PartValues>("Part");
        Gathered = AddFired("Gathered");
    }

    /// <summary>Optional: wired, it runs when triggered rather than when its values are in.</summary>
    public InputPort Trigger { get; }

    public IReadOnlyList<InputPort> Values => _values;

    public OutputPort PartOut { get; }

    public OutputPort Gathered { get; }

    [Param("Name 1", Group = "Names")]
    public string Name1 { get => _names[0]; set => SetName(0, value); }

    [Param("Name 2", Group = "Names")]
    public string Name2 { get => _names[1]; set => SetName(1, value); }

    [Param("Name 3", Group = "Names")]
    public string Name3 { get => _names[2]; set => SetName(2, value); }

    [Param("Name 4", Group = "Names")]
    public string Name4 { get => _names[3]; set => SetName(3, value); }

    [Param("Name 5", Group = "Names")]
    public string Name5 { get => _names[4]; set => SetName(4, value); }

    [Param("Name 6", Group = "Names")]
    public string Name6 { get => _names[5]; set => SetName(5, value); }

    [Param("Name 7", Group = "Names")]
    public string Name7 { get => _names[6]; set => SetName(6, value); }

    [Param("Name 8", Group = "Names")]
    public string Name8 { get => _names[7]; set => SetName(7, value); }

    /// <summary>The name a value is gathered under, from 0.</summary>
    public string NameOf(int slot) => _names[slot];

    private void SetName(int slot, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null) =>
        SetParam(ref _names[slot], value?.Trim() ?? string.Empty, property);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        var wired = Enumerable.Range(0, Slots).Where(slot => _values[slot].IsConnected).ToArray();

        if (wired.Length == 0)
            yield return "No value is wired in.";

        foreach (var slot in wired.Where(slot => _names[slot].Length == 0))
            yield return $"Value {slot + 1} has no name.";

        foreach (var name in wired.Select(slot => _names[slot]).Where(name => name.Length > 0)
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            yield return $"Two values are called '{name.Key}'.";
    }

    protected internal override Task RunAsync(WorkflowStep step)
    {
        var values = new List<KeyValuePair<string, object?>>();

        for (var slot = 0; slot < Slots; slot++)
        {
            if (step.IsWired(_values[slot]))
                values.Add(new(_names[slot].Length > 0 ? _names[slot] : $"Value {slot + 1}", step.Get(_values[slot])));
        }

        var part = new PartValues(values);

        step.Set(PartOut, part);
        step.Fire(Gathered);
        step.Trace(part.ToString());
        return Task.CompletedTask;
    }
}
