using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Where a result's own label is drawn, and what it looks like.
/// </summary>
/// <remarks>
/// A reading produced by the graph - a Measure node's distance, say - and
/// one a person placed with the measuring tools are the same kind of thing
/// to whoever is reading the screen, so they are drawn by the same code.
/// They were not: a measurement was boxed and sat on its midpoint, while a
/// shape's label was bare text pinned to the corner of its bounding box.
/// </remarks>
[Collection(WpfCollection.Name)]
public class ShapeLabelTests
{
    public ShapeLabelTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static ImageViewer Laid(params Shape[] shapes)
    {
        WpfTestHost.EnsureApplicationResources();

        var viewer = new ImageViewer
        {
            ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                400, 400, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
            Shapes = new ShapeSet(shapes, 400, 400),
        };

        viewer.Measure(new Size(500, 500));
        viewer.Arrange(new Rect(0, 0, 500, 500));
        viewer.UpdateLayout();
        viewer.FitToWindow();

        return viewer;
    }

    private static System.Collections.IList Drawn(ImageViewer viewer) =>
        (System.Collections.IList)typeof(ImageViewer)
            .GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

    private static void Invoke(ImageViewer viewer, string method) =>
        typeof(ImageViewer)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, null);

    private static Canvas Layer(ImageViewer viewer, string name) =>
        (Canvas)viewer.FindName(name);

    private static Border TheLabel(ImageViewer viewer) =>
        Assert.Single(Layer(viewer, "ShapeLayer").Children.OfType<Border>());

    /// <summary>
    /// The point a label was placed from, in image coordinates.
    /// </summary>
    /// <remarks>
    /// A label is set down a short distance clear of its anchor so it does
    /// not cover the geometry, and that clearance is in screen pixels so it
    /// does not grow with the zoom. Undoing it here lets a test say where
    /// the label belongs without also restating how far off it sits.
    /// </remarks>
    private static Point AnchorOf(ImageViewer viewer, Border label)
    {
        var pixel = 1 / ((MatrixTransform)viewer.FindName("SceneTransform")).Matrix.M11;

        return new Point(
            Canvas.GetLeft(label) - (8 * pixel),
            Canvas.GetTop(label) + (20 * pixel));
    }

    /// <summary>
    /// A diagonal line's bounding box has two corners the line never
    /// reaches. The label used to sit at one of them, which on a long
    /// diagonal put the reading a long way from the thing it measured.
    /// </summary>
    [Fact]
    public void A_diagonal_line_is_labelled_at_its_midpoint() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new LineShape(new Point2(50, 50), new Point2(250, 350))
            {
                Label = "123.45 px",
            });

            var anchor = AnchorOf(viewer, TheLabel(viewer));

            Assert.Equal(150, anchor.X, 6);
            Assert.Equal(200, anchor.Y, 6);
        });

    [Fact]
    public void A_circle_is_labelled_at_its_centre() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new CircleShape(new Point2(120, 80), 40) { Label = "R 40" });

            var anchor = AnchorOf(viewer, TheLabel(viewer));

            Assert.Equal(120, anchor.X, 6);
            Assert.Equal(80, anchor.Y, 6);
        });

    /// <summary>
    /// A blob's published centroid beats the middle of its outline, the
    /// same way a measurement from that blob takes it.
    /// </summary>
    [Fact]
    public void It_follows_a_published_centroid() =>
        WpfTestHost.OnStaThread(() =>
        {
            var lopsided = new PolygonShape(
            [
                new Point2(0, 0), new Point2(200, 0), new Point2(200, 20), new Point2(0, 20),
            ])
            {
                Label = "1",
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = 30,
                    [ShapeMetric.CentroidY] = 10,
                },
            };

            var viewer = Laid(lopsided);
            var anchor = AnchorOf(viewer, TheLabel(viewer));

            Assert.Equal(30, anchor.X, 6);
            Assert.Equal(10, anchor.Y, 6);
        });

    // ---- how it reads ----------------------------------------------------

    /// <summary>
    /// Boxed, so a reading stays legible over whatever it lands on - the
    /// bare text was unreadable over a bright blob, which is exactly where
    /// a label about that blob belongs.
    /// </summary>
    [Fact]
    public void The_reading_is_boxed_against_the_image() =>
        WpfTestHost.OnStaThread(() =>
        {
            var label = TheLabel(Laid(new CircleShape(new Point2(120, 80), 40) { Label = "R 40" }));

            Assert.NotNull(label.Background);
            Assert.NotNull(label.BorderBrush);
            Assert.True(label.BorderThickness.Left > 0);
        });

    /// <summary>
    /// The same box a measurement gets. Compared rather than asserted
    /// literally: the point is that the two agree, not what they agree on.
    /// </summary>
    [Fact]
    public void It_matches_a_measurement_drawn_by_hand() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new LineShape(new Point2(50, 50), new Point2(250, 350))
            {
                Label = "123.45 px",
            });

            Drawn(viewer).Add(new Measurement(
                ViewerTool.PointToPoint,
                [new Point(10, 10), new Point(90, 10)],
                System.Windows.Media.Colors.Gold));

            Invoke(viewer, "RedrawMeasurements");

            var byHand = Assert.Single(Layer(viewer, "MeasureLayer").Children.OfType<Border>());
            var byGraph = TheLabel(viewer);

            Assert.Equal(byHand.CornerRadius, byGraph.CornerRadius);
            Assert.Equal(byHand.Padding, byGraph.Padding);
            Assert.Equal(byHand.BorderThickness, byGraph.BorderThickness);
            Assert.Equal(
                ((System.Windows.Media.SolidColorBrush)byHand.Background).Color,
                ((System.Windows.Media.SolidColorBrush)byGraph.Background).Color);

            var handText = Assert.IsType<TextBlock>(byHand.Child);
            var graphText = Assert.IsType<TextBlock>(byGraph.Child);

            Assert.Equal(handText.FontFamily, graphText.FontFamily);
            Assert.Equal(handText.FontSize, graphText.FontSize);
        });
}
