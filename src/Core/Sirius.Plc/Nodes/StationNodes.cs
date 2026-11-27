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
            snapshot.Received ? ReceivedWord : ImageSaving.VerdictWord(snapshot.Verdict, snapshot.NodeErrors),
            snapshot.Verdict.Accepted,
            snapshot.NodeErrors,
            new PartValues(results),
            snapshot.Part)
        {
            SourceFailed = snapshot.SourceFailed,
            Received = snapshot.Received,
            ResultCode = snapshot.Received ? null : snapshot.Verdict.Result,
        };
    }

    /// <summary>
    /// The station's result (F33) - PASS, FAIL, REVIEW, with its code and how
    /// it counts - or null for a shot only received, or a recipe that judges
    /// nothing.
    /// </summary>
    public ResultCode? ResultCode { get; init; }

    /// <summary>The code the PLC is sent: the result's, or 0 when there is none.</summary>
    public int Code => ResultCode?.Code ?? 0;

    /// <summary>The verdict word of a shot before its set's last: taken, not judged (F26).</summary>
    public const string ReceivedWord = "RECEIVED";

    /// <summary>Whether the node taking the picture failed - the camera's fault, not the recipe's.</summary>
    public bool SourceFailed { get; init; }

    /// <summary>A shot before its set's last: the station has it, the part is not judged yet (F26).</summary>
    public bool Received { get; init; }

    /// <summary>
    /// A published result by name, or else a value of the part's - its Part
    /// ID and Shot among them - or Frame, the station's number for the
    /// picture; false when none has it.
    /// </summary>
    public bool TryGetValue(string name, out object? value)
    {
        if (Results.TryGetValue(name, out value) || (Part?.TryGetValue(name, out value) ?? false))
            return true;

        if (string.Equals(name?.Trim(), FrameName, StringComparison.OrdinalIgnoreCase))
        {
            value = Serial;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>The name a picture's number at its station is read under: its serial.</summary>
    public const string FrameName = "Frame";
}

/// <summary>Has a station inspect once, telling it what is known about the part.</summary>
[VisionNode(
    "Inspect",
    TypeKey = "station.inspect",
    Icon = "ph_inspect",
    Category = NodeCategory.Stations,
    Description = "Has a station inspect once when triggered, telling it about the part - what Gather put " +
                  "together - which is kept with the picture. Pass or Fail fires by the verdict; Done either way; " +
                  "Failed when the station could not inspect. Verdict is the station's result - PASS, FAIL, REVIEW - " +
                  "and Code the number the PLC is sent for it, a late part's or one not inspected's too, as " +
                  "Builder's Results set them. A station taking several pictures a part fires Received for each " +
                  "before the last, and Judged with the part's verdict at the last.")]
public sealed class InspectNode : WorkflowNode
{
    private string _station = string.Empty;
    private int _timeout = 10000;

    public InspectNode()
    {
        Trigger = AddTrigger();
        PartIn = AddInput<PartValues>("Part", isRequired: false);
        ShotIn = AddInput<object>("Shot", isRequired: false);
        LastIn = AddInput<object>("Last", isRequired: false);
        Done = AddFired("Done");
        Judged = AddFired("Judged");
        Received = AddFired("Received");
        Pass = AddFired("Pass");
        Fail = AddFired("Fail");
        Failed = AddFired("Failed");
        VerdictOut = AddOutput<string>("Verdict");
        CodeOut = AddOutput<int>("Code");
        PassedOut = AddOutput<bool>("Passed");
        ResultOut = AddOutput<InspectionResult>("Result");
    }

    public InputPort Trigger { get; }

    public InputPort PartIn { get; }

    /// <summary>
    /// Which picture of the part this is, from 1 (F26): what the PLC says.
    /// Unwired, the inspections of a pass are numbered in turn.
    /// </summary>
    public InputPort ShotIn { get; }

    /// <summary>
    /// Whether this is the part's last picture, when the PLC says. Unwired,
    /// the last is the station's Shots per Part.
    /// </summary>
    public InputPort LastIn { get; }

    /// <summary>The station has inspected, whatever it found - each shot of a part.</summary>
    public OutputPort Done { get; }

    /// <summary>
    /// The part has its verdict: every inspection of a station taking a
    /// picture a part, the last of a set (F26). What writes the result.
    /// </summary>
    public OutputPort Judged { get; }

    /// <summary>
    /// The station has the picture, and the part is not judged yet: a shot
    /// before its set's last (F26). Pass and Fail come with the last.
    /// </summary>
    public OutputPort Received { get; }

    public OutputPort Pass { get; }

    public OutputPort Fail { get; }

    /// <summary>The station could not inspect: busy, faulted, or too slow.</summary>
    public OutputPort Failed { get; }

    /// <summary>
    /// The station's result - PASS, FAIL, REVIEW (F33) - or ERROR, NONE, or
    /// RECEIVED, a shot before its set's last.
    /// </summary>
    public OutputPort VerdictOut { get; }

    /// <summary>
    /// The number the PLC is sent for the result (F33); 0 for none - a shot
    /// only received. Given for a part missed as well, before Failed fires.
    /// </summary>
    public OutputPort CodeOut { get; }

    /// <summary>Whether the part is accepted: by its result, so a REVIEW is not.</summary>
    public OutputPort PassedOut { get; }

    public OutputPort ResultOut { get; }

    public override OutputPort? FailedOutput => Failed;

    [Param("Station", Description = "Which station inspects, by name.")]
    public string Station
    {
        get => _station;
        set => SetParam(ref _station, value?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// The station's deadline. Named Timeout in saved workflows, as it was
    /// before it became one.
    /// </summary>
    [Param(
        "Deadline (ms)",
        Min = 100,
        Max = 600000,
        Description = "How long the station has to give its result. Past it the inspection is cancelled, the part " +
                      "is a reject counted as late, and Failed fires - on time, for the PLC.")]
    public int Timeout
    {
        get => _timeout;
        set => SetParam(ref _timeout, Math.Clamp(value, 100, 600000));
    }

    public override TimeSpan? StepTimeout => TimeSpan.FromMilliseconds(Timeout);

    public override bool Inspects => true;

    protected internal override IEnumerable<string> Check(WorkflowCheck check)
    {
        if (Station.Length == 0)
            yield return "No station is chosen.";
        else if (!check.HasStation(Station))
            yield return $"There is no station called '{Station}'.";

        // A part's shots come in passes of their own, joined by its ID.
        if ((ShotIn.Connection is not null || LastIn.Connection is not null) && PartIn.Connection is null)
            yield return "Shots of a part are joined by its Part ID: wire Part, with a Part ID gathered from the PLC.";
    }

    protected internal override async Task RunAsync(WorkflowStep step)
    {
        if (step.Stations is not { } stations)
            throw new WorkflowException("This controller runs without the project's stations.");

        var given = step.Get(PartIn) as PartValues;
        var part = step.PartFor(given, step.IsWired(ShotIn) ? step.Get(ShotIn) : null, step.IsWired(LastIn) ? step.Get(LastIn) : null, Station);

        // Waited for only until the deadline: a node still running in the
        // recipe is cancelled when it finishes, but the PLC is answered now.
        var snapshot = await stations.InspectAsync(Station, part, step.CancellationToken)
            .WaitAsync(step.CancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            step.CancellationToken.ThrowIfCancellationRequested();
            throw new WorkflowException($"{Station} did not inspect: it is busy or faulted.");
        }

        var result = InspectionResult.From(snapshot);
        step.AddShot(result);

        step.Set(VerdictOut, result.Verdict);
        step.Set(CodeOut, result.Code);
        step.Set(PassedOut, result.Passed == true);
        step.Set(ResultOut, result);
        step.Fire(Done);

        if (!result.Received)
            step.Fire(Judged);

        if (result.Received)
            step.Fire(Received);
        else if (result.Passed == true)
            step.Fire(Pass);
        else if (result.Passed == false)
            step.Fire(Fail);

        step.Log(given is { Count: > 0 }
            ? $"{Station} inspection {result.Serial}: {result.Verdict} ({given})."
            : $"{Station} inspection {result.Serial}: {result.Verdict}.");
    }

    /// <summary>
    /// A part the station missed: the result its list gives a late part, or
    /// one not inspected - FAIL, or REVIEW where the line chose - as Verdict
    /// and Code, for the PLC Write after Failed.
    /// </summary>
    protected internal override ResultCode? Missed(WorkflowStep step, bool late)
    {
        var results = step.Stations?.ResultsOf(Station) ?? ResultList.Default;
        var result = late ? results.LateResult : results.NotInspectedResult;

        step.Set(VerdictOut, result?.Name ?? (late ? "LATE" : "NOT INSPECTED"));
        step.Set(CodeOut, result?.Code ?? 0);
        step.Set(PassedOut, false);
        return result;
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
        CodeOut = AddOutput<int>("Code");
        PassedOut = AddOutput<bool>("Passed");
        ResultOut = AddOutput<InspectionResult>("Result");
    }

    public OutputPort Finished { get; }

    public OutputPort Pass { get; }

    public OutputPort Fail { get; }

    /// <summary>The station's result - PASS, FAIL, REVIEW (F33) - or ERROR or NONE.</summary>
    public OutputPort VerdictOut { get; }

    /// <summary>The number the PLC is sent for the result (F33); 0 for none.</summary>
    public OutputPort CodeOut { get; }

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

    public override bool StartsPart => true;

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
                    step.AddShot(result);
                    step.Set(VerdictOut, result.Verdict);
                    step.Set(CodeOut, result.Code);
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

        // The part's ID, when the station was not told it - a Station Result
        // pass, the inspection started by a button - is the controller's.
        if (!result.TryGetValue(ValueName, out var value))
        {
            if (PartRecord.IsPartIdName(ValueName) && step.PartId is { } id)
                value = id;
            else
                throw new WorkflowException($"{result.Station} published nothing called '{ValueName}'.");
        }

        step.Set(ValueOut, value);
        return Task.CompletedTask;
    }
}
