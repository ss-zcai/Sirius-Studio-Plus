using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Builder;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Shapes;
using Sirius.IV;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius IV, the image viewer: a folder's pictures listed, filtered, and
/// shown one at a time or side by side with their annotations - and
/// Builder's Image Saving dialog, which decides what it is given to show.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SiriusIvTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-iv-" + Guid.NewGuid().ToString("N"));

    public SiriusIvTests(WpfTestHost host)
    {
        _ = host;
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

    /// <summary>
    /// A station's folder: two days, five pictures - two of them failed -
    /// annotations beside the station's, and a picture of another kind,
    /// with things that are not pictures to be passed over.
    /// </summary>
    private string StationFolder()
    {
        var station = Path.Combine(_folder, "Top");

        Picture(Path.Combine(station, "2026-11-10", "20261110-090000-000_000001_PASS.png"));
        Picture(Path.Combine(station, "2026-11-10", "20261110-090001-000_000002_FAIL.png"), annotated: true);
        Picture(Path.Combine(station, "2026-11-11", "20261111-080000-000_000003_PASS.png"));
        Picture(Path.Combine(station, "2026-11-11", "20261111-080001-000_000004_ERROR.png"));
        Picture(Path.Combine(station, "2026-11-11", "20261111-080002-000_000005_PASS.jpg"));
        Picture(Path.Combine(station, "reference.bmp"));

        File.WriteAllText(Path.Combine(station, "notes.txt"), "not a picture");
        File.WriteAllText(Path.Combine(station, "2026-11-11", "half.saving.png"), "being written");

        return station;
    }

    private static void Picture(string path, bool annotated = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var mat = new Mat(30, 40, DepthType.Cv8U, 1))
        {
            mat.SetTo(new MCvScalar(100));
            CvInvoke.Imwrite(path, mat);
        }

        if (annotated)
        {
            new AnnotationFile
            {
                Image = Path.GetFileName(path),
                Width = 40,
                Height = 30,
                Station = "Top",
                Serial = 2,
                Passed = false,
                Results = [new("Brightness", 100.0)],
                Layers = [new AnnotationLayer("Main View", [new CircleShape(new Point2(20, 15), 5) { Label = "hole" }])],
            }.Save(AnnotationFile.PathFor(path));
        }
    }

    private static BrowserViewModel Open(string folder, bool subfolders = true)
    {
        var model = new BrowserViewModel { IncludeSubfolders = subfolders };
        WpfTestHost.Wait(model.OpenAsync(folder));
        return model;
    }

    [Fact]
    public void A_station_folder_is_listed_with_its_days_in_the_order_taken()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());

            Assert.Equal(
                [
                    @"2026-11-10\20261110-090000-000_000001_PASS.png",
                    @"2026-11-10\20261110-090001-000_000002_FAIL.png",
                    @"2026-11-11\20261111-080000-000_000003_PASS.png",
                    @"2026-11-11\20261111-080001-000_000004_ERROR.png",
                    @"2026-11-11\20261111-080002-000_000005_PASS.jpg",
                    "reference.bmp",
                ],
                model.Files.Select(file => file.RelativePath));

            Assert.Equal(["PASS", "FAIL", "PASS", "ERROR", "PASS", null], model.Files.Select(file => file.Verdict));
            Assert.Equal("2026-11-10", model.Files[0].Subfolder);
            Assert.Null(model.Files[5].Subfolder);
            Assert.Equal("6 pictures, 2 failed", model.CountText);
            Assert.Equal("Top - Sirius IV", model.Title);

            // The first is chosen and shown.
            Assert.Equal([model.Files[0]], model.Chosen);
        });
    }

    [Fact]
    public void Without_sub_folders_only_the_folders_own_pictures_are_listed()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder(), subfolders: false);
            Assert.Equal(["reference.bmp"], model.Files.Select(file => file.Name));
        });
    }

    [Fact]
    public void Failed_shows_failures_and_errors_and_keeps_what_was_chosen_where_it_can()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());
            var failed = model.Files[1];
            model.Choose([failed]);

            model.Filter = FileFilter.Failed;

            Assert.Equal(["FAIL", "ERROR"], model.Files.Select(file => file.Verdict));
            Assert.Equal([failed], model.Chosen);
            Assert.Equal("2 shown of 6 pictures, 2 failed", model.CountText);

            model.Filter = FileFilter.Passed;
            Assert.Equal(3, model.Files.Count);
            Assert.Equal([model.Files[0]], model.Chosen);
        });
    }

    /// <summary>
    /// With several chosen, the viewers show those; with one, it and the ones
    /// after it - so stepping down the list steps a strip of pictures along.
    /// </summary>
    [Fact]
    public void The_viewers_show_what_is_chosen_or_the_strip_from_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());

            model.Layout = ViewerLayout.Four;
            Assert.Equal(4, model.Tiles.Count);
            Assert.Equal((2, 2), (model.Rows, model.Columns));

            model.Choose([model.Files[3]]);
            Assert.Equal([model.Files[3], model.Files[4], model.Files[5]], model.Shown());
            Assert.Equal(
                [model.Files[3], model.Files[4], model.Files[5], null],
                model.Tiles.Select(tile => tile.File));

            model.Choose([model.Files[4], model.Files[0]]);
            Assert.Equal([model.Files[0], model.Files[4]], model.Shown());

            model.Layout = ViewerLayout.Single;
            Assert.Single(model.Tiles);
            Assert.Equal([model.Files[0]], model.Shown());

            model.Layout = ViewerLayout.Nine;
            Assert.Equal((3, 3), (model.Rows, model.Columns));
            Assert.Equal(9, model.Tiles.Count);
        });
    }

    [Fact]
    public void A_picture_is_shown_with_the_shapes_and_results_from_its_annotation_file()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());
            var tile = new TileViewModel();

            WpfTestHost.Wait(tile.Show(model.Files[1]));

            Assert.NotNull(tile.Picture);
            Assert.Equal((40, 30), (tile.Picture!.PixelWidth, tile.Picture.PixelHeight));
            Assert.Null(tile.Problem);
            Assert.Equal("FAIL", tile.Verdict);

            var circle = Assert.IsType<CircleShape>(Assert.Single(tile.Shapes!.Shapes));
            Assert.Equal("hole", circle.Label);
            Assert.Equal((40, 30), (tile.Shapes.FrameWidth, tile.Shapes.FrameHeight));

            Assert.Contains("Brightness: 100", tile.Details);
            Assert.Contains("Layer Main View: 1 shape", tile.Details);

            // One with no annotations is just the picture.
            WpfTestHost.Wait(tile.Show(model.Files[0]));
            Assert.Null(tile.Shapes);
            Assert.Equal("PASS", tile.Verdict);

            WpfTestHost.Wait(tile.Show(null));
            Assert.True(tile.IsEmpty);
            Assert.Null(tile.Picture);
        });
    }

    [Fact]
    public void A_picture_that_will_not_read_says_so()
    {
        WpfTestHost.OnStaThread(() =>
        {
            File.WriteAllText(Path.Combine(_folder, "broken.png"), "not a png");
            var model = Open(_folder);
            var tile = new TileViewModel();

            WpfTestHost.Wait(tile.Show(model.Files.Single(file => file.Name == "broken.png")));

            Assert.Null(tile.Picture);
            Assert.Contains("broken.png", tile.Problem);
        });
    }

    [Fact]
    public void The_folder_and_layout_are_remembered_for_next_time()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var settings = Path.Combine(_folder, "settings", "sirius-iv.json");
            var station = StationFolder();

            var first = new BrowserViewModel(settings) { Layout = ViewerLayout.Two, Filter = FileFilter.Failed };
            WpfTestHost.Wait(first.OpenAsync(station));

            var next = new BrowserViewModel(settings);

            Assert.Equal(station, next.LastFolder);
            Assert.Equal(ViewerLayout.Two, next.Layout);
            Assert.Equal(FileFilter.Failed, next.Filter);
            Assert.Equal(2, next.Tiles.Count);
        });
    }

    // ---- measurements drawn by hand ------------------------------------------------

    /// <summary>
    /// A real viewer, its template applied, bound to a tile as the window
    /// binds it - since what went wrong was between the two: a measurement
    /// drawn on one picture stayed on screen when the list moved on.
    /// </summary>
    private static Sirius.Studio.App.Controls.ImageViewer Viewer(TileViewModel tile)
    {
        WpfTestHost.EnsureApplicationResources();
        var viewer = new Sirius.Studio.App.Controls.ImageViewer { DataContext = tile };

        System.Windows.Data.BindingOperations.SetBinding(
            viewer, Sirius.Studio.App.Controls.ImageViewer.ImageSourceProperty, new System.Windows.Data.Binding(nameof(TileViewModel.Picture)));
        System.Windows.Data.BindingOperations.SetBinding(
            viewer, Sirius.Studio.App.Controls.ImageViewer.ShapesProperty, new System.Windows.Data.Binding(nameof(TileViewModel.Shapes)));
        System.Windows.Data.BindingOperations.SetBinding(
            viewer,
            Sirius.Studio.App.Controls.ImageViewer.MeasurementsProperty,
            new System.Windows.Data.Binding(nameof(TileViewModel.Measurements)) { Mode = System.Windows.Data.BindingMode.TwoWay });

        viewer.Measure(new System.Windows.Size(900, 700));
        viewer.Arrange(new System.Windows.Rect(0, 0, 900, 700));
        viewer.UpdateLayout();
        return viewer;
    }

    /// <summary>A measurement as the viewer writes back one drawn by hand.</summary>
    private static void Draw(Sirius.Studio.App.Controls.ImageViewer viewer, double x) =>
        viewer.SetCurrentValue(
            Sirius.Studio.App.Controls.ImageViewer.MeasurementsProperty,
            (IReadOnlyList<MeasurementRecord>)[.. viewer.Measurements ?? [], new MeasurementRecord("PointToPoint", [new Point2(x, 2), new Point2(x + 10, 20)], "#F59E0B", null)]);

    [Fact]
    public void Each_picture_keeps_its_own_measurements_as_the_list_moves_on_and_back()
    {
        WpfTestHost.OnStaThread(() =>
        {
            // One picture of another size, which makes the viewer clear its own.
            var folder = StationFolder();

            using (var big = new Mat(60, 80, DepthType.Cv8U, 1))
            {
                big.SetTo(new MCvScalar(30));
                CvInvoke.Imwrite(Path.Combine(folder, "zz-big.png"), big);
            }

            var model = Open(folder);
            var tile = Assert.Single(model.Tiles);
            var viewer = Viewer(tile);
            var (first, second, third) = (model.Files[0], model.Files[1], model.Files.Single(file => file.Name == "zz-big.png"));

            model.Choose([first]);
            WpfTestHost.PumpUntil(() => tile.Picture is not null && tile.File == first && !tile.IsLoading);
            Draw(viewer, 1);

            Assert.True(first.IsModified);
            Assert.True(model.HasUnsavedChanges);
            Assert.StartsWith("*", model.Title);

            // Another picture: none of the first one's measurements on it.
            model.Choose([second]);
            WpfTestHost.PumpUntil(() => !tile.IsLoading && tile.File == second);
            Assert.Empty(tile.Measurements);
            Assert.Empty(viewer.Measurements ?? []);
            Assert.False(second.IsModified);

            // One of another size: still none, and the first one's still kept.
            model.Choose([third]);
            WpfTestHost.PumpUntil(() => !tile.IsLoading && tile.File == third);
            Assert.Empty(viewer.Measurements ?? []);

            // And back: the first picture's measurement is still there.
            model.Choose([first]);
            WpfTestHost.PumpUntil(() => !tile.IsLoading && tile.File == first);
            Assert.Equal(1, Assert.Single(tile.Measurements).Points[0].X);
            Assert.Single(viewer.Measurements!);
            Assert.Equal(1, model.UnsavedCount);
        });
    }

    [Fact]
    public void Saving_writes_measurements_into_the_annotation_file_keeping_what_the_station_wrote()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());
            var tile = Assert.Single(model.Tiles);
            var viewer = Viewer(tile);
            var (plain, annotated) = (model.Files[0], model.Files[1]);

            model.Choose([plain]);
            WpfTestHost.PumpUntil(() => tile.Picture is not null && tile.File == plain && !tile.IsLoading);
            Draw(viewer, 3);

            model.Choose([annotated]);
            WpfTestHost.PumpUntil(() => !tile.IsLoading && tile.File == annotated);
            Draw(viewer, 5);

            Assert.True(model.SaveCommand.CanExecute(null));
            Assert.True(model.Save());

            Assert.False(model.HasUnsavedChanges);
            Assert.False(plain.IsModified || annotated.IsModified);
            Assert.False(model.SaveCommand.CanExecute(null));

            // A picture with no annotation file has one now, holding its measurement.
            var made = AnnotationFile.TryLoad(plain.AnnotationPath, out _)!;
            Assert.Equal((plain.Name, 40, 30), (made.Image, made.Width, made.Height));
            Assert.Equal(3, Assert.Single(made.Measurements).Points[0].X);

            // One the station wrote keeps its shapes and results beside the measurement.
            var kept = AnnotationFile.TryLoad(annotated.AnnotationPath, out _)!;
            Assert.Equal(5, Assert.Single(kept.Measurements).Points[0].X);
            Assert.Equal("Main View", Assert.Single(kept.Layers).Name);
            Assert.Equal("Brightness", Assert.Single(kept.Results).Key);
            Assert.False(kept.Passed);

            // Read again, it is the picture's own.
            model.Choose([plain]);
            WpfTestHost.PumpUntil(() => !tile.IsLoading && tile.File == plain);
            Assert.Single(viewer.Measurements!);
            Assert.False(model.HasUnsavedChanges);
        });
    }

    [Fact]
    public void Leaving_with_measurements_not_saved_asks_first()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var station = StationFolder();
            var model = Open(station);
            var tile = Assert.Single(model.Tiles);
            var viewer = Viewer(tile);

            WpfTestHost.PumpUntil(() => tile.Picture is not null && !tile.IsLoading);
            Draw(viewer, 7);

            string? asked = null;
            model.Ask = (message, _) =>
            {
                asked = message;
                return System.Windows.MessageBoxResult.Cancel;
            };

            // Cancel: stay, nothing lost.
            Assert.False(model.ConfirmLeaving());
            Assert.Equal(
                "Measurements on 1 picture are not saved. Save them in the pictures' annotation files before closing?",
                asked);

            var opening = model.OpenAsync(_folder);
            WpfTestHost.Wait(opening);
            Assert.False(opening.Result);
            Assert.Equal(station, model.Folder);
            Assert.True(model.HasUnsavedChanges);

            // Yes: saved, then on.
            model.Ask = (_, _) => System.Windows.MessageBoxResult.Yes;
            Assert.True(model.ConfirmLeaving());
            Assert.False(model.HasUnsavedChanges);
            Assert.Single(AnnotationFile.TryLoad(model.Files[0].AnnotationPath, out _)!.Measurements);

            // No: let go.
            Draw(viewer, 9);
            Assert.True(model.HasUnsavedChanges);
            model.Ask = (_, _) => System.Windows.MessageBoxResult.No;

            opening = model.OpenAsync(_folder);
            WpfTestHost.Wait(opening);
            Assert.True(opening.Result);
            Assert.False(model.HasUnsavedChanges);
        });
    }

    // ---- Builder's Image Saving dialog --------------------------------------------

    [Fact]
    public void Image_saving_is_chosen_in_Builder_and_kept_only_on_OK()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
            var station = project.Stations[0];
            var model = new ImageSavingViewModel(project, station);

            PropertyRow Row(string name) => model.Categories.SelectMany(category => category.Rows).Single(row => row.Name == name);

            Assert.False(Row("Format").IsEnabled);

            Row("Save").Value = ImageSavePolicy.Failed;
            Row("Format").Value = ImageFileFormat.Jpeg;
            Row("Keep for (days)").Value = "30";

            Assert.True(Row("Format").IsEnabled);
            Assert.Equal(Path.Combine(project.Folder, "images", "Top"), Row("Station's folder").Value);
            Assert.EndsWith("_000042_FAIL.jpg", model.Example);

            // Nothing is written until OK.
            Assert.Equal(ImageSaving.Off, station.Saving);

            Assert.True(model.Apply());
            Assert.Equal(new ImageSaving(ImageSavePolicy.Failed, ImageFileFormat.Jpeg, KeepDays: 30), station.Saving);
            Assert.Equal("Saves failed images, JPEG, 30 days", new StationViewModel(project, station).SavingText);
        });
    }

    [Fact]
    public void A_folder_picked_inside_the_project_is_kept_relative_to_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
            var model = new ImageSavingViewModel(project, project.Stations[0]);
            PropertyRow Row(string name) => model.Categories.SelectMany(category => category.Rows).Single(row => row.Name == name);

            Row("Save").Value = ImageSavePolicy.All;

            model.PickFolder = _ => Path.Combine(project.Folder, "pictures");
            model.BrowseCommand.Execute(null);
            Assert.Equal("pictures", model.Draft.Folder);

            model.PickFolder = _ => Path.Combine(project.Folder, "images");
            model.BrowseCommand.Execute(null);
            Assert.Null(model.Draft.Folder);

            model.PickFolder = _ => @"D:\Line pictures";
            model.BrowseCommand.Execute(null);
            Assert.Equal(@"D:\Line pictures", model.Draft.Folder);
        });
    }

    [Fact]
    public void Days_that_are_not_a_number_are_refused_in_words()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
            var model = new ImageSavingViewModel(project, project.Stations[0]);

            model.Categories.SelectMany(category => category.Rows).Single(row => row.Name == "Keep for (days)").Value = "a month";

            Assert.False(model.Apply());
            Assert.StartsWith("Keep for is a whole number of days", model.Problem);
            Assert.Equal(ImageSaving.Off, project.Stations[0].Saving);
        });
    }
}
