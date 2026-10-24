using System.IO;
using System.Windows.Media;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Colouring a node, for marking up a large recipe.
/// </summary>
/// <remarks>
/// <para>
/// A fixed set rather than two colour pickers, and the reason is contrast:
/// on a dark canvas a freely chosen background sits under a near-white title
/// the node inherits, and the result is a node nobody can read. So the set
/// itself is the thing worth testing - these tests do the arithmetic that
/// made the palette, so a colour added or tweaked later cannot quietly fall
/// below it.
/// </para>
/// <para>
/// The rest is the usual obligation of anything held on a node: it saves, it
/// counts as an unsaved change, and it does not make the graph recompute.
/// </para>
/// </remarks>
public class NodeAccentTests
{
    public NodeAccentTests() => TestCatalog.Ensure();

    /// <summary>The canvas these nodes are drawn on.</summary>
    private const string Canvas = "#1E1F22";

    private static string AppSource => Repository.AppSource;

    /// <summary>
    /// Default and Muted keep the theme's own quiet border - Default has to
    /// look exactly as it did before accents existed, and Muted is dimmed on
    /// purpose. The rest exist to be seen from across the canvas.
    /// </summary>
    private static bool IsQuiet(NodeAccent accent) =>
        accent is NodeAccent.Default or NodeAccent.Muted;

    // ---- the palette is readable -------------------------------------------

    [Fact]
    public void Every_accent_has_a_style()
    {
        foreach (var accent in Enum.GetValues<NodeAccent>())
            Assert.Equal(accent, NodeAccents.Of(accent).Accent);

        Assert.Equal(Enum.GetValues<NodeAccent>().Length, NodeAccents.All.Count);
    }

    /// <summary>
    /// Text on its own background clears 7:1 - AAA for body text - because
    /// a node title at canvas zoom is small text on a coloured field, which
    /// is the hardest case there is.
    /// </summary>
    [Fact]
    public void Every_title_is_readable_on_its_own_node()
    {
        foreach (var option in NodeAccents.All)
        {
            var contrast = Contrast(option.TextHex, option.BackgroundHex);

            Assert.True(
                contrast >= 7,
                $"{option.Name}: title contrast is {contrast:0.00}:1, below 7:1");
        }
    }

    /// <summary>
    /// The backgrounds stay dark. This is the failure the fixed set exists
    /// to prevent: a pale fill on a dark canvas, with the title still
    /// inherited onto it.
    /// </summary>
    [Fact]
    public void No_accent_is_a_pale_block_on_a_dark_canvas()
    {
        foreach (var option in NodeAccents.All)
        {
            var luminance = Luminance(option.BackgroundHex);

            Assert.True(
                luminance < 0.08,
                $"{option.Name}: background luminance {luminance:0.000} is too light for this canvas");
        }
    }

    /// <summary>
    /// The stroke is what carries the colour, so it has to read against the
    /// fill it surrounds - otherwise the accent is a dark node like every
    /// other dark node.
    /// </summary>
    [Fact]
    public void An_accents_stroke_reads_as_an_edge()
    {
        foreach (var option in NodeAccents.All.Where(o => !IsQuiet(o.Accent)))
        {
            var contrast = Contrast(option.BorderHex, option.BackgroundHex);

            Assert.True(
                contrast >= 3,
                $"{option.Name}: stroke contrast is {contrast:0.00}:1, below 3:1");
        }
    }

    /// <summary>
    /// And against the canvas as well, or a marked node would be invisible
    /// until the eye was already on it - which is the opposite of the point.
    /// </summary>
    [Fact]
    public void An_accents_stroke_reads_against_the_canvas()
    {
        foreach (var option in NodeAccents.All.Where(o => !IsQuiet(o.Accent)))
        {
            var contrast = Contrast(option.BorderHex, Canvas);

            Assert.True(
                contrast >= 3,
                $"{option.Name}: stroke against the canvas is {contrast:0.00}:1, below 3:1");
        }
    }

    /// <summary>
    /// The accents are distinguishable from each other. Two strokes a few
    /// units apart would give the operator two markers that mean different
    /// things and look the same - which is what the first Teal and Green
    /// tried here did, at a measured 60 apart.
    /// </summary>
    /// <remarks>
    /// 60 is where the bar sits because six hues spread around the wheel at
    /// one luminance cannot all be much further apart than that: Blue, Teal
    /// and Purple are mutually constrained, and the closest pair in the
    /// shipped set clears it by a little over two. Raising the bar means
    /// dropping an accent, not finding better hex.
    /// </remarks>
    [Fact]
    public void The_accents_are_told_apart()
    {
        var accents = NodeAccents.All.Where(o => !IsQuiet(o.Accent)).ToArray();

        for (var i = 0; i < accents.Length; i++)
        {
            for (var j = i + 1; j < accents.Length; j++)
            {
                var distance = Distance(accents[i].BorderHex, accents[j].BorderHex);

                Assert.True(
                    distance >= 60,
                    $"{accents[i].Name} and {accents[j].Name} are {distance:0} apart in RGB");
            }
        }
    }

    /// <summary>
    /// The default is the theme's own node, unchanged. Accents were added to
    /// an application people already use; the nodes they have must look
    /// exactly as they did.
    /// </summary>
    [Fact]
    public void The_default_is_the_theme_it_replaced()
    {
        var option = NodeAccents.Of(NodeAccent.Default);

        Assert.Equal("#32343A", option.BackgroundHex, ignoreCase: true);
        Assert.Equal("#4A4D55", option.BorderHex, ignoreCase: true);
        Assert.Equal("#E6E6E6", option.TextHex, ignoreCase: true);
    }

    /// <summary>
    /// An accent a build does not have resolves to the default rather than
    /// throwing: a node in the wrong colour beats a canvas that will not
    /// draw.
    /// </summary>
    [Fact]
    public void An_unknown_accent_falls_back()
    {
        var option = NodeAccents.Of((NodeAccent)99);

        Assert.Equal(NodeAccent.Default, option.Accent);
    }

    // ---- the shell shows it in the right places ----------------------------

    /// <summary>
    /// The canvas draws the accent. Without this binding the whole chain is
    /// dead code - the node is coloured and the graph is grey.
    /// </summary>
    [Fact]
    public void The_canvas_draws_the_accent()
    {
        var xaml = File.ReadAllText(Path.Combine(AppSource, "MainWindow.xaml"));

        Assert.Contains("Background=\"{Binding AccentStyle.Background}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{Binding AccentStyle.Border}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{Binding AccentStyle.Text}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The toolbar offers the colours, as a dropdown of swatch and name.
    /// </summary>
    [Fact]
    public void The_toolbar_offers_the_colours()
    {
        var xaml = File.ReadAllText(Path.Combine(AppSource, "MainWindow.xaml"));

        Assert.Contains("ItemsSource=\"{Binding AccentOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedAccentOption", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the properties panel does not, now that the toolbar does. Two
    /// places to set the same thing on one screen is one too many, and the
    /// toolbar's version colours a whole selection where the panel's could
    /// only ever reach the node it belonged to.
    /// </summary>
    [Fact]
    public void The_properties_panel_does_not_offer_them_as_well()
    {
        var xaml = File.ReadAllText(Path.Combine(AppSource, "MainWindow.xaml"));

        Assert.DoesNotContain("AccentChoices", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnNodeAccentClick", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both pickers draw their swatch from one template.
    /// </summary>
    /// <remarks>
    /// A <c>Setter.Value</c> holding an element hands every item the same
    /// element, and an element has one visual parent - so the last item in
    /// the list took the swatch and the other seven showed nothing. That
    /// shipped briefly and looked like seven missing icons. A template is
    /// instantiated per item, which is the fix and the reason this asserts
    /// on the template rather than on the colours.
    /// </remarks>
    [Fact]
    public void The_swatch_comes_from_a_template_not_a_shared_element()
    {
        var theme = File.ReadAllText(Path.Combine(AppSource, "Themes", "Dark.xaml"));
        var window = File.ReadAllText(Path.Combine(AppSource, "MainWindow.xaml"));

        Assert.Contains("x:Key=\"AccentItemTemplate\"", theme, StringComparison.Ordinal);
        Assert.Contains("AccentItemTemplate", window, StringComparison.Ordinal);

        // The shape of the bug: an Icon whose value is an element.
        Assert.DoesNotContain("<Setter Property=\"Icon\">", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every property the accent markup binds to exists.
    /// </summary>
    /// <remarks>
    /// A binding path is a string. A renamed property leaves the markup
    /// compiling, the build passing and the node drawn in the theme's
    /// default grey with a silent trace message nobody reads - which is
    /// indistinguishable from the feature never having been wired up.
    /// MainWindow is checked by reading its markup, as the other tests over
    /// it do: constructing it means a second AvalonDock in a process that
    /// keeps static state across them.
    /// </remarks>
    [Theory]
    [InlineData(typeof(NodeViewModel), "AccentStyle")]
    [InlineData(typeof(NodeViewModel), "AccentChoices")]
    [InlineData(typeof(NodeViewModel), "Accent")]
    [InlineData(typeof(MainViewModel), "AccentOptions")]
    [InlineData(typeof(MainViewModel), "SelectedAccentOption")]
    [InlineData(typeof(MainViewModel), "HasNodeSelection")]
    [InlineData(typeof(NodeAccentOption), "Name")]
    [InlineData(typeof(NodeAccentOption), "Background")]
    [InlineData(typeof(NodeAccentOption), "Border")]
    [InlineData(typeof(NodeAccentOption), "Text")]
    public void Every_bound_property_exists(Type type, string property) =>
        Assert.NotNull(type.GetProperty(property));

    // ---- it is part of the recipe ------------------------------------------

    [Fact]
    public void An_accent_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        var node = new ThresholdNode { Accent = NodeAccent.Amber };
        graph.Add(node);

        var result = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Empty(result.Warnings);
        Assert.Equal(NodeAccent.Amber, Assert.Single(result.Graph.Nodes).Accent);
    }

    /// <summary>
    /// An unmarked recipe says nothing about colour, so a file written by
    /// this build still opens in one that predates accents, and a diff of an
    /// untouched recipe stays empty.
    /// </summary>
    [Fact]
    public void An_unmarked_recipe_says_nothing_about_colour()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode());

        Assert.DoesNotContain("accent", GraphSerializer.Serialize(graph), StringComparison.Ordinal);
    }

    /// <summary>
    /// Written as a name, not an ordinal, so inserting a colour into the
    /// middle of the set does not recolour every saved recipe.
    /// </summary>
    [Fact]
    public void An_accent_is_written_as_a_name()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode { Accent = NodeAccent.Teal });

        Assert.Contains("\"accent\": \"Teal\"", GraphSerializer.Serialize(graph), StringComparison.Ordinal);
    }

    /// <summary>
    /// A colour from a newer build is reported and dropped. Failing a whole
    /// load over an annotation would be absurd.
    /// </summary>
    [Fact]
    public void A_colour_this_build_lacks_is_reported_not_fatal()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode());

        var document = GraphSerializer.ToDocument(graph);
        document.Nodes[0].Accent = "Chartreuse";

        var result = GraphSerializer.FromDocument(document);

        Assert.Equal(NodeAccent.Default, Assert.Single(result.Graph.Nodes).Accent);
        Assert.Single(result.Warnings);
    }

    /// <summary>
    /// Marking up a graph is work, so losing it without asking would be
    /// losing work.
    /// </summary>
    [Fact]
    public void Colouring_a_node_counts_as_a_change()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-accent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            OnStaThread(() =>
            {
                var model = new MainViewModel();
                var item = model.Palette
                    .SelectMany(group => group.Items)
                    .First(entry => entry.DisplayName == "Threshold");
                var node = model.AddNodeAt(item, new System.Windows.Point(40, 40))!.Node;

                model.Write(Path.Combine(folder, "recipe.sgraph"));
                Assert.False(model.IsModified);

                node.Accent = NodeAccent.Red;

                Assert.True(model.IsModified);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// And does not make anything recompute. A colour says nothing about
    /// what the recipe computes.
    /// </summary>
    [Fact]
    public void Colouring_a_node_does_not_invalidate()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 10 };
        var node = new ThresholdNode();
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.ImageOut, node.ImageIn);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;
        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (scheduler)
        {
            scheduler.RunAsync().GetAwaiter().GetResult();
            Assert.Equal(0, scheduler.DirtyCount);

            node.Accent = NodeAccent.Green;

            Assert.Equal(0, scheduler.DirtyCount);
        }
    }

    // ---- what the canvas and the menu are told ------------------------------

    [Fact]
    public void The_view_model_follows_the_node()
    {
        var node = new ThresholdNode();
        var view = new NodeViewModel(node);
        var raised = new List<string>();

        view.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        node.Accent = NodeAccent.Purple;

        Assert.Equal(NodeAccent.Purple, view.Accent);
        Assert.Equal("#352D45", view.AccentStyle.BackgroundHex, ignoreCase: true);
        Assert.Contains(nameof(NodeViewModel.AccentStyle), raised);
        Assert.Contains(nameof(NodeViewModel.AccentChoices), raised);
    }

    /// <summary>
    /// Exactly one choice is marked, and it is the one in use - so the
    /// canvas menu shows which colour this node has rather than which was
    /// picked last.
    /// </summary>
    [Fact]
    public void One_choice_is_marked_as_current()
    {
        var node = new ThresholdNode { Accent = NodeAccent.Blue };
        var view = new NodeViewModel(node);

        var marked = Assert.Single(view.AccentChoices.Where(choice => choice.IsCurrent));

        Assert.Equal(NodeAccent.Blue, marked.Accent);

        node.Accent = NodeAccent.Green;

        Assert.Equal(
            NodeAccent.Green,
            Assert.Single(view.AccentChoices.Where(choice => choice.IsCurrent)).Accent);
    }

    /// <summary>
    /// Two nodes are marked independently. The options are shared, so a mark
    /// baked into them would make the last node looked at decide what every
    /// other node showed.
    /// </summary>
    [Fact]
    public void Two_nodes_are_marked_independently()
    {
        var first = new NodeViewModel(new ThresholdNode { Accent = NodeAccent.Red });
        var second = new NodeViewModel(new ThresholdNode { Accent = NodeAccent.Teal });

        Assert.Equal(
            NodeAccent.Red,
            Assert.Single(first.AccentChoices.Where(choice => choice.IsCurrent)).Accent);
        Assert.Equal(
            NodeAccent.Teal,
            Assert.Single(second.AccentChoices.Where(choice => choice.IsCurrent)).Accent);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance(string hex)
    {
        var colour = (Color)ColorConverter.ConvertFromString(hex)!;

        return 0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

        static double Linear(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    private static double Contrast(string a, string b)
    {
        var (first, second) = (Luminance(a), Luminance(b));
        var (high, low) = first > second ? (first, second) : (second, first);

        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>
    /// Straight-line distance in RGB. Crude next to a perceptual metric, but
    /// what it is being asked is only "are these two obviously different
    /// colours", and for saturated strokes it answers that.
    /// </summary>
    private static double Distance(string a, string b)
    {
        var first = (Color)ColorConverter.ConvertFromString(a)!;
        var second = (Color)ColorConverter.ConvertFromString(b)!;

        var dr = first.R - second.R;
        var dg = first.G - second.G;
        var db = first.B - second.B;

        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
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
