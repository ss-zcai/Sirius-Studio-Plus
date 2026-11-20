using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Plc;
using Sirius.Plc.Nodes;
using Sirius.Plc.Workflows;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A controller's workflow (backlog F23a): a PLC watched, read and
/// written, stations asked to inspect - against the simulated PLC, without
/// a window.
/// </summary>
public sealed class PlcWorkflowTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-plc-" + Guid.NewGuid().ToString("N"));

    public PlcWorkflowTests()
    {
        TestCatalog.Ensure();
        PlcNodeLibrary.Register();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    // ---- values and the simulated PLC --------------------------------------------------

    [Theory]
    [InlineData("true", PlcType.Bool, true)]
    [InlineData("0", PlcType.Bool, false)]
    [InlineData(1, PlcType.Bool, true)]
    [InlineData("12", PlcType.Int16, (short)12)]
    [InlineData(12.0, PlcType.Int32, 12)]
    [InlineData("3.5", PlcType.Real, 3.5f)]
    [InlineData(true, PlcType.UInt16, (ushort)1)]
    [InlineData(42, PlcType.String, "42")]
    public void Values_are_converted_to_what_an_address_keeps(object given, PlcType type, object expected) =>
        Assert.Equal(expected, PlcValues.Convert(given, type));

    [Theory]
    [InlineData("pallet", PlcType.Int16)]
    [InlineData(2.5, PlcType.Int16)]
    [InlineData(70000, PlcType.Int16)]
    [InlineData(-1, PlcType.UInt16)]
    [InlineData("maybe", PlcType.Bool)]
    public void A_value_an_address_cannot_keep_is_refused_in_words(object given, PlcType type)
    {
        var refused = Assert.Throws<PlcException>(() => PlcValues.Convert(given, type));
        Assert.Contains($"a {type} can hold", refused.Message);
    }

    [Fact]
    public async Task The_simulated_plc_keeps_values_and_can_be_unplugged()
    {
        var plc = new SimulatedPlc();
        var word = new PlcVariable("DB34.22", PlcType.Int16);

        await Assert.ThrowsAsync<PlcException>(() => plc.ReadAsync(word, default));

        await plc.ConnectAsync(default);
        Assert.Equal((short)0, await plc.ReadAsync(word, default));

        await plc.WriteAsync(word, (short)12, default);
        Assert.Equal((short)12, await plc.ReadAsync(word, default));
        Assert.Equal(12, await plc.ReadAsync(word with { Type = PlcType.Int32 }, default));
        Assert.Equal([("DB34.22", (object)(short)12)], plc.Writes);

        // An address no PLC could have.
        var refused = await Assert.ThrowsAsync<PlcException>(() => plc.ReadAsync(new PlcVariable("DB 34", PlcType.Bool), default));
        Assert.Contains("space", refused.Message);

        // The cable out: nothing answers, and connecting fails, until it is back.
        plc.Unplug();
        Assert.False(plc.IsConnected);
        await Assert.ThrowsAsync<PlcException>(() => plc.ReadAsync(word, default));
        await Assert.ThrowsAsync<PlcException>(() => plc.ConnectAsync(default));

        plc.PlugIn();
        await plc.ConnectAsync(default);
        Assert.Equal((short)12, await plc.ReadAsync(word, default));
    }

    // ---- a workflow, end to end ---------------------------------------------------------

    /// <summary>
    /// The PLC team's example: a ready bit rising at DB34.20.0; the tray
    /// number, position and run mode read; gathered as PalletId,
    /// PositionId and RunMode; station Top inspects; Capture Done -
    /// DB34.20.1 - pulsed, and the verdict written to DB34.28.
    /// </summary>
    private sealed class LineWorkflow
    {
        public LineWorkflow(string station = "Top")
        {
            Monitor = new PlcMonitorNode { Name = "Monitor Ready", Address = "DB34.20.0", PollInterval = 5 };
            Tray = new PlcReadNode { Name = "Tray Number", Address = "DB34.22", Type = PlcType.Int16 };
            Position = new PlcReadNode { Name = "Position", Address = "DB34.24", Type = PlcType.Int16 };
            Mode = new PlcReadNode { Name = "RunMode", Address = "DB34.26", Type = PlcType.Int16 };
            Gather = new GatherNode { Name1 = "PalletId", Name2 = "PositionId", Name3 = "RunMode" };
            Inspect = new InspectNode { Station = station };
            Done = new PlcPulseNode { Name = "Capture Done", Address = "DB34.20.1", Width = 10 };
            Verdict = new PlcWriteNode { Name = "Verdict", Address = "DB34.28", Type = PlcType.Int16 };
            Failed = new PlcWriteNode { Name = "Alarm", Address = "DB34.20.2", Value = "true" };

            foreach (var node in new VisionNode[] { Monitor, Tray, Position, Mode, Gather, Inspect, Done, Verdict, Failed })
                Graph.Add(node);

            Wire(Monitor.Triggered, Tray.Trigger);
            Wire(Monitor.Triggered, Position.Trigger);
            Wire(Monitor.Triggered, Mode.Trigger);
            Wire(Tray.ValueOut, Gather.Values[0]);
            Wire(Position.ValueOut, Gather.Values[1]);
            Wire(Mode.ValueOut, Gather.Values[2]);
            Wire(Gather.Gathered, Inspect.Trigger);
            Wire(Gather.PartOut, Inspect.PartIn);
            Wire(Inspect.Done, Done.Trigger);
            Wire(Inspect.PassedOut, Verdict.ValueIn);
            Wire(Inspect.Done, Verdict.Trigger);
            Wire(Inspect.Failed, Failed.Trigger);
        }

        public VisionGraph Graph { get; } = new();

        public PlcMonitorNode Monitor { get; }

        public PlcReadNode Tray { get; }

        public PlcReadNode Position { get; }

        public PlcReadNode Mode { get; }

        public GatherNode Gather { get; }

        public InspectNode Inspect { get; }

        public PlcPulseNode Done { get; }

        public PlcWriteNode Verdict { get; }

        public PlcWriteNode Failed { get; }

        public void Wire(OutputPort from, InputPort to) => Assert.True(Graph.TryConnect(from, to).Success, $"{from.Name} -> {to.Name}");
    }

    /// <summary>Stations that answer as a test says, and remember what they were asked.</summary>
    private sealed class FakeStations : IStationBus
    {
        private long _serial;

        public List<(string Station, PartValues? Part)> Asked { get; } = [];

        public bool? Passes { get; set; } = true;

        public TimeSpan Takes { get; set; }

        /// <summary>Busy or faulted: inspects nothing.</summary>
        public bool Busy { get; set; }

        /// <summary>How each station says it is.</summary>
        public StationState State { get; set; } = StationState.Ready;

        /// <summary>The source - the camera - fails, with a node error.</summary>
        public bool CameraFails { get; set; }

        /// <summary>A node of the recipe fails.</summary>
        public bool RecipeFails { get; set; }

        public StationState? StateOf(string station) => State;

        /// <summary>Whether the last inspection asked for was cancelled while it ran.</summary>
        public bool WasCancelled { get; private set; }

        public IReadOnlyList<string> Stations => ["Top"];

        public event EventHandler<InspectionSnapshot>? Inspected;

        public async Task<InspectionSnapshot?> InspectAsync(string station, PartValues? part, CancellationToken cancellationToken)
        {
            lock (Asked)
                Asked.Add((station, part));

            if (Busy)
                return null;

            if (Takes > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(Takes, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    return null;
                }
            }

            var snapshot = Snapshot(station, Passes, Interlocked.Increment(ref _serial)) with
            {
                Part = part,
                NodeErrors = CameraFails || RecipeFails ? 1 : 0,
                SourceFailed = CameraFails,
            };
            Inspected?.Invoke(this, snapshot);
            return snapshot;
        }

        /// <summary>A station finishing an inspection something else started.</summary>
        public void Finish(InspectionSnapshot snapshot) => Inspected?.Invoke(this, snapshot);
    }

    private static InspectionSnapshot Snapshot(string station, bool? passed, long serial, params (string Name, object? Value)[] values) =>
        new(
            station,
            serial,
            DateTime.Now,
            new Verdict(passed, null, 1, passed == false ? 1 : 0),
            TimeSpan.FromMilliseconds(5),
            0,
            values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>Starts the runner and waits until it is connected and its Monitors have read once.</summary>
    private static async Task StartAsync(WorkflowRunner runner, SimulatedPlc plc)
    {
        runner.Start();

        var reads = plc.Reads;
        await Until(() => runner.IsConnected && plc.Reads > reads + 1);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Waited too long.");
            await Task.Delay(5);
        }
    }

    /// <summary>Does something, and waits for the pass it starts to finish.</summary>
    private static async Task<WorkflowPassResult> PassAfter(WorkflowRunner runner, Action act)
    {
        var finished = new TaskCompletionSource<WorkflowPassResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(object? sender, WorkflowPassResult result) => finished.TrySetResult(result);

        runner.PassFinished += OnFinished;

        try
        {
            act();
            return await finished.Task.WaitAsync(Patience);
        }
        finally
        {
            runner.PassFinished -= OnFinished;
        }
    }

    private static SimulatedPlc Line()
    {
        var plc = new SimulatedPlc();
        plc.Set("DB34.22", (short)12);
        plc.Set("DB34.24", (short)3);
        plc.Set("DB34.26", (short)1);
        return plc;
    }

    [Fact]
    public async Task A_rising_bit_reads_the_part_has_it_inspected_and_answers_the_plc()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = new LineWorkflow();
        await using var runner = new WorkflowRunner(line.Graph, plc, stations, "Line 1 PLC");

        Assert.Empty(runner.Check());
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        // The part as the PLC described it, to the station.
        var (station, part) = Assert.Single(stations.Asked);
        Assert.Equal("Top", station);
        Assert.Equal([("PalletId", (object?)12L), ("PositionId", 3L), ("RunMode", 1L), (PartRecord.PartIdName, "1"), (PartRecord.ShotName, 1L)], part!.Select(pair => (pair.Key, pair.Value)));

        // Capture Done pulsed, the verdict written; no alarm.
        Assert.Equal([("DB34.20.1", (object)true), ("DB34.20.1", false), ("DB34.28", (short)1)], plc.Writes);
        Assert.False((bool)plc.Get("DB34.20.2"));

        Assert.Equal(1, pass.Number);
        Assert.Equal("Monitor Ready", pass.Start);
        Assert.Empty(pass.Failed);
        Assert.Equal(["Monitor Ready", "Tray Number", "Position", "RunMode", "Gather", "Inspect", "Capture Done", "Verdict"], pass.Ran);
        Assert.Equal("Monitor Ready: rising edge on DB34.20.0 (event 1)", runner.LastEvent);
        Assert.Contains(runner.Log, entry => entry.Message.StartsWith("Connected to", StringComparison.Ordinal));

        // Held high, no second pass; dropped and raised again, a second.
        await Task.Delay(50);
        Assert.Equal(1, runner.Passes);

        plc.Set("DB34.20.0", false);
        var reads = plc.Reads;
        await Until(() => plc.Reads > reads + 1);

        var second = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
        Assert.Equal(2, second.Number);
        Assert.Equal(2, stations.Asked.Count);

        await runner.StopAsync();
        Assert.Equal(ControllerState.Stopped, runner.State);
        Assert.False(plc.IsConnected);
    }

    [Fact]
    public async Task A_failed_inspection_takes_the_fail_branch_only()
    {
        var plc = Line();
        var stations = new FakeStations { Passes = false };
        var graph = new VisionGraph();
        var monitor = new PlcMonitorNode { Address = "M0.0", PollInterval = 5 };
        var inspect = new InspectNode { Station = "Top" };
        var good = new PlcWriteNode { Name = "Good", Address = "M1.0" };
        var bad = new PlcWriteNode { Name = "Bad", Address = "M1.1" };

        foreach (var node in new VisionNode[] { monitor, inspect, good, bad })
            graph.Add(node);

        graph.TryConnect(monitor.Triggered, inspect.Trigger);
        graph.TryConnect(inspect.Pass, good.Trigger);
        graph.TryConnect(inspect.Fail, bad.Trigger);

        await using var runner = new WorkflowRunner(graph, plc, stations);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("M0.0", true));

        Assert.Equal([("M1.1", (object)true)], plc.Writes);
        Assert.DoesNotContain("Good", pass.Ran);
        Assert.Contains("Bad", pass.Ran);

        // Asked for nothing about the part: none is gathered, and none goes with it.
        // Nothing gathered: told only which part, and which shot.
        Assert.Equal([(PartRecord.PartIdName, (object?)"1"), (PartRecord.ShotName, 1L)], Assert.Single(stations.Asked).Part!.Select(pair => (pair.Key, pair.Value)));
    }

    [Fact]
    public async Task A_read_that_fails_fires_failed_and_what_needs_its_value_does_not_run()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = new LineWorkflow();
        line.Position.Address = "DB 34.24";

        var alarm = new PlcWriteNode { Name = "Read Alarm", Address = "DB34.20.3" };
        line.Graph.Add(alarm);
        line.Wire(line.Position.Failed, alarm.Trigger);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);

        // Said before it runs.
        Assert.Contains("Position: 'DB 34.24' has a space in it.", runner.Check());

        await StartAsync(runner, plc);
        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Equal(["Position"], pass.Failed);
        Assert.Contains("Read Alarm", pass.Ran);
        Assert.DoesNotContain("Gather", pass.Ran);
        Assert.Empty(stations.Asked);
        Assert.True((bool)plc.Get("DB34.20.3"));

        Assert.Equal("Position: 'DB 34.24' has a space in it.", runner.LastError);
        Assert.Contains(runner.Log, entry => entry is { Level: WorkflowLogLevel.Error, Node: "Position" });
    }

    [Fact]
    public async Task A_station_slower_than_its_timeout_fails_the_step_and_fires_failed()
    {
        var plc = Line();
        var stations = new FakeStations { Takes = TimeSpan.FromSeconds(5) };
        var line = new LineWorkflow();
        line.Inspect.Timeout = 100;

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Equal(["Inspect"], pass.Failed);
        Assert.Contains("Alarm", pass.Ran);
        Assert.DoesNotContain("Capture Done", pass.Ran);
        Assert.True((bool)plc.Get("DB34.20.2"));
        Assert.Equal("Inspect: No answer within 0.1 s.", runner.LastError);
    }

    [Fact]
    public async Task Wait_for_carries_on_when_the_value_comes_or_says_it_timed_out()
    {
        var plc = Line();
        var graph = new VisionGraph();
        var monitor = new PlcMonitorNode { Address = "M0.0", PollInterval = 5 };
        var wait = new PlcWaitForNode { Address = "M0.1", Value = "true", Timeout = 300, PollInterval = 5 };
        var acknowledged = new PlcWriteNode { Name = "Acknowledged", Address = "M2.0" };
        var late = new PlcWriteNode { Name = "Late", Address = "M2.1" };

        foreach (var node in new VisionNode[] { monitor, wait, acknowledged, late })
            graph.Add(node);

        graph.TryConnect(monitor.Triggered, wait.Trigger);
        graph.TryConnect(wait.Done, acknowledged.Trigger);
        graph.TryConnect(wait.TimedOut, late.Trigger);

        await using var runner = new WorkflowRunner(graph, plc);
        await StartAsync(runner, plc);

        // Nobody answers: Timed Out.
        var first = await PassAfter(runner, () => plc.Set("M0.0", true));
        Assert.Contains("Late", first.Ran);
        Assert.Empty(first.Failed);

        plc.Set("M0.0", false);

        // Until the Monitor has read it off - however busy the machine.
        var readsSince = plc.Reads;
        await Until(() => plc.Reads > readsSince + 1);

        // The PLC answers part way through the wait.
        var second = await PassAfter(runner, () =>
        {
            plc.Set("M0.0", true);
            _ = Task.Delay(60).ContinueWith(_ => plc.Set("M0.1", true), TaskScheduler.Default);
        });

        Assert.Contains("Acknowledged", second.Ran);
        Assert.DoesNotContain("Late", second.Ran);
    }

    [Fact]
    public async Task Station_results_are_written_to_the_plc_one_pass_at_a_time_in_order()
    {
        var plc = Line();
        var stations = new FakeStations();
        var graph = new VisionGraph();
        var heard = new StationResultNode { Station = "top" };
        var diameter = new ResultValueNode { ValueName = "Diameter" };
        var write = new PlcWriteNode { Name = "Diameter", Address = "DB40.0", Type = PlcType.Real };

        foreach (var node in new VisionNode[] { heard, diameter, write })
            graph.Add(node);

        graph.TryConnect(heard.ResultOut, diameter.ResultIn);
        graph.TryConnect(diameter.ValueOut, write.ValueIn);
        graph.TryConnect(heard.Finished, write.Trigger);

        await using var runner = new WorkflowRunner(graph, plc, stations);
        Assert.Empty(runner.Check());
        runner.Start();
        await Until(() => runner.IsConnected);

        var passes = new List<WorkflowPassResult>();
        runner.PassFinished += (_, result) =>
        {
            lock (passes)
                passes.Add(result);
        };

        // Three inspections in quick succession, and one of another station's.
        stations.Finish(Snapshot("Top", true, 1, ("Diameter", 12.5)));
        stations.Finish(Snapshot("Side", true, 1, ("Diameter", 99.0)));
        stations.Finish(Snapshot("Top", false, 2, ("Diameter", 13.25)));
        stations.Finish(Snapshot("Top", true, 3, ("Diameter", 12.75)));

        await Until(() =>
        {
            lock (passes)
                return passes.Count == 3;
        });

        Assert.Equal([12.5f, 13.25f, 12.75f], plc.Writes.Select(written => (float)written.Value));
        Assert.Equal([1, 2, 3], passes.Select(pass => pass.Number));
        Assert.Equal("Station Result: Top inspection 3: PASS", runner.LastEvent);

        // A name the station never published fails the step, saying which.
        diameter.ValueName = "Width";
        var missing = await PassAfter(runner, () => stations.Finish(Snapshot("Top", true, 4, ("Diameter", 1.0))));
        Assert.Equal(["Result Value"], missing.Failed);
        Assert.Equal("Result Value: Top published nothing called 'Width'.", runner.LastError);
    }

    [Fact]
    public async Task A_lost_plc_is_connected_again_and_watched_afresh()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = new LineWorkflow();

        await using var runner = new WorkflowRunner(line.Graph, plc, stations) { ReconnectInterval = TimeSpan.FromMilliseconds(20) };
        await StartAsync(runner, plc);

        plc.Unplug();
        await Until(() => runner.Log.Any(entry => entry.Message.StartsWith("Lost the connection", StringComparison.Ordinal)));
        await Until(() => runner.Log.Any(entry => entry.Message.StartsWith("Cannot connect", StringComparison.Ordinal)));

        // Tried again and again, said once.
        await Task.Delay(100);
        Assert.Single(runner.Log, entry => entry.Message.StartsWith("Cannot connect", StringComparison.Ordinal));

        // The bit rose while the cable was out: no edge for that.
        plc.Set("DB34.20.0", true);
        plc.PlugIn();
        await Until(() => runner.IsConnected);
        var reads = plc.Reads;
        await Until(() => plc.Reads > reads + 1);
        Assert.Equal(0, runner.Passes);

        plc.Set("DB34.20.0", false);

        // Until the Monitor has read it off - however busy the machine.
        var readsSince = plc.Reads;
        await Until(() => plc.Reads > readsSince + 1);
        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
        Assert.Equal(1, pass.Number);
        Assert.Equal(2, runner.Log.Count(entry => entry.Message.StartsWith("Connected to", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A read that fails - how a real driver first finds a connection gone,
    /// before it says it is not connected - is a gap in the watching too: a
    /// bit that rose meanwhile is no edge. The Monitor kept the value from
    /// before the failure, and fired on the first read after it; the lost-PLC
    /// test above failed now and then for it, when the cable was pulled
    /// between the Monitor's look at the connection and its read.
    /// </summary>
    [Fact]
    public async Task A_bit_that_rose_while_reads_failed_is_no_edge()
    {
        var plc = Line();
        var driver = new FailingReads(plc);
        var line = new LineWorkflow();

        await using var runner = new WorkflowRunner(line.Graph, driver, new FakeStations());
        await StartAsync(runner, plc);

        // Reads fail, still connected; the bit rises meanwhile.
        driver.Failing = true;
        var failed = driver.Failed;
        await Until(() => driver.Failed > failed);

        plc.Set("DB34.20.0", true);
        driver.Failing = false;

        // Read again, after the Monitor's wait - and nothing fired.
        var reads = plc.Reads;
        await Until(() => plc.Reads > reads + 1);
        Assert.Equal(0, runner.Passes);

        // A rise it saw both sides of still fires.
        plc.Set("DB34.20.0", false);
        var readsSince = plc.Reads;
        await Until(() => plc.Reads > readsSince + 1);
        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
        Assert.Equal(1, pass.Number);
    }

    /// <summary>The simulated PLC, whose reads can be made to fail while it says it is connected.</summary>
    private sealed class FailingReads(SimulatedPlc plc) : IPlcDriver
    {
        private int _failed;

        public volatile bool Failing;

        /// <summary>Reads failed so far.</summary>
        public int Failed => Volatile.Read(ref _failed);

        public string Description => plc.Description;

        public bool IsConnected => plc.IsConnected;

        public Task ConnectAsync(CancellationToken cancellationToken) => plc.ConnectAsync(cancellationToken);

        public Task DisconnectAsync() => plc.DisconnectAsync();

        public string? AddressProblem(PlcVariable variable) => plc.AddressProblem(variable);

        public Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken)
        {
            if (!Failing)
                return plc.ReadAsync(variable, cancellationToken);

            Interlocked.Increment(ref _failed);
            throw new PlcException("No answer in time.");
        }

        public Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken) =>
            plc.WriteAsync(variable, value, cancellationToken);

        public ValueTask DisposeAsync() => plc.DisposeAsync();
    }

    // ---- parts (F25) ---------------------------------------------------------------------

    /// <summary>
    /// A pass that inspects is a part: numbered by the controller, its ID
    /// told to the station with which shot it is - kept in the picture's
    /// .sann - and counted.
    /// </summary>
    [Fact]
    public async Task A_pass_that_inspects_is_a_part_numbered_and_counted()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = new LineWorkflow();

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var first = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
        var part = Assert.IsType<PartRecord>(first.Part);

        Assert.Equal(1, part.Number);
        Assert.Equal("1", part.Id);
        Assert.Equal(PartVerdict.Passed, part.Verdict);
        Assert.Equal("PASS", part.VerdictWord);
        var shot = Assert.Single(part.Shots);
        Assert.Equal("Top", shot.Station);

        // The station is told the part's ID and the shot, beside what Gather read.
        var told = stations.Asked.Single().Part!;
        Assert.Equal(12L, told["PalletId"]);
        Assert.Equal("1", told[PartRecord.PartIdName]);
        Assert.Equal(1L, told[PartRecord.ShotName]);

        // The next part, failed.
        stations.Passes = false;
        plc.Set("DB34.20.0", false);
        var readsSince = plc.Reads;
        await Until(() => plc.Reads > readsSince + 1);
        var second = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Equal(2, second.Part!.Number);
        Assert.Equal(PartVerdict.Failed, second.Part.Verdict);
        Assert.Equal(new PartCounts(2, 1, 1, 0, 0), runner.PartCounts);
        Assert.Same(second.Part, runner.LastPart);
        Assert.Equal(50, runner.PartCounts.Yield);
    }

    /// <summary>A value Gather read called Part ID is the part's ID, however it is written.</summary>
    [Theory]
    [InlineData("Part ID")]
    [InlineData("PartId")]
    [InlineData("part_id")]
    public async Task A_part_id_read_from_the_plc_is_the_parts_id(string name)
    {
        var plc = Line();
        plc.Set("DB34.30", (short)1043);
        var stations = new FakeStations();
        var line = new LineWorkflow();

        var id = new PlcReadNode { Name = "Part ID", Address = "DB34.30", Type = PlcType.Int16 };
        line.Graph.Add(id);
        line.Wire(line.Monitor.Triggered, id.Trigger);
        line.Gather.Name4 = name;
        line.Wire(id.ValueOut, line.Gather.Values[3]);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Equal("1043", pass.Part!.Id);
        Assert.Equal(1, pass.Part.Number);

        // Told as read, not again under another name.
        var told = stations.Asked.Single().Part!;
        Assert.Equal(1043L, told[name]);
        Assert.Equal(5, told.Count);
    }

    /// <summary>
    /// A station that gives no result by its deadline: the part is a reject,
    /// counted as late, the PLC answered on time - Failed fires - and the
    /// inspection is cancelled.
    /// </summary>
    [Fact]
    public async Task A_station_past_its_deadline_makes_the_part_late_and_is_cancelled()
    {
        var plc = Line();
        var stations = new FakeStations { Takes = TimeSpan.FromSeconds(5) };
        var line = new LineWorkflow();
        line.Inspect.Timeout = 150;

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"answered after {clock.Elapsed}");
        Assert.Equal(PartVerdict.Late, pass.Part!.Verdict);
        Assert.Equal("LATE", pass.Part.VerdictWord);
        Assert.Contains("no result within 150 ms", pass.Part.Problem);
        Assert.Empty(pass.Part.Shots);
        Assert.Contains("Inspect", pass.Failed);
        Assert.Contains(plc.Writes, written => written.Address == "DB34.20.2");
        Assert.Equal(new PartCounts(1, 0, 0, 1, 0), runner.PartCounts);

        await Until(() => stations.WasCancelled);
    }

    /// <summary>A station busy or faulted: the part is not inspected, a reject.</summary>
    [Fact]
    public async Task A_station_that_cannot_inspect_leaves_the_part_not_inspected()
    {
        var plc = Line();
        var stations = new FakeStations { Busy = true };
        var line = new LineWorkflow();

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Equal(PartVerdict.NotInspected, pass.Part!.Verdict);
        Assert.Equal("NOT INSPECTED", pass.Part.VerdictWord);
        Assert.Contains("busy or faulted", pass.Part.Problem);
        Assert.Equal(new PartCounts(1, 0, 0, 0, 1), runner.PartCounts);
        Assert.Equal(1, runner.PartCounts.Rejected);
    }

    /// <summary>A pass that inspects nothing - a heartbeat, a reset - is not a part.</summary>
    [Fact]
    public async Task A_pass_that_inspects_nothing_is_not_a_part()
    {
        var plc = Line();
        var graph = new VisionGraph();
        var monitor = new PlcMonitorNode { Name = "Reset", Address = "M0.0", PollInterval = 5 };
        var write = new PlcWriteNode { Name = "Ack", Address = "M0.1", Value = "true" };
        graph.Add(monitor);
        graph.Add(write);
        Assert.True(graph.TryConnect(monitor.Triggered, write.Trigger).Success);

        await using var runner = new WorkflowRunner(graph, plc);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("M0.0", true));

        Assert.Null(pass.Part);
        Assert.Equal(PartCounts.Zero, runner.PartCounts);
    }

    /// <summary>
    /// Part counts, and the next part's number, are kept beside the
    /// workflow and carried on from after a restart (F4); a reset starts
    /// the counts again but never a number given before.
    /// </summary>
    [Fact]
    public async Task Part_counts_and_numbers_carry_on_after_a_restart()
    {
        var path = PartCountsFile.PathFor(Path.Combine(_folder, "workflows", "Line 1 PLC" + WorkflowFile.Extension));
        Assert.EndsWith("Line 1 PLC.counts.json", path);

        var plc = Line();
        var stations = new FakeStations();

        await using (var runner = new WorkflowRunner(new LineWorkflow().Graph, plc, stations))
        {
            runner.KeepCountsIn(path);
            await StartAsync(runner, plc);
            await PassAfter(runner, () => plc.Set("DB34.20.0", true));
            Assert.Equal(1, runner.PartCounts.Total);
        }

        Assert.True(File.Exists(path));

        var again = new SimulatedPlc();
        again.Set("DB34.22", (short)12);

        await using (var runner = new WorkflowRunner(new LineWorkflow().Graph, again, stations))
        {
            runner.KeepCountsIn(path);
            Assert.Equal(new PartCounts(1, 1, 0, 0, 0), runner.PartCounts);

            await StartAsync(runner, again);
            var pass = await PassAfter(runner, () => again.Set("DB34.20.0", true));
            Assert.Equal(2, pass.Part!.Number);
            Assert.Equal(2, runner.PartCounts.Total);

            runner.ResetPartCounts();
            Assert.Equal(PartCounts.Zero, runner.PartCounts);
        }

        var (counts, next) = PartCountsFile.Load(path);
        Assert.Equal(PartCounts.Zero, counts);
        Assert.Equal(3, next);

        // Unreadable: started again, not a failure.
        File.WriteAllText(path, "{ not json");
        Assert.Equal((PartCounts.Zero, 1L), PartCountsFile.Load(path));
    }

    /// <summary>The part's ID and the picture's frame number, written to the PLC by Result Value.</summary>
    [Fact]
    public async Task Result_value_gives_the_part_id_and_the_frame()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = new LineWorkflow();

        var id = new ResultValueNode { Name = "Id", ValueName = "Part ID" };
        var frame = new ResultValueNode { Name = "Frame", ValueName = "Frame" };
        var writeId = new PlcWriteNode { Name = "Write Id", Address = "DB34.40", Type = PlcType.Int16 };
        var writeFrame = new PlcWriteNode { Name = "Write Frame", Address = "DB34.42", Type = PlcType.Int32 };

        foreach (var node in new VisionNode[] { id, frame, writeId, writeFrame })
            line.Graph.Add(node);

        line.Wire(line.Inspect.ResultOut, id.ResultIn);
        line.Wire(line.Inspect.ResultOut, frame.ResultIn);
        line.Wire(id.ValueOut, writeId.ValueIn);
        line.Wire(frame.ValueOut, writeFrame.ValueIn);
        line.Wire(line.Inspect.Done, writeId.Trigger);
        line.Wire(line.Inspect.Done, writeFrame.Trigger);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));

        Assert.Empty(pass.Failed);
        Assert.Contains(plc.Writes, written => written.Address == "DB34.40" && Equals(written.Value, (short)1));
        Assert.Contains(plc.Writes, written => written.Address == "DB34.42" && Equals(written.Value, 1));
    }

    // ---- the handshake (F25) -------------------------------------------------------------

    private static LineWorkflow WithHandshake(out HandshakeNode handshake)
    {
        var line = new LineWorkflow();
        handshake = new HandshakeNode { ReadyAddress = "DB34.20.3", BusyAddress = "DB34.20.4", ErrorAddress = "DB34.32", PollInterval = 5 };
        line.Graph.Add(handshake);
        return line;
    }

    /// <summary>
    /// Ready while the stations are ready and no part is in hand; Busy from
    /// a part's trigger to its answer; the error code the part left - each
    /// written as it changes, with nothing wired.
    /// </summary>
    [Fact]
    public async Task The_handshake_tells_the_plc_ready_busy_and_the_parts_error_code()
    {
        var plc = Line();
        var stations = new FakeStations { Takes = TimeSpan.FromMilliseconds(300) };
        var line = WithHandshake(out _);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        Assert.Empty(runner.Check());
        await StartAsync(runner, plc);

        await Until(() => Equals(plc.Get("DB34.20.3"), true));
        Assert.Equal(false, plc.Get("DB34.20.4"));
        Assert.Equal((short)0, plc.Get("DB34.32", PlcType.Int16));

        // A part in hand: Busy, and not Ready.
        var pass = PassAfter(runner, () => plc.Set("DB34.20.0", true));
        await Until(() => Equals(plc.Get("DB34.20.4"), true));
        Assert.Equal(false, plc.Get("DB34.20.3"));

        await pass;
        await Until(() => Equals(plc.Get("DB34.20.4"), false) && Equals(plc.Get("DB34.20.3"), true));

        // Busy came before the answer was written, and went after it.
        var writes = plc.Writes.Select(written => written.Address).ToList();
        Assert.True(
            writes.IndexOf("DB34.20.4") < writes.IndexOf("DB34.28") && writes.LastIndexOf("DB34.20.4") > writes.LastIndexOf("DB34.28"),
            string.Join(", ", writes));

        // The next part late: error code 2.
        line.Inspect.Timeout = 100;
        plc.Set("DB34.20.0", false);
        var readsSince = plc.Reads;
        await Until(() => plc.Reads > readsSince + 1);
        stations.Takes = TimeSpan.FromSeconds(5);

        await PassAfter(runner, () => plc.Set("DB34.20.0", true));
        await Until(() => Equals(plc.Get("DB34.32", PlcType.Int16), HandshakeCodes.Late));
    }

    /// <summary>A station faulted: not Ready, and error code 1 until it is back.</summary>
    [Fact]
    public async Task A_faulted_station_takes_ready_away_and_says_so_in_the_code()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = WithHandshake(out _);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);
        await Until(() => Equals(plc.Get("DB34.20.3"), true));

        stations.State = StationState.Faulted;
        await Until(() => Equals(plc.Get("DB34.20.3"), false) && Equals(plc.Get("DB34.32", PlcType.Int16), HandshakeCodes.StationFault));

        stations.State = StationState.Ready;
        await Until(() => Equals(plc.Get("DB34.20.3"), true) && Equals(plc.Get("DB34.32", PlcType.Int16), HandshakeCodes.Ok));
    }

    /// <summary>The code a part leaves: late, not inspected, the camera, the recipe - or nothing wrong, pass or fail.</summary>
    [Fact]
    public async Task A_parts_error_code_says_what_went_wrong()
    {
        var plc = Line();
        var stations = new FakeStations();
        var line = WithHandshake(out _);

        await using var runner = new WorkflowRunner(line.Graph, plc, stations);
        await StartAsync(runner, plc);

        async Task<short> CodeAfterAPart()
        {
            plc.Set("DB34.20.0", false);
            var readsSince = plc.Reads;
            await Until(() => plc.Reads > readsSince + 1);

            var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
            return HandshakeCodes.For(pass.Part!);
        }

        stations.Passes = false;
        Assert.Equal(HandshakeCodes.Ok, await CodeAfterAPart());

        stations.CameraFails = true;
        Assert.Equal(HandshakeCodes.Camera, await CodeAfterAPart());
        await Until(() => Equals(plc.Get("DB34.32", PlcType.Int16), HandshakeCodes.Camera));

        stations.CameraFails = false;
        stations.RecipeFails = true;
        Assert.Equal(HandshakeCodes.Recipe, await CodeAfterAPart());

        stations.RecipeFails = false;
        stations.Busy = true;
        Assert.Equal(HandshakeCodes.StationFault, await CodeAfterAPart());
    }

    [Fact]
    public void A_handshake_with_no_address_says_so()
    {
        var graph = new VisionGraph();
        graph.Add(new PlcMonitorNode { Name = "Ready", Address = "M0.0" });
        graph.Add(new HandshakeNode { Name = "Handshake" });

        var runner = new WorkflowRunner(graph, new SimulatedPlc(), new FakeStations());

        Assert.Contains("Handshake: No address is given: Ready, Busy or Error Code.", runner.Check());
    }

    [Fact]
    public void A_check_names_what_is_wrong_before_anything_runs()
    {
        var graph = new VisionGraph();
        var read = new PlcReadNode { Name = "Tray" };
        var gather = new GatherNode { Name1 = "PalletId", Name2 = "palletid" };
        var inspect = new InspectNode { Station = "Bottom" };
        var threshold = new ThresholdNode();

        foreach (var node in new VisionNode[] { read, gather, inspect, threshold })
            graph.Add(node);

        graph.TryConnect(read.ValueOut, gather.Values[0]);
        graph.TryConnect(read.ValueOut, gather.Values[1]);

        var runner = new WorkflowRunner(graph, new SimulatedPlc(), new FakeStations());

        Assert.Equal(
            [
                "Tray: No address is given.",
                "Gather: Two values are called 'PalletId'.",
                "Inspect: There is no station called 'Bottom'.",
                $"'{threshold.Name}' is a recipe's node: a workflow cannot run it.",
                "Nothing starts this workflow: it needs a Monitor or a Station Result.",
            ],
            runner.Check());

        // A recipe's node will not run in a workflow, nor a workflow's in a recipe.
        Assert.Throws<InvalidOperationException>(runner.Start);
        Assert.True(NodeCatalog.Describe(typeof(InspectNode)).Category.IsWorkflow());
        Assert.False(NodeCatalog.Describe(typeof(ThresholdNode)).Category.IsWorkflow());
    }

    [Fact]
    public void A_workflow_is_saved_and_read_back_as_it_was()
    {
        var line = new LineWorkflow();
        line.Done.Width = 300;
        var path = Path.Combine(_folder, "Line 1 PLC" + WorkflowFile.Extension);

        WorkflowFile.Save(line.Graph, path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"format\": \"sirius-workflow\"", text);
        Assert.Contains("\"name\": \"Line 1 PLC\"", text);

        var loaded = WorkflowFile.Load(path);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(line.Graph.Nodes.Select(node => node.Name), loaded.Graph.Nodes.Select(node => node.Name));
        Assert.Equal(line.Graph.Connections.Count, loaded.Graph.Connections.Count);

        var gather = loaded.Graph.Nodes.OfType<GatherNode>().Single();
        Assert.Equal(["PalletId", "PositionId", "RunMode"], Enumerable.Range(0, 3).Select(gather.NameOf));
        Assert.Equal(300, loaded.Graph.Nodes.OfType<PlcPulseNode>().Single().Width);
        Assert.Equal(PlcType.Int16, loaded.Graph.Nodes.OfType<PlcReadNode>().First().Type);

        // Read back, it runs.
        Assert.Empty(new WorkflowRunner(loaded.Graph, new SimulatedPlc(), new FakeStations()).Check());

        // A recipe is not a workflow; a newer workflow is not read.
        Assert.Contains("not a Sirius workflow", Assert.Throws<InvalidDataException>(() => WorkflowFile.Deserialize("{\"format\": \"sirius-annotations\"}")).Message);
        Assert.Contains("newer Sirius", Assert.Throws<InvalidDataException>(() => WorkflowFile.Deserialize("{\"format\": \"sirius-workflow\", \"version\": 9}")).Message);
    }

    [Fact]
    public void Part_values_keep_their_order_and_plain_values_and_ignore_case()
    {
        var part = new PartValues([new("PalletId", (short)12), new("Ratio", 0.5f), new("palletid", 13), new(" ", 1), new("Mode", PlcType.Real)]);

        Assert.Equal(["PalletId", "Ratio", "Mode"], part.Names);
        Assert.Equal(13L, part["PALLETID"]);
        Assert.Equal(0.5, part["Ratio"]);
        Assert.Equal("Real", part["Mode"]);
        Assert.Equal("PalletId 13, Ratio 0.5, Mode Real", part.ToString());
    }

    // ---- with a real station -------------------------------------------------------------

    /// <summary>
    /// A project whose station Top inspects a folder - dark, bright, dark -
    /// passing up to 128, and keeps every picture with its annotations.
    /// </summary>
    private Project StationProject()
    {
        var images = Path.Combine(_folder, "source");
        Directory.CreateDirectory(images);

        foreach (var (name, value) in new[] { ("1.png", 50), ("2.png", 200), ("3.png", 60) })
        {
            using var mat = new Mat(30, 40, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(value));
            CvInvoke.Imwrite(Path.Combine(images, name), mat);
        }

        var recipe = new VisionGraph();
        var sequence = new ImageSequenceNode { SourcePath = images };
        var stats = new RegionStatsNode();
        var check = new LimitCheckNode { Minimum = 0, Maximum = 128 };

        foreach (var node in new VisionNode[] { sequence, stats, check })
            recipe.Add(node);

        recipe.TryConnect(sequence.ImageOut, stats.ImageIn);
        recipe.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(LimitCheckNode.Value)));
        Publishing.Publish(recipe, stats, PublishedKind.Result, "Mean", "Brightness");

        var project = Project.Create(_folder, "Line", recipe, "Top");
        project.SetSaving(project.Stations[0], new ImageSaving(ImageSavePolicy.All));
        return project;
    }

    [Fact]
    public async Task A_real_station_inspects_for_the_plc_and_keeps_the_part_with_its_picture()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var project = StationProject();
            var plc = Line();
            var line = new LineWorkflow();

            var stationRuntime = new ProjectRuntime(project);
            using var stations = new ProjectStationBus(stationRuntime);
            var heard = new List<InspectionSnapshot>();
            stations.Inspected += (_, snapshot) => heard.Add(snapshot);

            await using (var runner = new WorkflowRunner(line.Graph, plc, stations, "Line 1 PLC"))
            {
                Assert.Empty(runner.Check());
                await StartAsync(runner, plc);

                var pass = await PassAfter(runner, () => plc.Set("DB34.20.0", true));
                Assert.Empty(pass.Failed);
            }

            // The station's own record of it, and what it told whoever listens.
            var snapshot = Assert.Single(heard);
            Assert.Equal(("Top", true), (snapshot.Station, snapshot.Verdict.Passed));
            Assert.Equal(12L, snapshot.Part!["PalletId"]);
            Assert.Same(snapshot, stationRuntime.Stations[0].Last);
            Assert.Equal((short)1, plc.Get("DB34.28", PlcType.Int16));

            // Closing the station writes what it queued: the picture, and its part in the .sann.
            stationRuntime.Dispose();

            var picture = Directory.GetFiles(Path.Combine(project.Folder, "images", "Top"), "*.png", SearchOption.AllDirectories).Single();
            var annotations = AnnotationFile.TryLoad(AnnotationFile.PathFor(picture), out var problem);

            Assert.Null(problem);
            Assert.Equal(
                [("PalletId", (object?)12L), ("PositionId", 3L), ("RunMode", 1L), (PartRecord.PartIdName, "1"), (PartRecord.ShotName, 1L)],
                annotations!.Part.Select(pair => (pair.Key, pair.Value)));
            Assert.Contains("\"part\"", File.ReadAllText(AnnotationFile.PathFor(picture)));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void A_picture_with_no_part_writes_no_part()
    {
        var path = Path.Combine(_folder, "plain.sann");
        new AnnotationFile { Image = "plain.png", Width = 4, Height = 3 }.Save(path);

        Assert.DoesNotContain("\"part\"", File.ReadAllText(path));
        Assert.Empty(AnnotationFile.TryLoad(path, out _)!.Part);
    }
}
