using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Sirius.Vision.Runtime;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A running station keeping its pictures, with an annotation file beside
/// each (F20): which it keeps, where, under what name, and what the
/// annotations hold.
/// </summary>
public sealed class ImageSavingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-saving-" + Guid.NewGuid().ToString("N"));

    public ImageSavingTests()
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

    // ---- the settings ----------------------------------------------------------

    [Theory]
    [InlineData(ImageSavePolicy.None, true, 0, false)]
    [InlineData(ImageSavePolicy.All, true, 0, true)]
    [InlineData(ImageSavePolicy.Failed, true, 0, false)]
    [InlineData(ImageSavePolicy.Failed, false, 0, true)]
    [InlineData(ImageSavePolicy.Failed, null, 0, false)]
    [InlineData(ImageSavePolicy.Failed, null, 1, true)]
    [InlineData(ImageSavePolicy.Failed, true, 1, true)]
    public void Failed_only_keeps_failures_and_inspections_a_node_went_wrong_in(
        ImageSavePolicy policy, bool? passed, int nodeErrors, bool kept) =>
        Assert.Equal(kept, new ImageSaving(policy).Wants(new Verdict(passed, null, 1, passed == false ? 1 : 0), nodeErrors));

    [Fact]
    public void A_picture_is_named_for_when_which_and_how_it_went()
    {
        var time = new DateTime(2026, 11, 11, 14, 23, 5, 123);

        Assert.Equal("20261111-142305-123_000042_FAIL", ImageSaving.FileStem(time, 42, new Verdict(false, null, 1, 1), 0));
        Assert.Equal("20261111-142305-123_000042_PASS", ImageSaving.FileStem(time, 42, new Verdict(true, null, 1, 0), 0));
        Assert.Equal("20261111-142305-123_000042_ERROR", ImageSaving.FileStem(time, 42, new Verdict(true, null, 1, 0), 2));
        Assert.Equal("20261111-142305-123_000042_NONE", ImageSaving.FileStem(time, 42, default, 0));
        Assert.Equal("2026-11-11", ImageSaving.DayFolder(time));
        Assert.Equal(new DateTime(2026, 11, 11), ImageSaving.DayOf("2026-11-11"));
        Assert.Null(ImageSaving.DayOf("notes"));
    }

    [Fact]
    public void A_station_keeps_its_image_saving_in_the_project_file_and_none_writes_nothing()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        var station = project.Stations[0];

        Assert.Equal(ImageSaving.Off, station.Saving);
        Assert.DoesNotContain("\"images\"", File.ReadAllText(project.FilePath));

        var saving = new ImageSaving(ImageSavePolicy.Failed, ImageFileFormat.Jpeg, @"D:\Pictures", 30, Annotations: false);
        Assert.True(project.SetSaving(station, saving));
        Assert.False(project.SetSaving(station, saving));

        var text = File.ReadAllText(project.FilePath);
        Assert.Contains("\"save\": \"failed\"", text);
        Assert.Contains("\"format\": \"jpeg\"", text);

        var loaded = Project.Load(project.FilePath);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(saving, loaded.Project.Stations[0].Saving);

        Assert.Throws<ArgumentException>(() => project.SetSaving(station, saving with { KeepDays = -1 }));
    }

    [Fact]
    public void Pictures_go_in_the_project_images_folder_unless_another_is_named()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top: left");
        var station = project.Stations[0];

        Assert.Equal(Path.Combine(project.Folder, "images", "Top_ left"), ImageSaving.Off.FolderFor(project, station));
        Assert.Equal(Path.Combine(project.Folder, "pictures", "Top_ left"), new ImageSaving(Folder: "pictures").FolderFor(project, station));
        Assert.Equal(@"D:\Pictures\Top_ left", new ImageSaving(Folder: @"D:\Pictures").FolderFor(project, station));
    }

    // ---- the annotation file -----------------------------------------------------

    [Fact]
    public void An_annotation_file_reads_back_what_was_written()
    {
        var written = new AnnotationFile
        {
            Image = "20261111-142305-123_000042_FAIL.png",
            Width = 640,
            Height = 480,
            Bits = 12,
            Station = "Top",
            Recipe = "cap.sgraph",
            Serial = 42,
            Time = new DateTime(2026, 11, 11, 14, 23, 5, 123),
            Passed = false,
            Grade = "Reject",
            Results = [new("Diameter", 12.5), new("Count", 3L), new("Code", "LOT 4471"), new("Sealed", true)],
            Layers =
            [
                new AnnotationLayer("Main View",
                [
                    new CircleShape(new Point2(100, 120), 30) { Label = "cap", Colour = 0xFFFF0000, Metrics = new Dictionary<string, double> { ["area"] = 2827.4 } },
                    new OrientedRectShape(new Point2(5, 6), 7, 8, 30) { LabelAnchor = LabelAnchor.Centre },
                    new PolygonShape([new Point2(0, 0), new Point2(4, 0), new Point2(4, 3)], Closed: false),
                ]),
                new AnnotationLayer("Defects", [new PointShape(new Point2(1, 2)), new LineShape(new Point2(0, 0), new Point2(3, 4))]),
            ],
        };

        var path = Path.Combine(_folder, "a.sann");
        written.Save(path);

        var read = AnnotationFile.TryLoad(path, out var problem);

        Assert.Null(problem);
        Assert.NotNull(read);
        Assert.Equal((written.Image, 640, 480, 12), (read!.Image, read.Width, read.Height, read.Bits));
        Assert.Equal(("Top", "cap.sgraph", 42L, written.Time, false, "Reject"), (read.Station, read.Recipe, read.Serial, read.Time, read.Passed, read.Grade));
        Assert.Equal("FAIL", read.VerdictWord);

        Assert.Equal(["Diameter", "Count", "Code", "Sealed"], read.Results.Select(result => result.Key));
        Assert.Equal(12.5, read.Results[0].Value);
        Assert.Equal(3L, read.Results[1].Value);
        Assert.Equal("LOT 4471", read.Results[2].Value);
        Assert.Equal(true, read.Results[3].Value);

        Assert.Equal(["Main View", "Defects"], read.Layers.Select(layer => layer.Name));

        for (var i = 0; i < written.Layers.Count; i++)
        {
            for (var j = 0; j < written.Layers[i].Shapes.Count; j++)
            {
                var (sent, back) = (written.Layers[i].Shapes[j], read.Layers[i].Shapes[j]);
                Assert.Equal(sent.GetType(), back.GetType());
                Assert.Equal((sent.Label, sent.Colour, sent.LabelAnchor), (back.Label, back.Colour, back.LabelAnchor));
                Assert.Equal(sent.Metrics, back.Metrics);
                Assert.Equal(sent.Bounds, back.Bounds);
            }
        }

        Assert.False(((PolygonShape)read.Layers[0].Shapes[2]).Closed);
        Assert.Equal(5, read.AllShapes().Count);
        Assert.Equal(Path.Combine(_folder, "part.sann"), AnnotationFile.PathFor(Path.Combine(_folder, "part.png")));
    }

    [Fact]
    public void A_file_that_is_not_ours_or_is_newer_says_so()
    {
        var other = Path.Combine(_folder, "other.sann");
        File.WriteAllText(other, """{ "format": "coco" }""");
        Assert.Null(AnnotationFile.TryLoad(other, out var problem));
        Assert.Contains("'coco'", problem);

        var newer = Path.Combine(_folder, "newer.sann");
        File.WriteAllText(newer, """{ "format": "sirius-annotations", "version": 99 }""");
        Assert.Null(AnnotationFile.TryLoad(newer, out problem));
        Assert.Contains("newer Sirius", problem);

        var broken = Path.Combine(_folder, "broken.sann");
        File.WriteAllText(broken, "{ not json");
        Assert.Null(AnnotationFile.TryLoad(broken, out problem));
        Assert.StartsWith("broken.sann could not be read", problem);
    }

    // ---- a station saving ---------------------------------------------------------

    /// <summary>
    /// The recipe the station tests use: a dark, a bright and a dark image;
    /// Region Stats' mean, published as "Brightness"; a Limit Check passing
    /// up to 128; a viewer drawing the region, published as "Main View".
    /// </summary>
    private Project ProjectSaving(ImageSaving saving, int bits = 0)
    {
        var images = Path.Combine(_folder, "source");
        Directory.CreateDirectory(images);

        foreach (var (name, value) in new[] { ("1.png", 50), ("2.png", 200), ("3.png", 60) })
        {
            using var mat = bits == 12 ? new Mat(300, 400, DepthType.Cv16U, 1) : new Mat(300, 400, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(value));
            CvInvoke.Imwrite(Path.Combine(images, name), mat);
        }

        var graph = new VisionGraph();
        var sequence = new ImageSequenceNode { SourcePath = images, Mode = ImreadModes.Unchanged };
        var stats = new RegionStatsNode();
        var check = new LimitCheckNode { Minimum = 0, Maximum = 128 };
        var viewer = new ViewerNode { Name = "Main View" };

        foreach (var node in new VisionNode[] { sequence, stats, check, viewer })
            graph.Add(node);

        graph.TryConnect(sequence.ImageOut, stats.ImageIn);
        graph.TryConnect(sequence.ImageOut, viewer.ImageIn);
        graph.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(LimitCheckNode.Value)));
        graph.TryConnect(stats.ShapesOut, viewer.ShapesIn);

        Publishing.Publish(graph, stats, PublishedKind.Result, "Mean", "Brightness");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        var project = Project.Create(_folder, "Line", graph, "Top");
        project.SetSaving(project.Stations[0], saving);
        return project;
    }

    /// <summary>Three triggers - dark, bright, dark - and the station closed, which writes what is queued.</summary>
    private static StationRunner RunThree(Project project)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var station = StationRunner.Open(project, project.Stations[0]);

            for (var i = 0; i < 3; i++)
                Assert.NotNull(station.TriggerAsync().GetAwaiter().GetResult());

            station.Dispose();
            return station;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static string[] Saved(string folder, string pattern) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray()
            : [];

    [Fact]
    public void Failed_only_saves_the_failed_picture_and_its_annotations_in_a_day_folder()
    {
        var project = ProjectSaving(new ImageSaving(ImageSavePolicy.Failed));
        var station = RunThree(project);

        Assert.Null(station.SavingProblem);

        var folder = Path.Combine(project.Folder, "images", "Top");
        var picture = Assert.Single(Saved(folder, "*.png"));

        Assert.Equal(ImageSaving.DayFolder(DateTime.Now), Path.GetFileName(Path.GetDirectoryName(picture)));
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}_000002_FAIL\.png$", Path.GetFileName(picture));

        // The picture as the camera - here the sequence - took it.
        using (var saved = CvInvoke.Imread(picture, ImreadModes.Unchanged))
            Assert.Equal(200, CvInvoke.Mean(saved).V0);

        var annotations = AnnotationFile.TryLoad(AnnotationFile.PathFor(picture), out var problem);

        Assert.Null(problem);
        Assert.Equal(Path.GetFileName(picture), annotations!.Image);
        Assert.Equal(("Top", "Top.sgraph", 2L, false), (annotations.Station, annotations.Recipe, annotations.Serial, annotations.Passed));
        Assert.Equal((400, 300), (annotations.Width, annotations.Height));

        var brightness = Assert.Single(annotations.Results);
        Assert.Equal("Brightness", brightness.Key);
        Assert.Equal(200.0, Convert.ToDouble(brightness.Value, System.Globalization.CultureInfo.InvariantCulture), 1);

        var layer = Assert.Single(annotations.Layers);
        Assert.Equal("Main View", layer.Name);
        Assert.NotEmpty(layer.Shapes);
    }

    [Fact]
    public void All_saves_every_picture_as_jpeg_and_no_annotations_when_told()
    {
        var project = ProjectSaving(new ImageSaving(ImageSavePolicy.All, ImageFileFormat.Jpeg, Annotations: false));
        RunThree(project);

        var folder = Path.Combine(project.Folder, "images", "Top");

        Assert.Equal(
            ["000001_PASS", "000002_FAIL", "000003_PASS"],
            Saved(folder, "*.jpg").Select(path => Path.GetFileNameWithoutExtension(path)[20..]));
        Assert.Empty(Saved(folder, "*.sann"));
    }

    /// <summary>
    /// A 12-bit frame is never cut to 8 bits by a format that cannot hold
    /// it: asked for JPEG, it is written as PNG, every value kept.
    /// </summary>
    [Fact]
    public void A_deep_picture_is_saved_as_png_whatever_format_was_chosen()
    {
        var project = ProjectSaving(new ImageSaving(ImageSavePolicy.All, ImageFileFormat.Jpeg), bits: 12);
        RunThree(project);

        var folder = Path.Combine(project.Folder, "images", "Top");

        Assert.Empty(Saved(folder, "*.jpg"));
        var pictures = Saved(folder, "*.png");
        Assert.Equal(3, pictures.Length);

        using var saved = CvInvoke.Imread(pictures[1], ImreadModes.Unchanged);
        Assert.Equal(DepthType.Cv16U, saved.Depth);
        Assert.Equal(200, CvInvoke.Mean(saved).V0);
    }

    [Fact]
    public void A_station_that_saves_nothing_writes_nothing()
    {
        var project = ProjectSaving(ImageSaving.Off);
        var station = RunThree(project);

        Assert.Null(station.Recorder);
        Assert.False(Directory.Exists(Path.Combine(project.Folder, "images")));
    }

    [Fact]
    public void Day_folders_older_than_the_days_kept_are_deleted_and_others_left_alone()
    {
        var project = ProjectSaving(new ImageSaving(ImageSavePolicy.Failed, KeepDays: 2));
        var folder = Path.Combine(project.Folder, "images", "Top");
        var today = DateTime.Now.Date;

        foreach (var name in new[] { ImageSaving.DayFolder(today.AddDays(-1)), ImageSaving.DayFolder(today.AddDays(-2)), "notes" })
            Directory.CreateDirectory(Path.Combine(folder, name));

        RunThree(project);

        Assert.Equal(
            new[] { ImageSaving.DayFolder(today.AddDays(-1)), ImageSaving.DayFolder(today), "notes" }.Order(StringComparer.Ordinal),
            Directory.GetDirectories(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_nearly_full_disk_stops_saving_and_says_so_and_the_station_carries_on()
    {
        var folder = Path.Combine(_folder, "full");
        using var recorder = new ImageRecorder(new ImageSaving(ImageSavePolicy.All), folder, "Top", freeSpace: _ => 10_000_000);

        using var mat = new Mat(10, 10, DepthType.Cv8U, 1);
        using var image = ImageData.Adopt(mat.Clone());

        var snapshot = new InspectionSnapshot("Top", 1, DateTime.Now, new Verdict(true, null, 1, 0), TimeSpan.Zero, 0,
            new Dictionary<string, object?>());

        recorder.Record(new InspectionRecord(snapshot, null, image, []));
        recorder.Dispose();

        Assert.Equal(0, recorder.Saved);
        Assert.Equal(1, recorder.Skipped);
        Assert.Contains("less than 1 GB free", recorder.Problem);
    }

    [Fact]
    public void A_recipe_with_nothing_to_save_says_so()
    {
        using var recorder = new ImageRecorder(new ImageSaving(ImageSavePolicy.All), Path.Combine(_folder, "nothing"), "Top");

        var snapshot = new InspectionSnapshot("Top", 1, DateTime.Now, default, TimeSpan.Zero, 0, new Dictionary<string, object?>());
        recorder.Record(new InspectionRecord(snapshot, null, null, []));

        Assert.StartsWith("Nothing to save", recorder.Problem);
    }

    /// <summary>
    /// Shapes measured on a picture of another size - a cut-out region's -
    /// would not sit where they belong on the saved picture, and are left
    /// out; published shapes come in as layers of their own.
    /// </summary>
    [Fact]
    public void Only_shapes_measured_on_the_saved_picture_are_annotated()
    {
        using var mat = new Mat(30, 40, DepthType.Cv8U, 1);
        using var image = ImageData.Adopt(mat.Clone());

        var here = new ShapeSet([new PointShape(new Point2(1, 1))], 40, 30);
        var elsewhere = new ShapeSet([new PointShape(new Point2(2, 2))], 10, 10);
        var found = new ShapeSet([new CircleShape(new Point2(5, 5), 2)], 40, 30);

        var snapshot = new InspectionSnapshot("Top", 7, DateTime.Now, new Verdict(true, null, 1, 0), TimeSpan.Zero, 0,
            new Dictionary<string, object?> { ["Holes"] = found, ["Count"] = 1, ["Picture"] = image });

        var annotations = ImageRecorder.Describe(
            new InspectionRecord(snapshot, "cap.sgraph", image, [new ViewerRecord("Main View", image, [here, elsewhere])]),
            image);

        Assert.Equal(["Main View", "Holes"], annotations.Layers.Select(layer => layer.Name));
        Assert.Equal(new Point2(1, 1), Assert.IsType<PointShape>(Assert.Single(annotations.Layers[0].Shapes)).At);
        Assert.Equal(["Count"], annotations.Results.Select(result => result.Key));
    }
}
