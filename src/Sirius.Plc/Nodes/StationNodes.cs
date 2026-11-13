using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Plc.Workflows;

namespace Sirius.Plc.Nodes;

/// <summary>
/// An inspection, as a workflow keeps it: how it went, and what the
/// station published that a PLC could be told - numbers, yes/no, text,
/// grades. Pictures are left behind; they are the station's.
/// </summary>
public sealed record InspectionResult(
    string Station,
    long Serial,
    DateTime Time,
    string Verdict,
    bool? Passed,
    int NodeErrors,
    PartValues Results,
    PartValues? Part)
{
    public static InspectionResult From(InspectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var results = snapshot.Values
            .Where(pair => pair.Value is double or float or int or long or short or bool or string or Grade)
            .Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value is Grade grade ? grade.Name : pair.Value));

        return new InspectionResult(
            snapshot.Station,
            snapshot.Serial,
            snapshot.Time,
            ImageSaving.VerdictWord(snapshot.Verdict, snapshot.NodeErrors),
            snapshot.Verdict.Passed,
            snapshot.NodeErrors,
            new PartValues(results),
            snapshot.Part);
    }

    /// <summary>A published result by name, or else a value of the part's; null when neither has it.</summary>
    public bool TryGetValue(string name, out object? value) =>
        Results.TryGetValue(name, out value) || (Part?.TryGetValue(name, out value) ?? false);
}

/// <summary>Has a station inspect once, telling it what is known about the part.</summary>
[VisionNode(
    "Inspect",
    TypeKey = "station.inspect",
    Icon = "ph_inspect",
    Category = NodeCategory.Stations,
    Description = "Has a station inspect once when triggered, telling it about the part - what Gather put " +
                  "together - which is kept with the picture. Pass or Fail fires by the verdict; Done either way; " +
                  "Failed when the station could not inspect.")]
public sealed class InspectNode : WorkflowNode
{
    private string _station = string.Empty;
    private int _timeout = 10000;

    public InspectNode()
    {
        Trigger = AddTrigger();
        PartIn = AddInput<PartValues>("Part", isRequired: false);
        Done = AddFired("Done");
        Pass = AddFired("Pass");
        Fail = AddFired("Fail");
        Failed = AddFired("Failed");
        VerdictOut = AddOutput<string>("Verdict");
        PassedOut = AddOutput<bool>("Passed");
        ResultOut = AddOutput<InspectionResult>("Result");
    }

    public InputPort Trigger { get; }

    public InputPort PartIn { get; }

    /// <summary>The station has inspected, whatever it found.</summary>
    public OutputPort Done { get; }

    public OutputPort Pass { get; }

    public OutputPort Fail { get; }

    /// <summary>The station could not inspect: busy, faulted, or too slow.</summary>
    public OutputPort Failed { get; }

    /// <summary>PASS, FAIL, ERROR or NONE.</summary>
    public OutputPort VerdictOut { get; }

    public OutputPort PassedOut { get; }

    public OutputPort ResultOut { get; }

    public override OutputPort? FailedOutput => Failed;

    [Param("Station", Description = "Which station inspects, by name.")]
    public string Station
    {
        get => _station;
        set => SetParam(ref _station, value?.Trim() ?? string.Empty);
    }

    [Param("Timeout (ms)", Min = 100, Max = 600000, Description = "How long the inspection may take before Failed.")]
    public int Timeout
    {
        get => _timeout;
        set => SetParam(ref _timeout, Math.Clamp(value, 100, 600000));
    }

    public override TimeSpan? StepTimeout => TimeSpan.FromMilliseconds(Timeout);

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Station.Length == 0)
            yield return "No station is chosen.";
        else if (!check.HasStation(Station))
            yield return $"There is no station called '{Station}'.";
    }

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        if (step.Stations is not { } stations)
            throw new WorkflowException("This controller runs without the project's stations.");

        var part = step.Get(PartIn) as PartValues;

        var snapshot = await stations.InspectAsync(Station, part, step.CancellationToken).ConfigureAwait(false)
                       ?? throw new WorkflowException($"{Station} did not inspect: it is busy or faulted.");

        var result = InspectionResult.From(snapshot);

        step.Set(VerdictOut, result.Verdict);
        step.Set(PassedOut, result.Passed == true);
        step.Set(ResultOut, result);
        step.Fire(Done);

        if (result.Passed == true)
            step.Fire(Pass);
        else if (result.Passed == false)
            step.Fire(Fail);

        step.Log(part is { Count: > 0 }
            ? $"{Station} inspection {result.Serial}: {result.Verdict} ({part})."
            : $"{Station} inspection {result.Serial}: {result.Verdict}.");
    }
}

/// <summary>Starts a pass whenever a station has inspected - however it was started.</summary>
[VisionNode(
    "Station Result",
    TypeKey = "station.result",
    Icon = "ph_station_result",
    Category = NodeCategory.Stations,
    Description = "Starts the workflow whenever a station has inspected - by a PLC, a button or running - " +
                  "with how it went, so the result can be written to the PLC.")]
public sealed class StationResultNode : WorkflowEventNode
{
    private string _station = string.Empty;

    public StationResultNode()
    {
        Finished = AddFired("Finished");
        Pass = AddFired("Pass");
        Fail = AddFired("Fail");
        VerdictOut = AddOutput<string>("Verdict");
        PassedOut = AddOutput<bool>("Passed");
        ResultOut = AddOutput<InspectionResult>("Result");
    }

    public OutputPort Finished { get; }

    public OutputPort Pass { get; }

    public OutputPort Fail { get; }

    public OutputPort VerdictOut { get; }

    public OutputPort PassedOut { get; }

    public OutputPort ResultOut { get; }

    [Param("Station", Description = "Which station to hear from, by name.")]
    public string Station
    {
        get => _station;
        set => SetParam(ref _station, value?.Trim() ?? string.Empty);
    }

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Station.Length == 0)
            yield return "No station is chosen.";
        else if (!check.HasStation(Station))
            yield return $"There is no station called '{Station}'.";
    }

    protected internal override async Task WatchAsync(WorkflowWatch watch, CancellationToken cancellationToken)
    {
        if (watch.Stations is not { } stations)
        {
            watch.Warn("This controller runs without the project's stations: nothing to hear from.");
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        // On the stations' thread: copy what is needed and return - the
        // pass runs on the controller's own worker, after any before it.
        void OnInspected(object? sender, InspectionSnapshot snapshot)
        {
            if (!string.Equals(snapshot.Station, Station, StringComparison.OrdinalIgnoreCase))
                return;

            var result = InspectionResult.From(snapshot);

            _ = watch.RaiseAsync(
                step =>
                {
                    step.Set(VerdictOut, result.Verdict);
                    step.Set(PassedOut, result.Passed == true);
                    step.Set(ResultOut, result);
                    step.Fire(Finished);

                    if (result.Passed == true)
                        step.Fire(Pass);
                    else if (result.Passed == false)
                        step.Fire(Fail);
                },
                $"{result.Station} inspection {result.Serial}: {result.Verdict}");
        }

        stations.Inspected += OnInspected;

        try
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stations.Inspected -= OnInspected;
        }
    }
}

/// <summary>One value from an inspection: a published result, or a value of its part.</summary>
[VisionNode(
    "Result Value",
    TypeKey = "station.resultValue",
    Icon = "ph_number",
    Category = NodeCategory.Stations,
    Description = "Takes one value from an inspection's result by name - a published Diameter, Count or Code, " +
                  "or a value of the part such as PalletId - to write to the PLC.")]
public sealed class ResultValueNode : WorkflowNode
{
    private string _valueName = string.Empty;

    public ResultValueNode()
    {
        ResultIn = AddInput<InspectionResult>("Result");
        ValueOut = AddOutput<object>("Value");
    }

    public InputPort ResultIn { get; }

    public OutputPort ValueOut { get; }

    [Param("Name", Description = "The name the station publishes it under, or a part value's name.")]
    public string ValueName
    {
        get => _valueName;
        set => SetParam(ref _valueName, value?.Trim() ?? string.Empty);
    }

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (ValueName.Length == 0)
            yield return "No name is given.";

        if (!ResultIn.IsConnected)
            yield return "No result is wired in.";
    }

    protected internal override Task RunAsync(WorkflowStep step)
    {
        if (step.Get(ResultIn) is not InspectionResult result)
            throw new WorkflowException("No inspection result is wired in.");

        if (!result.TryGetValue(ValueName, out var value))
            throw new WorkflowException($"{result.Station} published nothing called '{ValueName}'.");

        step.Set(ValueOut, value);
        return Task.CompletedTask;
    }
}
