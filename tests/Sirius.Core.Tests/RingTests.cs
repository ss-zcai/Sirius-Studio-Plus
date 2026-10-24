using System.IO;
using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The annular search band, and drawing one.
/// </summary>
/// <remarks>
/// Four numbers became one value for the reason the region of interest's
/// five did: it is a single decision, drawn in a single gesture, and four
/// separate parameters are four things that can be edited into a state
/// that is not a ring.
/// </remarks>
public class RingTests
{
    public RingTests() => TestCatalog.Ensure();

    // ---- the value ----------------------------------------------------------

    [Fact]
    public void It_orders_its_radii()
    {
        var ring = new Ring(10, 20, 140, 80);

        Assert.Equal(80, ring.InnerRadius);
        Assert.Equal(140, ring.OuterRadius);
    }

    /// <summary>
    /// A band of no width can be typed but cannot be scanned, so it is
    /// widened rather than left to produce "no edge found" from a tool
    /// that looks correctly configured.
    /// </summary>
    [Fact]
    public void A_band_of_no_width_is_widened()
    {
        var ring = new Ring(0, 0, 50, 50);

        Assert.True(ring.Width > 0);
        Assert.Equal(50, ring.InnerRadius);
    }

    [Fact]
    public void Negative_radii_are_refused()
    {
        var ring = new Ring(0, 0, -30, 40);

        Assert.True(ring.InnerRadius >= 0);
        Assert.True(ring.OuterRadius >= ring.InnerRadius);
    }

    [Fact]
    public void Mean_and_half_width_describe_the_same_band()
    {
        var ring = new Ring(5, 6, 80, 140);

        Assert.Equal(110, ring.MeanRadius);
        Assert.Equal(30, ring.HalfWidth);
        Assert.Equal(60, ring.Width);
    }

    /// <summary>
    /// The form the drawing gesture produces - a centre, the edge, and how
    /// far either side - is the same band.
    /// </summary>
    [Fact]
    public void FromMean_round_trips()
    {
        var ring = Ring.FromMean(100, 200, 110, 30);

        Assert.Equal(100, ring.CentreX);
        Assert.Equal(200, ring.CentreY);
        Assert.Equal(80, ring.InnerRadius);
        Assert.Equal(140, ring.OuterRadius);
    }

    [Theory]
    [InlineData("500, 500, 80, 140")]
    [InlineData("500,500,80,140")]
    [InlineData("  500 500 80 140 ")]
    public void It_reads_what_people_type(string text)
    {
        Assert.True(Ring.TryParse(text, out var ring));

        Assert.Equal(500, ring.CentreX);
        Assert.Equal(80, ring.InnerRadius);
        Assert.Equal(140, ring.OuterRadius);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("500, 500, 80")]
    [InlineData("500, 500, 80, 140, 7")]
    [InlineData("500, 500, 80, banana")]
    public void Nonsense_is_refused(string? text) => Assert.False(Ring.TryParse(text, out _));

    [Fact]
    public void It_round_trips_through_its_own_text()
    {
        var original = new Ring(500.5, 499.25, 80, 140);

        Assert.True(Ring.TryParse(original.ToString(), out var again));
        Assert.Equal(original, again);
    }

    [Fact]
    public void Contains_is_the_band_not_the_disc()
    {
        var ring = new Ring(0, 0, 80, 140);

        Assert.True(ring.Contains(100, 0));
        Assert.False(ring.Contains(10, 0));     // in the hole
        Assert.False(ring.Contains(200, 0));    // outside
    }

    // ---- the gesture --------------------------------------------------------

    /// <summary>
    /// Three clicks: the centre, a point on the edge, then a point giving
    /// the width either side of that edge.
    /// </summary>
    /// <remarks>
    /// This arithmetic is what the viewer does with the clicks, asserted
    /// here rather than through the control so it can be checked without a
    /// mouse. The gesture plumbing itself is checked below.
    /// </remarks>
    [Fact]
    public void Three_clicks_describe_a_band()
    {
        var centre = new Point(200, 200);
        var onEdge = new Point(300, 200);       // radius 100
        var width = new Point(325, 200);        // 25 outside the edge

        var mean = (onEdge - centre).Length;
        var half = Math.Abs((width - centre).Length - mean);
        var ring = Ring.FromMean(centre.X, centre.Y, mean, half);

        Assert.Equal(75, ring.InnerRadius);
        Assert.Equal(125, ring.OuterRadius);
    }

    /// <summary>
    /// The third click works on either side: clicking inside the edge
    /// gives the same band as clicking the same distance outside it, which
    /// is what "either side" means and saves the operator thinking about
    /// which.
    /// </summary>
    [Fact]
    public void The_third_click_works_on_either_side()
    {
        var centre = new Point(200, 200);
        var mean = 100.0;

        var outward = Ring.FromMean(centre.X, centre.Y, mean, Math.Abs(125 - mean));
        var inward = Ring.FromMean(centre.X, centre.Y, mean, Math.Abs(75 - mean));

        Assert.Equal(outward, inward);
    }

    [Fact]
    public void The_gesture_takes_three_clicks() => Assert.Equal(3, ImageViewer.RingClicks);

    // ---- the node -----------------------------------------------------------

    [Fact]
    public void Find_circle_offers_a_band_to_draw()
    {
        var node = new FindCircleNode();

        var band = node.Descriptor.Parameters.Single(p => p.ValueType == typeof(Ring));

        Assert.Equal(ParamEditor.Ring, band.Editor);
        Assert.True(band.Attribute.Connectable);
    }

    [Fact]
    public void A_band_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        graph.Add(new FindCircleNode { Band = new Ring(123, 456, 70, 110) });

        var result = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));
        var restored = (FindCircleNode)result.Graph.Nodes.Single();

        Assert.Empty(result.Warnings);
        Assert.Equal(new Ring(123, 456, 70, 110), restored.Band);
    }

    /// <summary>
    /// A recipe saved while the node had four separate numbers keeps its
    /// band rather than reverting to the default.
    /// </summary>
    [Fact]
    public void An_old_recipe_keeps_its_band()
    {
        var graph = new VisionGraph();
        graph.Add(new FindCircleNode());

        var document = GraphSerializer.ToDocument(graph);
        var record = document.Nodes[0];

        record.Parameters.Remove("Band");
        record.Parameters["CentreX"] = System.Text.Json.JsonSerializer.SerializeToElement(321.0);
        record.Parameters["CentreY"] = System.Text.Json.JsonSerializer.SerializeToElement(654.0);
        record.Parameters["InnerRadius"] = System.Text.Json.JsonSerializer.SerializeToElement(60.0);
        record.Parameters["OuterRadius"] = System.Text.Json.JsonSerializer.SerializeToElement(90.0);

        var restored = (FindCircleNode)GraphSerializer.FromDocument(document).Graph.Nodes.Single();

        Assert.Equal(new Ring(321, 654, 60, 90), restored.Band);
    }

    // ---- the shell ------------------------------------------------------------

    /// <summary>
    /// Selecting a Find Circle offers its band to every viewer, so it can
    /// be drawn on whichever panel shows the useful image.
    /// </summary>
    [Fact]
    public void Selecting_the_node_offers_its_band_to_the_viewers()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var item = model.Palette
                .SelectMany(group => group.Items)
                .First(entry => entry.DisplayName == "Find Circle");

            model.AddNodeAt(item, new Point(40, 40));

            Assert.NotNull(model.ActiveRingParameter);
            Assert.Equal(ParamEditor.Ring, model.ActiveRingParameter!.Kind);
        });
    }

    /// <summary>
    /// The editor row and the draw button exist, and the viewer binds the
    /// band - the control having the property is no use if nothing fills
    /// it.
    /// </summary>
    [Fact]
    public void The_band_is_wired_from_panel_to_viewer()
    {
        var window = File.ReadAllText(Path.Combine(Repository.AppSource, "MainWindow.xaml"));
        var theme = File.ReadAllText(Path.Combine(Repository.AppSource, "Themes", "Dark.xaml"));

        Assert.Contains("{Binding RingText", window, StringComparison.Ordinal);
        Assert.Contains("IsRing, Converter", window, StringComparison.Ordinal);

        // Both templates: a viewer panel and a preview.
        Assert.Equal(2, theme.Split("Ring=\"{Binding Ring, Mode=TwoWay}\"").Length - 1);
        Assert.Equal(2, theme.Split("IsRingEnabled=\"{Binding IsRingEnabled}\"").Length - 1);
    }

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
