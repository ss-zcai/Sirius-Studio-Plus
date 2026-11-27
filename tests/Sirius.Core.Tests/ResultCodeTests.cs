using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Builder;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Sirius.Vision.Runtime;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Result codes beyond PASS and FAIL (F33): a station's list of results -
/// name, colour, PLC code, how each counts - what an inspection is given
/// from it, and where the result goes: counts, pictures' names, the .sann,
/// housekeeping, Builder.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ResultCodeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-results-" + Guid.NewGuid().ToString("N"));

    public ResultCodeTests()
    {
        TestCatalog.Ensure();
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

    /// <summary>PASS 1, REVIEW 3 (not counted), FAIL 2, NoLens 9 (not counted): a lens line's.</summary>
    private static ResultList LensResults(string? late = null, string? notInspected = null) => new(
        [
            new ResultCode("PASS", 1, ResultCounting.Pass, "#3FB950"),
            new ResultCode("REVIEW", 3, ResultCounting.NotCounted, "#E3B341"),
            new ResultCode("FAIL", 2, ResultCounting.Fail, "#DB5C5C"),
            new ResultCode("NoLens", 9, ResultCounting.NotCounted, "#8B949E"),
        ],
        late,
        notInspected);

    private static Verdict Judged(bool? passed, string? grade = null) =>
        new(passed, grade is null ? null : new Grade(grade, 1, passed == true), 1, passed == false ? 1 : 0);

    // ---- the list ----------------------------------------------------------------------

    [Fact]
    public void An_inspection_takes_its_grade_by_name_else_the_first_pass_or_fail()
    {
        var results = LensResults();

        Assert.Equal("PASS", results.For(Judged(true), 0)!.Name);
        Assert.Equal("FAIL", results.For(Judged(false), 0)!.Name);

        // A grade with a result's name is that result, whether or not the recipe passed it, ignoring case.
        Assert.Equal("REVIEW", results.For(Judged(true, "review"), 0)!.Name);
        Assert.Equal("NoLens", results.For(Judged(false, "NOLENS"), 0)!.Name);

        // A grade the list does not have: as the recipe judged.
        Assert.Equal("PASS", results.For(Judged(true, "B"), 0)!.Name);

        // Nothing judging: no result.
        Assert.Null(results.For(Judged(null), 0));

        // A node gone wrong without failing: ERROR when the list has it, else as judged.
        Assert.Equal("PASS", results.For(Judged(true), 1)!.Name);
        var withError = new ResultList([.. results, new ResultCode("ERROR", 7, ResultCounting.Fail, "#A371F7")]);
        Assert.Equal("ERROR", withError.For(Judged(true), 1)!.Name);
        Assert.Equal("FAIL", withError.For(Judged(false), 1)!.Name);

        // Accepted by the result, not the recipe's grade.
        var review = Judged(true, "REVIEW") with { Result = results.For(Judged(true, "REVIEW"), 0) };
        Assert.True(review.Passed);
        Assert.False(review.Accepted);
        Assert.Equal("REVIEW", ImageSaving.VerdictWord(review, 0));
        Assert.True(new ImageSaving(ImageSavePolicy.Failed).Wants(review, 0));
    }

    [Fact]
    public void Pass_and_fail_is_what_a_station_without_a_list_gives_as_before()
    {
        var results = ResultList.Default;

        Assert.Null(results.Problem());
        Assert.True(results.IsDefault);
        Assert.Equal((1, 2), (results.FirstPass!.Code, results.FirstFail!.Code));
        Assert.Equal("FAIL", results.LateResult!.Name);
        Assert.Equal("FAIL", results.NotInspectedResult!.Name);

        // A recipe's own grades - A, B, Reject - judge as before.
        Assert.Equal("PASS", results.For(Judged(true, "B"), 0)!.Name);
        Assert.Equal("FAIL", results.For(Judged(false, "Reject"), 0)!.Name);

        // ERROR stays ERROR in the picture's name.
        var errored = Judged(true) with { Result = results.For(Judged(true), 1) };
        Assert.Equal("ERROR", ImageSaving.VerdictWord(errored, 1));
        Assert.Equal("PASS", ImageSaving.VerdictWord(errored, 0));
    }

    [Theory]
    [InlineData("PASS,FAIL,pass", "two results called pass")]
    [InlineData("PASS,FAIL,RE VIEW", "letters, digits and '-' only")]
    [InlineData("PASS,FAIL,RE_VIEW", "letters, digits and '-' only")]
    [InlineData("PASS,FAIL,NONE", "already uses")]
    [InlineData("PASS,FAIL,ABCDEFGHIJKLMNOPQ", "too long")]
    public void A_list_with_a_name_that_cannot_be_is_refused(string names, string why)
    {
        var list = new ResultList(names.Split(',').Select((name, i) =>
            new ResultCode(name, i + 1, i == 0 ? ResultCounting.Pass : ResultCounting.Fail, "#3FB950")));

        Assert.Contains(why, list.Problem(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_list_needs_a_pass_a_fail_and_codes_the_plc_can_tell_apart()
    {
        Assert.Contains("pass", new ResultList([new ResultCode("FAIL", 2, ResultCounting.Fail, "#DB5C5C")]).Problem());
        Assert.Contains("fail", new ResultList([new ResultCode("OK", 1, ResultCounting.Pass, "#3FB950")]).Problem());
        Assert.Contains("needs results", new ResultList([]).Problem());

        Assert.Contains("code 1", new ResultList(
        [
            new ResultCode("OK", 1, ResultCounting.Pass, "#3FB950"),
            new ResultCode("NG", 1, ResultCounting.Fail, "#DB5C5C"),
        ]).Problem());

        Assert.Contains("#RRGGBB", new ResultList(
        [
            new ResultCode("OK", 1, ResultCounting.Pass, "green"),
            new ResultCode("NG", 2, ResultCounting.Fail, "#DB5C5C"),
        ]).Problem());

        // A late part is a reject: never a pass.
        Assert.Contains("cannot be PASS", LensResults(late: "PASS").Problem());
        Assert.Contains("not a result", LensResults(notInspected: "LATER").Problem());
        Assert.Null(LensResults(late: "REVIEW", notInspected: "FAIL").Problem());
        Assert.Equal("REVIEW", LensResults(late: "review").LateResult!.Name);
    }

    [Fact]
    public void The_worst_result_is_a_fail_then_one_not_counted_then_down_the_list()
    {
        var results = LensResults();
        var (pass, review, fail, noLens) = (results[0], results[1], results[2], results[3]);

        Assert.Same(fail, ResultCode.Worst([pass, review, fail, noLens]));
        Assert.Same(noLens, ResultCode.Worst([review, noLens, pass]));
        Assert.Same(review, ResultCode.Worst([pass, review, null]));
        Assert.Null(ResultCode.Worst([]));

        // Across lists, a fail is worse than a station's not counted, wherever it is in its own.
        var other = new ResultList(
        [
            new ResultCode("NG", 2, ResultCounting.Fail, "#DB5C5C"),
            new ResultCode("OK", 1, ResultCounting.Pass, "#3FB950"),
        ]);
        Assert.Same(other[0], ResultCode.Worst([noLens, other[0]]));
    }

    [Fact]
    public void A_tally_counts_each_result_ignoring_case_and_compares_by_value()
    {
        var results = LensResults();
        var tally = ResultTally.Empty.Add(results[0]).Add(results[1]).Add(results[0]).Add(null);

        Assert.Equal(2, tally["pass"]);
        Assert.Equal(1, tally["REVIEW"]);
        Assert.Equal(0, tally["FAIL"]);
        Assert.Equal(ResultTally.Of([new("PASS", 2), new("review", 1), new("FAIL", 0)]), tally);
        Assert.Equal("PASS 2, REVIEW 1", tally.ToString());
    }

    // ---- the project ---------------------------------------------------------------------

    [Fact]
    public void A_stations_results_are_kept_in_the_project_and_pass_and_fail_is_not_written()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Lens");
        var station = project.Stations[0];

        Assert.True(station.Results.IsDefault);
        Assert.DoesNotContain("\"results\"", File.ReadAllText(project.FilePath), StringComparison.Ordinal);

        var results = LensResults(late: "REVIEW");
        Assert.True(project.SetResults(station, results));
        Assert.False(project.SetResults(station, LensResults(late: "REVIEW")));

        var text = File.ReadAllText(project.FilePath);
        Assert.Contains("\"notCounted\"", text, StringComparison.Ordinal);
        Assert.Contains("\"late\": \"REVIEW\"", text, StringComparison.Ordinal);

        var loaded = Project.Load(project.FilePath);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(results, loaded.Project.Stations[0].Results);
        Assert.Equal("PASS, REVIEW, FAIL, NoLens", loaded.Project.Stations[0].Results.Summary);
        Assert.Equal(2, loaded.Project.Stations[0].Results[2].Rank);

        Assert.Throws<ArgumentException>(() => project.SetResults(station, new ResultList([])));

        // Back to PASS and FAIL: written as before.
        project.SetResults(station, ResultList.Default);
        Assert.DoesNotContain("\"results\"", File.ReadAllText(project.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_in_the_file_that_cannot_be_used_is_said_and_pass_and_fail_given()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Lens");
        project.SetResults(project.Stations[0], LensResults());

        File.WriteAllText(project.FilePath, File.ReadAllText(project.FilePath).Replace("\"NoLens\"", "\"FAIL\"", StringComparison.Ordinal));

        var loaded = Project.Load(project.FilePath);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("its results could not be used", StringComparison.Ordinal));
        Assert.True(loaded.Project.Stations[0].Results.IsDefault);
    }

    // ---- the station ---------------------------------------------------------------------

    /// <summary>
    /// Pictures of 50, 120 and 200 graded PASS up to 100, REVIEW up to 150,
    /// FAIL beyond - REVIEW passing as far as the recipe says.
    /// </summary>
    private VisionGraph GradedRecipe()
    {
        var images = Path.Combine(_folder, "images");
        Directory.CreateDirectory(images);

        foreach (var (name, value) in new[] { ("1.png", 50), ("2.png", 120), ("3.png", 200) })
        {
            using var mat = new Mat(60, 80, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(value));
            CvInvoke.Imwrite(Path.Combine(images, name), mat);
        }

        var graph = new VisionGraph();
        var sequence = new ImageSequenceNode { SourcePath = images };
        var stats = new RegionStatsNode();
        var grade = new GradeNode { Grades = "PASS, REVIEW, FAIL", Limits = "100, 150", Failing = "FAIL" };

        foreach (var node in new VisionNode[] { sequence, stats, grade })
            graph.Add(node);

        graph.TryConnect(sequence.ImageOut, stats.ImageIn);
        graph.TryConnect(stats.MeanOut, grade.Inputs.Single(port => port.Name == nameof(GradeNode.Value)));
        return graph;
    }

    private static StationRunner Runner(VisionGraph recipe)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            return new StationRunner("Lens", recipe);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task A_station_gives_each_inspection_its_result_and_counts_them()
    {
        using var station = Runner(GradedRecipe());
        station.Results = LensResults();

        var pass = await station.TriggerAsync();
        var review = await station.TriggerAsync();
        var fail = await station.TriggerAsync();

        Assert.Equal(["PASS", "REVIEW", "FAIL"], new[] { pass, review, fail }.Select(snapshot => snapshot!.Verdict.Result!.Name));
        Assert.Equal(3, review!.Verdict.Result!.Code);

        // The recipe passed REVIEW; the station does not accept it.
        Assert.True(review.Verdict.Passed);
        Assert.False(review.Verdict.Accepted);

        // Not counted: out of the yield, neither passed nor failed.
        Assert.Equal(new StationCounts(3, 1, 1, 0, 0) { NotCounted = 1 }, station.Counts);
        Assert.Equal(50, station.Counts.Yield);
        Assert.Equal(ResultTally.Of([new("PASS", 1), new("REVIEW", 1), new("FAIL", 1)]), station.ResultCounts);
    }

    [Fact]
    public async Task A_stations_result_counts_are_kept_over_a_restart_and_reset()
    {
        var path = Path.Combine(_folder, "Lens.counts.json");

        using (var station = Runner(GradedRecipe()))
        {
            station.Results = LensResults();
            station.KeepCountsIn(path);

            for (var i = 0; i < 3; i++)
                await station.TriggerAsync();
        }

        Assert.Contains("\"REVIEW\": 1", File.ReadAllText(path), StringComparison.Ordinal);

        using var again = Runner(GradedRecipe());
        again.KeepCountsIn(path);

        Assert.Equal(1, again.Counts.NotCounted);
        Assert.Equal(1, again.ResultCounts["REVIEW"]);

        again.ResetCounts();
        Assert.Same(ResultTally.Empty, again.ResultCounts);
        Assert.Equal(0, again.Counts.NotCounted);
    }

    [Fact]
    public async Task Without_a_list_the_recipes_own_grades_judge_as_before()
    {
        using var station = Runner(GradedRecipe());

        var verdicts = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var snapshot = await station.TriggerAsync();
            verdicts.Add(ImageSaving.VerdictWord(snapshot!.Verdict, snapshot.NodeErrors));
        }

        Assert.Equal(["PASS", "PASS", "FAIL"], verdicts);
        Assert.Equal(new StationCounts(3, 2, 1, 0, 0), station.Counts);
    }

    // ---- pictures ------------------------------------------------------------------------

    [Fact]
    public void A_pictures_sann_keeps_its_result_and_code()
    {
        var file = new AnnotationFile
        {
            Image = "x.tif",
            Width = 10,
            Height = 10,
            Passed = false,
            Result = "REVIEW",
            Code = 3,
            Grade = "REVIEW",
        };

        var path = Path.Combine(_folder, "x.sann");
        file.Save(path);

        var read = AnnotationFile.TryLoad(path, out var problem)!;
        Assert.Null(problem);
        Assert.Equal(("REVIEW", 3, false), (read.Result, read.Code, read.Passed));
        Assert.Equal("REVIEW", read.VerdictWord);
        Assert.Contains("\"verdict\": \"REVIEW\"", File.ReadAllText(path), StringComparison.Ordinal);

        // One from before result codes: as it was.
        var before = new AnnotationFile { Image = "y.tif", Width = 1, Height = 1, Passed = false };
        Assert.Equal("FAIL", before.VerdictWord);
        Assert.DoesNotContain("\"result\"", before.ToJson().ToJsonString(), StringComparison.Ordinal);

        // Judged later, a shot held until its set's last.
        Assert.Equal("NoLens", before.WithVerdict(false, null, 0, "NoLens", 9).VerdictWord);
    }

    /// <summary>
    /// Housekeeping knows the station's results: a picture named REVIEW is
    /// kept as long as a failure; one named by a result that counts as a
    /// pass goes first, as PASS does.
    /// </summary>
    [Fact]
    public void Housekeeping_keeps_a_result_that_is_not_a_pass_as_long_as_a_failure()
    {
        var station = Path.Combine(_folder, "Lens");
        var day = new DateTime(2026, 11, 20);
        var folder = Path.Combine(station, ImageSaving.DayFolder(day));
        Directory.CreateDirectory(folder);

        string Picture(long serial, string word)
        {
            var picture = Path.Combine(folder, ImageSaving.FileStem(day.AddHours(1).AddSeconds(serial), serial, word) + ".tif");
            File.WriteAllBytes(picture, new byte[1000]);
            File.SetLastWriteTime(picture, day.AddHours(1));
            return picture;
        }

        var review = Picture(1, "REVIEW");
        var good = Picture(2, "GOOD");
        var pass = Picture(3, "PASS");
        var fail = Picture(4, "FAIL");

        var results = new ResultList(
        [
            new ResultCode("PASS", 1, ResultCounting.Pass, "#3FB950"),
            new ResultCode("GOOD", 4, ResultCounting.Pass, "#3FB950"),
            new ResultCode("REVIEW", 3, ResultCounting.NotCounted, "#E3B341"),
            new ResultCode("FAIL", 2, ResultCounting.Fail, "#DB5C5C"),
        ]);

        // Room for two of the four: the passes go, oldest first, though REVIEW is older still.
        var keeper = new ImageHousekeeper(new ImageSaving(ImageSavePolicy.All, MaxGigabytes: 2500 / (double)(1L << 30)), station, _ => long.MaxValue)
        {
            Now = () => day.AddDays(1),
            Results = results,
        };

        var result = keeper.Run();

        Assert.Equal(2, result.Deleted);
        Assert.False(File.Exists(good));
        Assert.False(File.Exists(pass));
        Assert.True(File.Exists(review));
        Assert.True(File.Exists(fail));
    }

    /// <summary>Sirius IV reads any result from a station's picture name: REVIEW is not a pass.</summary>
    [Fact]
    public void Sirius_iv_reads_any_result_from_a_pictures_name()
    {
        Sirius.IV.ImageFileItem Item(string name) => new(Path.Combine(_folder, name), _folder);

        var review = Item("20261127-101500-000_000042_REVIEW.tif");
        Assert.Equal("REVIEW", review.Verdict);
        Assert.True(review.Failed);
        Assert.False(review.Passed);

        Assert.Equal("NoLens", Item("20261127-101500-000_000043_NoLens_2.tif").Verdict);
        Assert.Equal("PASS", Item("20261127-101500-000_000044_pass.tif").Verdict);
        Assert.False(Item("20261127-101500-000_000045_NONE.tif").Failed);
        Assert.Null(Item("holiday.jpg").Verdict);

        // A set's verdict: ERROR and FAIL first, then any other result, before PASS.
        var set = new[] { Item("20261127-101500-000_000046_PASS.tif"), review };
        foreach (var item in set)
            item.Set = set;

        Assert.Equal("REVIEW", set[0].SetVerdict);
    }

    // ---- Builder ------------------------------------------------------------------------

    [Fact]
    public void Builder_edits_a_stations_results_and_keeps_them_on_ok()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Lens");
        var station = project.Stations[0];
        var model = new ResultsViewModel(project, station);

        Assert.Equal(["PASS", "FAIL"], model.Rows.Select(row => row.Name));
        Assert.Equal("FAIL", model.LateRow!.Name);

        // A result added: REVIEW, not counted, the next code.
        model.AddCommand.Execute(null);
        var review = model.Rows[2];
        Assert.Equal(("REVIEW", "3", ResultCounting.NotCounted), (review.Name, review.CodeText, review.Counting.Value));
        Assert.Contains(review, model.MissedChoices);
        Assert.DoesNotContain(model.Rows[0], model.MissedChoices);

        // Moved above FAIL; late parts REVIEW, a second chance.
        model.UpCommand.Execute(review);
        Assert.Equal(["PASS", "REVIEW", "FAIL"], model.Rows.Select(row => row.Name));
        model.LateRow = review;

        // Nothing is the project's until OK.
        Assert.True(station.Results.IsDefault);
        Assert.True(model.Apply());

        Assert.Equal("PASS, REVIEW, FAIL", station.Results.Summary);
        Assert.Equal("REVIEW", station.Results.LateResult!.Name);
        Assert.Null(station.Results.NotInspected);
        Assert.Equal("FAIL", station.Results.NotInspectedResult!.Name);

        // A late part's REVIEW made a pass: the choice goes back to the first fail.
        review.Counting = CountingChoice.All[0];
        Assert.Equal("FAIL", model.LateRow!.Name);

        // A code that is not a number is said, and nothing kept.
        review.CodeText = "three";
        Assert.False(model.Apply());
        Assert.Contains("whole number", model.Problem, StringComparison.Ordinal);

        review.CodeText = "2";
        Assert.False(model.Apply());
        Assert.Contains("code 2", model.Problem, StringComparison.Ordinal);
        Assert.Equal("REVIEW", station.Results.LateResult!.Name);

        // Back to PASS and FAIL.
        model.StandardCommand.Execute(null);
        Assert.True(model.Apply());
        Assert.True(station.Results.IsDefault);
    }

    [Fact]
    public void The_results_window_is_made_with_its_rows()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var project = Project.Create(_folder, "Line", new VisionGraph(), "Lens");
            project.SetResults(project.Stations[0], LensResults(late: "REVIEW"));

            var window = ResultsWindow.Unshown(new ResultsViewModel(project, project.Stations[0]));
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                Assert.Equal("Results - Lens", window.Title);
                var model = (ResultsViewModel)window.DataContext;
                Assert.Equal(4, model.Rows.Count);
                Assert.Equal("REVIEW", model.LateRow!.Name);
                Assert.Equal("REVIEW", ((ResultRow)window.LateBox.SelectedItem).Name);
                Assert.Equal("FAIL", ((ResultRow)window.NotInspectedBox.SelectedItem).Name);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
