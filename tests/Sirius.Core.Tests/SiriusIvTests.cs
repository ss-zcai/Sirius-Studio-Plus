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
            Assert.Equal($"6 pictures, 2 failed · {ImageFileItem.FormatSize(model.Files.Sum(file => file.Size))}", model.CountText);
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
            Assert.StartsWith("2 shown of 6 pictures, 2 failed · ", model.CountText);

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

    // ---- the file list's details ----------------------------------------------------

    [Fact]
    public void Each_picture_is_listed_with_its_type_size_and_time_without_being_opened()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var station = StationFolder();
            var path = Path.Combine(station, "2026-11-11", "20261111-080002-000_000005_PASS.jpg");
            var written = new DateTime(2026, 11, 11, 8, 0, 2);
            File.SetLastWriteTime(path, written);

            var model = Open(station);
            var jpeg = model.Files.Single(file => file.Path == path);

            Assert.Equal(["PNG", "PNG", "PNG", "PNG", "JPG", "BMP"], model.Files.Select(file => file.Kind));
            Assert.Equal(new FileInfo(path).Length, jpeg.Size);
            Assert.Equal(written, jpeg.Modified);
            Assert.Equal("2026-11-11 08:00:02", jpeg.ModifiedText);
            Assert.StartsWith("2026-11-11 08:00:02  ·  ", jpeg.DetailsText);
        });
    }

    [Fact]
    public void A_pictures_width_and_height_are_read_when_the_list_asks_for_them()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());
            var picture = model.Files[0];

            // Asking starts the read; the answer arrives on this thread.
            _ = picture.DimensionsText;
            WpfTestHost.PumpUntil(() => picture.DimensionsText.Length > 0);

            Assert.Equal((40, 30), (picture.Width, picture.Height));
            Assert.Equal("40 × 30", picture.DimensionsText);
            Assert.EndsWith("  ·  40 × 30", picture.DetailsText);
            Assert.Contains("PNG picture, 40 × 30 pixels", picture.Description);
        });
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(12_700, "12.4 KB")]
    [InlineData(1_300_000, "1.24 MB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    [InlineData(3_400_000_000, "3.17 GB")]
    public void Sizes_read_as_explorer_counts_them(long bytes, string expected) =>
        Assert.Equal(expected, ImageFileItem.FormatSize(bytes));

    [Theory]
    [InlineData(".png", DepthType.Cv8U, 1)]
    [InlineData(".png", DepthType.Cv16U, 1)]
    [InlineData(".png", DepthType.Cv8U, 3)]
    [InlineData(".jpg", DepthType.Cv8U, 1)]
    [InlineData(".jpg", DepthType.Cv8U, 3)]
    [InlineData(".bmp", DepthType.Cv8U, 1)]
    [InlineData(".bmp", DepthType.Cv8U, 3)]
    [InlineData(".tif", DepthType.Cv8U, 1)]
    [InlineData(".tif", DepthType.Cv16U, 1)]
    [InlineData(".tiff", DepthType.Cv8U, 3)]
    public void A_pictures_size_is_read_from_its_header(string extension, DepthType depth, int channels)
    {
        var path = Path.Combine(_folder, "size" + extension);

        using (var mat = new Mat(345, 678, depth, channels))
        {
            mat.SetTo(new MCvScalar(90, 120, 150));
            CvInvoke.Imwrite(path, mat);
        }

        Assert.Equal((678, 345), ImageHeader.TryRead(path));
    }

    [Fact]
    public void A_jpeg_with_an_exif_block_before_its_frame_is_still_read()
    {
        // An APP1 segment of 5000 bytes ahead of the frame header, as a camera writes.
        using var mat = new Mat(64, 96, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(77));

        using var encoded = new Emgu.CV.Util.VectorOfByte();
        CvInvoke.Imencode(".jpg", mat, encoded);
        var jpeg = encoded.ToArray();

        var exif = new byte[5000];
        exif[0] = 0xFF;
        exif[1] = 0xE1;
        exif[2] = (5000 - 2) >> 8;
        exif[3] = (5000 - 2) & 0xFF;

        var withExif = jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray();

        Assert.Equal((96, 64), ImageHeader.Read(new MemoryStream(withExif)));
    }

    [Fact]
    public void Files_that_are_not_pictures_or_are_cut_short_have_no_size()
    {
        Assert.Null(ImageHeader.Read(new MemoryStream("not a picture at all"u8.ToArray())));
        Assert.Null(ImageHeader.Read(new MemoryStream([])));
        Assert.Null(ImageHeader.Read(new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0])));
        Assert.Null(ImageHeader.Read(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00])));
        Assert.Null(ImageHeader.Read(new MemoryStream([(byte)'I', (byte)'I', 42, 0, 200, 0, 0, 0])));
        Assert.Null(ImageHeader.TryRead(Path.Combine(_folder, "missing.png")));
    }

    [Fact]
    public void A_sub_folders_pictures_are_kept_together_under_its_heading()
    {
        WpfTestHost.OnStaThread(() =>
        {
            // By path alone, "a\b\c.png" would sort between "a\b.png" and "a\c.png",
            // splitting folder a's pictures in two.
            Picture(Path.Combine(_folder, "a", "b.png"));
            Picture(Path.Combine(_folder, "a", "b", "c.png"));
            Picture(Path.Combine(_folder, "a", "c.png"));
            Picture(Path.Combine(_folder, "top.png"));

            var model = Open(_folder);

            Assert.Equal([@"a\b.png", @"a\c.png", @"a\b\c.png", "top.png"], model.Files.Select(file => file.RelativePath));
            Assert.Equal(["a", "a", @"a\b", null], model.Files.Select(file => file.Subfolder));
        });
    }

    [Fact]
    public void Searching_lists_the_pictures_whose_name_or_folder_holds_the_text()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(StationFolder());
            var chosen = model.Files[3];
            model.Choose([chosen]);

            model.SearchText = "000004";
            Assert.True(model.IsSearching);
            Assert.Equal([chosen], model.Files);
            Assert.Equal([chosen], model.Chosen);

            // Ignoring case, and the day folder counts.
            model.SearchText = "2026-11-10";
            Assert.Equal(2, model.Files.Count);
            model.SearchText = "_pass";
            Assert.Equal(3, model.Files.Count);

            // With the filter too: passed, and found.
            model.Filter = FileFilter.Failed;
            Assert.Empty(model.Files);
            Assert.True(model.IsEmpty);
            Assert.Equal("No pictures match. Esc clears the search.", model.EmptyText);

            model.Filter = FileFilter.All;
            model.SearchText = string.Empty;
            Assert.False(model.IsSearching);
            Assert.Equal(6, model.Files.Count);
        });
    }

    /// <summary>
    /// The window itself, not shown: its list grouped by day, and its
    /// templates - the file icon, the details line, the day headings - made
    /// for real, so a resource they name that is not there fails here and
    /// not in front of someone.
    /// </summary>
    [Fact]
    public void The_window_lists_pictures_under_their_days_with_icons_and_details()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(StationFolder());
            var window = new ViewerWindow(model);

            try
            {
                var list = (System.Windows.Controls.ListBox)window.FindName("FileList");
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(model.Files);

                Assert.Equal(
                    ["2026-11-10", "2026-11-11", null],
                    view.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.Name as string));
                Assert.Equal([2, 3, 1], view.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.ItemCount));

                // An entry, made as the list makes it.
                var entry = (System.Windows.FrameworkElement)list.ItemTemplate.LoadContent();
                entry.DataContext = model.Files[4];
                entry.Measure(new System.Windows.Size(300, 60));
                entry.Arrange(new System.Windows.Rect(0, 0, 300, 60));
                WpfTestHost.Settle(window.Dispatcher);

                var texts = Texts(entry);
                Assert.Contains("JPG", texts);
                Assert.Contains("PASS", texts);
                Assert.Contains(model.Files[4].Name, texts);
                Assert.Contains(texts, text => text.StartsWith(model.Files[4].ModifiedText, StringComparison.Ordinal));

                // A day's heading.
                var heading = (System.Windows.FrameworkElement)list.GroupStyle[0].HeaderTemplate.LoadContent();
                heading.DataContext = view.Groups![1];
                heading.Measure(new System.Windows.Size(300, 30));
                WpfTestHost.Settle(window.Dispatcher);
                Assert.Contains("2026-11-11", Texts(heading));

                // The right-click menu.
                Assert.Equal(
                    ["Copy _Name", "Copy Full _Path", "Show in _Explorer"],
                    list.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => (string)item.Header));

                // Sorted any way but by name, a folder's pictures are spread through the list: no headings.
                model.Sort = FileSort.Size;
                Assert.Empty(view.GroupDescriptions);
                model.Sort = FileSort.Name;
                Assert.Single(view.GroupDescriptions);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<string> Texts(System.Windows.DependencyObject root)
    {
        var found = new List<string>();

        void Walk(System.Windows.DependencyObject node)
        {
            if (node is System.Windows.Controls.TextBlock { Text: { Length: > 0 } text })
                found.Add(text);

            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node).OfType<System.Windows.DependencyObject>())
                Walk(child);
        }

        Walk(root);
        return found;
    }

    // ---- sorting ------------------------------------------------------------------------

    /// <summary>
    /// Four pictures that sort differently every way: by name a b c d; by
    /// time b c a d; by dimensions b d c a; by type (BMP JPG PNG TIF) c b a d.
    /// </summary>
    private string SortingFolder()
    {
        var folder = Path.Combine(_folder, "sorting");
        Directory.CreateDirectory(folder);

        void Write(string name, int width, int height, int day)
        {
            var path = Path.Combine(folder, name);

            using (var mat = new Mat(height, width, DepthType.Cv8U, 1))
            {
                CvInvoke.Randu(mat, new MCvScalar(0), new MCvScalar(255));
                CvInvoke.Imwrite(path, mat);
            }

            File.SetLastWriteTime(path, new DateTime(2026, 1, day, 9, 0, 0));
        }

        Write("a.png", 100, 50, 3);
        Write("b.jpg", 20, 10, 1);
        Write("c.bmp", 60, 60, 2);
        Write("d.tif", 300, 10, 4);
        return folder;
    }

    private static string[] Names(BrowserViewModel model) => model.Files.Select(file => file.Name).ToArray();

    [Fact]
    public void The_list_sorts_by_name_date_size_dimensions_or_type_either_way()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(SortingFolder());

            Assert.Equal(FileSort.Name, model.Sort);
            Assert.True(model.IsGroupedByFolder);
            Assert.Equal(["a.png", "b.jpg", "c.bmp", "d.tif"], Names(model));
            Assert.Equal("A to Z", model.SortDirectionText);

            model.SortDescending = true;
            Assert.Equal(["d.tif", "c.bmp", "b.jpg", "a.png"], Names(model));
            Assert.Equal(("↓", "Z to A"), (model.SortArrow, model.SortDirectionText));

            model.Sort = FileSort.Modified;
            Assert.False(model.IsGroupedByFolder);
            Assert.Equal("Newest first", model.SortDirectionText);
            Assert.Equal(["d.tif", "a.png", "c.bmp", "b.jpg"], Names(model));

            model.ReverseSortCommand.Execute(null);
            Assert.Equal(["b.jpg", "c.bmp", "a.png", "d.tif"], Names(model));
            Assert.Equal("Oldest first", model.SortDirectionText);

            model.Sort = FileSort.Size;
            Assert.Equal(model.Files.OrderBy(file => file.Size).Select(file => file.Name), Names(model));
            Assert.Equal("Smallest first", model.SortDirectionText);

            model.Sort = FileSort.Type;
            Assert.Equal(["c.bmp", "b.jpg", "a.png", "d.tif"], Names(model));
        });
    }

    [Fact]
    public void Sorting_by_dimensions_reads_every_pictures_size_first()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(SortingFolder());

            // None asked for yet: nothing on screen in a test.
            model.Sort = FileSort.Dimensions;
            WpfTestHost.PumpUntil(() => model.SortProgress is null && model.Files.All(file => file.HasDimensions));

            Assert.Equal(["b.jpg", "d.tif", "c.bmp", "a.png"], Names(model));
            Assert.Equal(["20 × 10", "300 × 10", "60 × 60", "100 × 50"], model.Files.Select(file => file.DimensionsText));

            model.SortDescending = true;
            Assert.Equal(["a.png", "c.bmp", "d.tif", "b.jpg"], Names(model));
            Assert.Equal("Largest first", model.SortDirectionText);
        });
    }

    [Fact]
    public void The_viewers_step_along_in_the_sorted_order_keeping_what_was_chosen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(SortingFolder());
            model.Layout = ViewerLayout.Two;

            var chosen = model.Files.Single(file => file.Name == "c.bmp");
            model.Choose([chosen]);

            model.Sort = FileSort.Modified;

            // Still chosen; the one after it is the next newer.
            Assert.Equal([chosen], model.Chosen);
            Assert.Equal(["c.bmp", "a.png"], model.Shown().Select(file => file.Name));
        });
    }

    [Fact]
    public void The_sort_is_remembered_between_sessions()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var settings = Path.Combine(_folder, "sirius-iv.json");

            _ = new BrowserViewModel(settings) { Sort = FileSort.Size, SortDescending = true };
            Assert.Contains("\"sort\":2", File.ReadAllText(settings));

            var again = new BrowserViewModel(settings);
            Assert.Equal((FileSort.Size, true), (again.Sort, again.SortDescending));
        });
    }

    [Fact]
    public void The_list_is_replaced_in_one_go()
    {
        var files = new FileCollection();
        var notices = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        files.CollectionChanged += (_, e) => notices.Add(e.Action);

        files.ReplaceAll(Enumerable.Range(0, 1000).Select(i => new ImageFileItem(Path.Combine(_folder, $"{i}.png"), _folder)));

        Assert.Equal(1000, files.Count);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset], notices);
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
