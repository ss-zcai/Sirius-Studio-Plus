using System.Text.Json;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Plc.Nodes;
using Sirius.Plc.Workflows;

namespace Sirius.Plc;

/// <summary>
/// Every controller of a project, running beside its stations: what Sirius
/// Edge, and Builder's Test Run, hold while a project runs.
/// </summary>
/// <remarks>
/// A controller whose workflow will not load, or whose driver is not
/// installed, is left out and said in <see cref="Problems"/>, so a screen
/// can say which and why; the others run.
/// </remarks>
public sealed class ProjectControllers : IAsyncDisposable
{
    private readonly List<WorkflowRunner> _runners = [];
    private readonly List<string> _problems = [];

    private ProjectControllers()
    {
    }

    /// <summary>Loads each controller's workflow and makes its driver, ready to start.</summary>
    /// <remarks>Needs <see cref="PlcNodeLibrary.Register"/> first, and any driver libraries loaded.</remarks>
    public static ProjectControllers Open(Project project, IStationBus? stations)
    {
        ArgumentNullException.ThrowIfNull(project);

        var opened = new ProjectControllers();

        foreach (var controller in project.Controllers)
        {
            try
            {
                var loaded = WorkflowFile.Load(project.WorkflowPath(controller));

                foreach (var warning in loaded.Warnings)
                    opened._problems.Add($"{controller.Name}: {warning}");

                var driver = PlcDrivers.Create(new PlcConnection(controller.Driver, controller.Address, controller.Options));
                opened._runners.Add(new WorkflowRunner(loaded.Graph, driver, stations, controller.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or PlcException)
            {
                opened._problems.Add($"{controller.Name} does not run: {ex.Message}");
            }
        }

        return opened;
    }

    public IReadOnlyList<WorkflowRunner> Runners => _runners;

    /// <summary>Controllers left out, and what their workflows' checks found.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Starts every controller. One that will not start is said in <see cref="Problems"/>.</summary>
    public void StartAll()
    {
        foreach (var runner in _runners.Where(runner => runner.State == ControllerState.Stopped))
        {
            try
            {
                runner.Start();

                foreach (var problem in runner.Check())
                    _problems.Add($"{runner.Name}: {problem}");
            }
            catch (InvalidOperationException ex)
            {
                _problems.Add($"{runner.Name} does not run: {ex.Message}");
            }
        }
    }

    public Task StopAllAsync() => Task.WhenAll(_runners.Select(runner => runner.StopAsync()));

    /// <summary>"Line 1 PLC: connected, 3 passes" for each - a line for the screen.</summary>
    public string StatusText => string.Join("   ·   ", _runners.Select(runner =>
        $"{runner.Name}: {(runner.State != ControllerState.Running ? "stopped" : runner.IsConnected ? "connected" : "not connected")}, " +
        $"{runner.Passes} pass{(runner.Passes == 1 ? string.Empty : "es")}" +
        (runner.LastError is { } error ? $" - {error}" : string.Empty)));

    public async ValueTask DisposeAsync()
    {
        foreach (var runner in _runners)
            await runner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The workflow a new controller starts with: a trigger bit, a part number, an inspection, and an answer.</summary>
public static class StarterWorkflow
{
    /// <summary>
    /// M0.0 rising: read the part number at MW10, have <paramref name="station"/>
    /// inspect, write PASS (true) or FAIL (false) to M0.1, then pulse M0.2 -
    /// "done". M0.3 is set when the station could not inspect. Siemens-style
    /// addresses, which the simulated PLC takes as they are.
    /// </summary>
    public static VisionGraph For(string? station)
    {
        var graph = new VisionGraph();

        var trigger = new PlcMonitorNode { Name = "Trigger", Address = "M0.0", X = 40, Y = 140 };
        var partNumber = new PlcReadNode { Name = "Part Number", Address = "MW10", Type = PlcType.Int16, X = 260, Y = 120 };
        var gather = new GatherNode { Name = "Part", Name1 = "PartNumber", X = 480, Y = 120 };
        var inspect = new InspectNode { Station = station ?? string.Empty, X = 700, Y = 120 };
        var result = new PlcWriteNode { Name = "Result", Address = "M0.1", Type = PlcType.Bool, X = 960, Y = 40 };
        var done = new PlcPulseNode { Name = "Done", Address = "M0.2", Width = 500, X = 1200, Y = 40 };
        var error = new PlcWriteNode { Name = "Error", Address = "M0.3", Value = "true", X = 960, Y = 300 };

        foreach (var node in new VisionNode[] { trigger, partNumber, gather, inspect, result, done, error })
            graph.Add(node);

        graph.TryConnect(trigger.Triggered, partNumber.Trigger);
        graph.TryConnect(partNumber.ValueOut, gather.Values[0]);
        graph.TryConnect(gather.Gathered, inspect.Trigger);
        graph.TryConnect(gather.PartOut, inspect.PartIn);
        graph.TryConnect(inspect.Done, result.Trigger);
        graph.TryConnect(inspect.PassedOut, result.ValueIn);
        graph.TryConnect(result.Done, done.Trigger);
        graph.TryConnect(inspect.Failed, error.Trigger);

        return graph;
    }
}
