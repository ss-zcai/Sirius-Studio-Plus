using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;
using Sirius.Plc;
using Sirius.Plc.Nodes;
using Sirius.Plc.Workflows;
using Sirius.Tools.ShotSample;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The sample made by tools/ShotSample (F26): six parts of three pictures,
/// run as the user would try it - Studio's Run Folder, then the station
/// with the simulated PLC's 3-shot starter - each part judged once.
/// </summary>
public sealed class ShotSampleTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-shot-sample-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Parts 2, 4 and 6 have a mark - 2 and 4 under one light only.</summary>
    private static readonly bool[] Expected = [true, false, true, false, true, false];

    [Fact]
    public void The_sample_is_a_station_of_three_shots_with_a_starter_that_answers_each()
    {
        var written = ShotSampleProject.Write(_folder);
        var project = Project.Load(written.ProjectFile).Project;

        Assert.Equal(18, written.ImageCount);
        Assert.True(File.Exists(Path.Combine(project.Folder, "README.txt")));

        var station = Assert.Single(project.Stations);
        Assert.Equal(("Top", 3), (station.Name, station.ShotsPerPart));

        var controller = Assert.Single(project.Controllers);
        var workflow = WorkflowFile.Load(project.WorkflowPath(controller)).Graph;
        Assert.Contains(workflow.Nodes, node => node.Name == "Shot Taken");
        Assert.Empty(new WorkflowRunner(workflow, new SimulatedPlc(), null, controller.Name).Check());
    }

    [Fact]
    public void Run_folder_over_the_sample_judges_six_parts_from_eighteen_pictures()
    {
        var project = Project.Load(ShotSampleProject.Write(_folder).ProjectFile).Project;
        var recipe = GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph;
        var sequence = recipe.Nodes.OfType<ImageSequenceNode>().Single();

        using var scheduler = new GraphScheduler(recipe);
        var summary = FolderRun.RunAsync(recipe, scheduler, sequence, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.StartsWith("18 images, 6 parts in", summary.Describe(), StringComparison.Ordinal);
        Assert.Equal((12, 3, 3), (summary.Received, summary.Passed, summary.Failed));
        Assert.Equal(0, summary.WithErrors);
        Assert.Equal(Expected, summary.Items.Where(item => !item.Received).Select(item => item.Verdict.Passed == true));
    }

    [Fact]
    public async Task The_sample_line_judges_each_part_once_from_the_trigger_alone()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var project = Project.Load(ShotSampleProject.Write(_folder).ProjectFile).Project;
            var workflow = WorkflowFile.Load(project.WorkflowPath(project.Controllers[0])).Graph;

            foreach (var pulse in workflow.Nodes.OfType<PlcPulseNode>())
                pulse.Width = 20;

            workflow.Nodes.OfType<PlcMonitorNode>().Single().PollInterval = 5;

            var plc = new SimulatedPlc();
            using var runtime = new ProjectRuntime(project);
            using var stations = new ProjectStationBus(runtime);
            var parts = new List<PartRecord>();

            await using (var runner = new WorkflowRunner(workflow, plc, stations, "Line PLC"))
            {
                runner.PartFinished += (_, part) =>
                {
                    lock (parts)
                        parts.Add(part);
                };

                runner.Start();
                await Until(() => runner.IsConnected && plc.Reads > 1);

                for (var trigger = 0; trigger < 18; trigger++)
                {
                    await PassAfter(runner, () => plc.Set("M0.0", true));
                    plc.Set("M0.0", false);
                    var reads = plc.Reads;
                    await Until(() => plc.Reads > reads + 1);
                }
            }

            Assert.Equal(["1", "2", "3", "4", "5", "6"], parts.Select(part => part.Id));
            Assert.Equal(Expected, parts.Select(part => part.Verdict == PartVerdict.Passed));
            Assert.All(parts, part => Assert.Equal(3, part.Shots.Count));

            // The PLC told: each part's result once, and "shot taken" for the two before each last.
            Assert.Equal(6, plc.Writes.Count(write => write.Address == "M0.1"));
            Assert.Equal(12, plc.Writes.Count(write => write.Address == "M0.4" && write.Value is true));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
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
}
