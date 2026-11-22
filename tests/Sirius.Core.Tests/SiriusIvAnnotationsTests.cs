using System.ComponentModel;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Shapes;
using Sirius.IV;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius IV's Annotations panel (F20b): the picture in the viewer last
/// clicked as tables, its layers switched on and off in every viewer, a
/// shape picked and zoomed to, and the pictures' annotations as one CSV.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SiriusIvAnnotationsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-ivann-" + Guid.NewGuid().ToString("N"));

    public SiriusIvAnnotationsTests(WpfTestHost host)
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

    /// <summary>Three pictures: two with a Main View and a Holes layer, the last with no annotations.</summary>
    private string Pictures()
    {
        Picture("0001_FAIL.png", serial: 1, holes: 2);
        Picture("0002_PASS.png", serial: 2, holes: 1);
        Picture("0003_PASS.png", serial: 3, holes: null);
        return _folder;
    }

    private void Picture(string name, long serial, int? holes)
    {
        var path = Path.Combine(_folder, name);

        using (var mat = new Mat(100, 200, DepthType.Cv8U, 1))
        {
            mat.SetTo(new MCvScalar(90));
            CvInvoke.Imwrite(path, mat);
        }

        if (holes is not { } count)
            return;

        new AnnotationFile
        {
            Image = name,
            Width = 200,
            Height = 100,
            Station = "Top",
            Serial = serial,
            Passed = name.Contains("PASS", StringComparison.Ordinal),
            Results = [new("Holes", (double)count)],
            Layers =
            [
                new AnnotationLayer("Main View", [new RectShape(new Bounds(10, 10, 60, 40)) { Label = "body" }]),
                new AnnotationLayer("Holes", Enumerable.Range(0, count).Select(i => (Shape)new CircleShape(new Point2(120 + (i * 30), 50), 6)).ToArray()),
            ],
        }.Save(AnnotationFile.PathFor(path));
    }

    private static BrowserViewModel Open(string folder)
    {
        var model = new BrowserViewModel();
        WpfTestHost.Wait(model.OpenAsync(folder));
        return model;
    }

    [Fact]
    public void The_panel_shows_the_viewer_last_clicked_and_picks_shapes_with_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            // No folder open yet: what to do.
            var none = new BrowserViewModel().Annotations;
            Assert.False(none.HasAnnotations);
            Assert.StartsWith("Choose a picture", none.EmptyText, StringComparison.Ordinal);

            var model = Open(Pictures());
            model.Layout = ViewerLayout.Two;

            var panel = model.Annotations;
            Assert.Same(model.Tiles[0], panel.Tile);

            // The first chosen: it and the one after it, side by side.
            model.Choose([model.Files[0]]);
            WpfTestHost.PumpUntil(() => model.Tiles.All(tile => !tile.IsLoading));

            Assert.True(panel.HasAnnotations);
            Assert.Equal("Viewer 1 · 0001_FAIL.png", panel.Caption);
            Assert.Equal("FAIL", panel.Verdict);
            Assert.Contains(panel.Facts, fact => fact.Name == "Holes" && fact.Value == "2");
            Assert.Equal("Shapes (3)", panel.ShapesHeader);
            Assert.Equal(["Main View (1)", "Holes (2)"], panel.Layers.Select(layer => layer.Text));

            // A row picked lights its shape in the viewer; one picked there, its row.
            panel.SelectedShapeIndex = 2;
            Assert.Equal(2, model.Tiles[0].SelectedShapeIndex);
            model.Tiles[0].SelectedShapeIndex = 1;
            Assert.Equal(1, panel.SelectedShapeIndex);

            // The second viewer clicked: its picture.
            model.CurrentTile = model.Tiles[1];
            Assert.Equal("Viewer 2 · 0002_PASS.png", panel.Caption);
            Assert.Equal("Shapes (2)", panel.ShapesHeader);

            // Moved on to a picture without annotations: said so.
            model.Choose([model.Files[1]]);
            WpfTestHost.PumpUntil(() => model.Tiles.All(tile => !tile.IsLoading));
            Assert.Equal("Viewer 2 · 0003_PASS.png", panel.Caption);
            Assert.False(panel.HasAnnotations);
            Assert.False(panel.HasLayers);
            Assert.Contains("no annotation file", panel.EmptyText, StringComparison.Ordinal);

            // A layout of one: the panel follows the viewer left.
            model.Layout = ViewerLayout.Single;
            Assert.Same(model.Tiles[0], panel.Tile);
        });
    }

    [Fact]
    public void A_layer_switched_off_goes_from_every_viewer_and_every_picture_after()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(Pictures());
            model.Layout = ViewerLayout.Two;
            model.Choose([model.Files[0]]);
            WpfTestHost.PumpUntil(() => model.Tiles.All(tile => !tile.IsLoading));

            var panel = model.Annotations;
            model.Tiles[0].SelectedShapeIndex = 2;

            panel.Layers.Single(layer => layer.Name == "Holes").IsShown = false;

            // Gone from both viewers, and from the table; nothing left picked.
            Assert.Single(model.Tiles[0].Shapes!.Shapes);
            Assert.Single(model.Tiles[1].Shapes!.Shapes);
            Assert.Equal("Shapes (1)", panel.ShapesHeader);
            Assert.Equal(-1, model.Tiles[0].SelectedShapeIndex);
            Assert.False(model.IsLayerShown("holes"));

            // Still listed, to switch on again - with its count.
            Assert.Equal(["Main View (1)", "Holes (2)"], panel.Layers.Select(layer => layer.Text));

            // The next picture: still off; the toggles kept.
            var toggle = panel.Layers[1];
            model.Choose([model.Files[1]]);
            WpfTestHost.PumpUntil(() => model.Tiles.All(tile => !tile.IsLoading));
            model.CurrentTile = model.Tiles[0];

            Assert.Single(model.Tiles[0].Shapes!.Shapes);
            Assert.Same(toggle, panel.Layers[1]);
            Assert.False(toggle.IsShown);
            Assert.Equal("Holes (1)", toggle.Text);

            toggle.IsShown = true;
            Assert.Equal(2, model.Tiles[0].Shapes!.Shapes.Count);
        });
    }

    [Fact]
    public void The_annotations_of_the_pictures_chosen_or_listed_export_as_one_csv()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Open(Pictures());
            model.Choose([model.Files[0], model.Files[2]]);
            Assert.Equal(["0001_FAIL.png", "0003_PASS.png"], model.ChosenPictures().Select(file => file.Name));

            var results = Path.Combine(_folder, "out", "results.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(results)!);

            var (rows, without) = WpfTestHost.Wait(model.ExportAsync(model.ListedPictures(), perShape: false, results));
            Assert.Equal((3, 1), (rows, without));

            var lines = File.ReadAllLines(results, AnnotationCsv.Encoding);
            Assert.Equal("File,Station,Recipe,Inspection,Time,Verdict,Grade,Node Errors,Holes", lines[0]);
            Assert.Equal("0001_FAIL.png,Top,,1,,FAIL,,0,2", lines[1]);
            Assert.Equal("0003_PASS.png,,,,,PASS,,,", lines[3]);

            // With its mark, so Excel reads it as UTF-8.
            Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(results).Take(3));

            var shapes = Path.Combine(_folder, "out", "shapes.csv");
            (rows, _) = WpfTestHost.Wait(model.ExportAsync(model.ChosenPictures(), perShape: true, shapes));
            Assert.Equal(3, rows);
            Assert.Equal("0001_FAIL.png,Top,1,FAIL,1,Main View,Rectangle,body,40,30,60,40", File.ReadAllLines(shapes)[1]);

            // Nothing left beside it.
            Assert.Equal(["results.csv", "shapes.csv"], Directory.GetFiles(Path.GetDirectoryName(results)!).Select(Path.GetFileName).Order());
        });
    }

    [Fact]
    public void A_drawn_picture_keeps_its_name_and_day_folder_as_a_png()
    {
        var folder = Path.Combine(_folder, "station");
        var pictures = new[]
        {
            new ImageFileItem(Path.Combine(folder, "2026-11-22", "0001_FAIL.tif"), folder),
            new ImageFileItem(Path.Combine(folder, "2026-11-22", "0002_PASS.tif"), folder),
            new ImageFileItem(Path.Combine(folder, "2026-11-22", "0002_PASS.bmp"), folder),
        };

        var targets = BrowserViewModel.DrawnTargets(pictures, Path.Combine(_folder, "drawn"));

        Assert.Equal(
            [
                Path.Combine(_folder, "drawn", "2026-11-22", "0001_FAIL.png"),
                Path.Combine(_folder, "drawn", "2026-11-22", "0002_PASS_tif.png"),
                Path.Combine(_folder, "drawn", "2026-11-22", "0002_PASS_bmp.png"),
            ],
            targets.Select(target => target.Path));
    }

    /// <summary>
    /// Each picture written with its annotations drawn on, as Save Image with
    /// Overlays draws them: the layers switched on, and measurements drawn by
    /// hand; the picture itself left alone.
    /// </summary>
    [Fact]
    public void Pictures_are_written_with_the_layers_switched_on_and_measurements_drawn()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(Pictures());

            // A measurement drawn by hand on the first, saved with it.
            var first = model.Files[0];
            var annotations = AnnotationFile.TryLoad(first.AnnotationPath, out _)!;
            new AnnotationFile
            {
                Image = annotations.Image,
                Width = annotations.Width,
                Height = annotations.Height,
                Layers = annotations.Layers,
                Measurements = [new Sirius.Core.Graph.MeasurementRecord("PointToPoint", [new Point2(20, 85), new Point2(90, 85)], "#FFFF0000", null)],
            }.Save(first.AnnotationPath);

            model.SetLayerShown("Holes", false);

            var drawer = new ImageViewer();
            drawer.Measure(new System.Windows.Size(800, 600));
            drawer.Arrange(new System.Windows.Rect(0, 0, 800, 600));

            var output = Path.Combine(_folder, "drawn");
            var targets = BrowserViewModel.DrawnTargets(model.ListedPictures(), output);
            var result = WpfTestHost.Wait(model.ExportDrawnAsync(targets, drawer.RenderPicture));

            Assert.Equal(3, result.Written);
            Assert.Empty(result.Problems);
            Assert.Null(model.Activity);

            using var drawn = CvInvoke.Imread(Path.Combine(output, "0001_FAIL.png"), ImreadModes.ColorBgr);
            Assert.Equal((200, 100), (drawn.Width, drawn.Height));

            // The body's rectangle, outlined in its colour - the grey under it no longer grey.
            Assert.True(Coloured(drawn, 10, 30), "The Main View layer's rectangle is not drawn.");

            // The measurement, in red.
            var red = Pixel(drawn, 55, 85);
            Assert.True(red[2] > 150 && red[1] < 120, $"The measurement is not drawn red: {string.Join(",", red)}.");

            // The Holes layer, switched off: left out.
            Assert.False(Coloured(drawn, 126, 50), "A layer switched off was drawn.");

            // A picture without annotations: written as it is.
            using var plain = CvInvoke.Imread(Path.Combine(output, "0003_PASS.png"), ImreadModes.ColorBgr);
            Assert.Equal(90, CvInvoke.Mean(plain).V0, 0);

            // The pictures themselves untouched.
            Assert.False(File.Exists(Path.Combine(_folder, "0001_FAIL.sann.png")));
            using var original = CvInvoke.Imread(first.Path, ImreadModes.Unchanged);
            Assert.Equal(1, original.NumberOfChannels);
        });
    }

    /// <summary>Whether any pixel within a pixel of (x, y) is not grey.</summary>
    private static bool Coloured(Mat picture, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var pixel = Pixel(picture, x + dx, y + dy);
                if (pixel.Max() - pixel.Min() > 30 || Math.Abs(pixel[0] - 90) > 30)
                    return true;
            }
        }

        return false;
    }

    private static byte[] Pixel(Mat picture, int x, int y)
    {
        var data = new byte[3];
        System.Runtime.InteropServices.Marshal.Copy(picture.DataPointer + (y * picture.Step) + (x * 3), data, 0, 3);
        return data;
    }

    [Fact]
    public void The_window_has_the_panel_and_zooms_a_viewer_to_a_shape_double_clicked()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var model = Open(Pictures());
            var window = new ViewerWindow(model)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
            };

            try
            {
                window.Show();
                model.Choose([model.Files[0]]);
                WpfTestHost.PumpUntil(() => model.Tiles.All(tile => !tile.IsLoading));
                WpfTestHost.Settle(window.Dispatcher);

                var table = (ShapeTableControl)window.FindName("ShapeTable");
                Assert.Equal(3, model.Annotations.Shapes.Rows.Count);
                Assert.Same(model.Annotations.Shapes, table.Table);

                var viewer = Descendants(window).OfType<ImageViewer>().Single();
                var before = viewer.Scale;
                var changed = 0;
                viewer.ViewChanged += (_, _) => changed++;

                window.ZoomToShape(model.Tiles[0], 2);

                Assert.Equal(2, model.Tiles[0].SelectedShapeIndex);
                Assert.Equal(1, changed);
                Assert.True(viewer.Scale > before, $"Zoomed from {before} to {viewer.Scale}.");
            }
            finally
            {
                window.Close();
                WpfTestHost.Settle(window.Dispatcher);
            }
        });
    }

    /// <summary>
    /// A row picked in a sorted table is its shape, not the shape at its
    /// row's place: sorted, the grid's first row was the last shape, and
    /// picking it lit the first.
    /// </summary>
    [Fact]
    public void A_sorted_shape_table_picks_the_shape_on_the_row_not_at_its_place()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var table = AnnotationTable.Shapes(
            [
                new AnnotationLayer("A", [new PointShape(new Point2(1, 1)), new PointShape(new Point2(2, 2)), new PointShape(new Point2(3, 3))]),
            ]);

            var control = new ShapeTableControl { Table = table };
            var activated = new List<int>();
            control.RowActivated += (_, index) => activated.Add(index);

            var grid = (System.Windows.Controls.DataGrid)control.Content;
            grid.Items.SortDescriptions.Add(new SortDescription("[0]", ListSortDirection.Descending));

            // The first row, sorted, is the third shape.
            grid.SelectedIndex = 0;
            Assert.Equal(2, control.SelectedIndex);

            // The first shape picked elsewhere: its row, now the last.
            control.SelectedIndex = 0;
            Assert.Same(table.Rows[0], grid.SelectedItem);
            Assert.Equal(2, grid.SelectedIndex);

            // Enter on it: that shape.
            grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                new FakeSource(),
                0,
                System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent });

            Assert.Equal([0], activated);
        });
    }

    private sealed class FakeSource : System.Windows.PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var below in Descendants(child))
                yield return below;
        }
    }
}
