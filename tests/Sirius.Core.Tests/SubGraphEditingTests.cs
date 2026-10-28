using System.IO;
using System.Windows;
using AvalonDock;
using AvalonDock.Layout;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Editing inside a sub-graph: its tab, what follows the tab, and the node
/// outside keeping up with what is done inside.
/// </summary>
[Collection(WpfCollection.Name)]
public class SubGraphEditingTests
{
    public SubGraphEditingTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    /// <summary>A shell that loads no custom node libraries of the real user's.</summary>
    private static MainViewModel Model()
    {
        var folder = CustomNodeTests.Scratch();

        return new MainViewModel(new CustomNodesViewModel(
            Path.Combine(folder, "custom-nodes.json"),
            Path.Combine(folder, "shipped"),
            Path.Combine(folder, "user")));
    }

    private static NodeDescriptor Type(string typeKey) => NodeCatalog.FindByTypeKey(typeKey)!;

    private static SubGraphNode AddSubGraph(MainViewModel model) =>
        (SubGraphNode)model.MainDocument.Editor.AddNode(Type("subgraph.graph"), new Point(0, 0)).Node;

    // ---- the tab ----------------------------------------------------------------

    [Fact]
    public void Opening_a_sub_graph_makes_its_tab_the_one_worked_in()
    {
        var model = Model();
        var sub = AddSubGraph(model);

        var document = model.OpenSubGraph(sub);

        Assert.Same(document, model.ActiveDocument);
        Assert.Same(document.Editor, model.Editor);
        Assert.Contains(document, model.OpenSubGraphs);

        // One way in and one way out, already there.
        Assert.Equal(2, model.Editor.Nodes.Count);
    }

    [Fact]
    public void Opening_it_again_finds_the_same_tab()
    {
        var model = Model();
        var sub = AddSubGraph(model);

        var first = model.OpenSubGraph(sub);
        model.ActivateDocument(model.MainDocument);
        var second = model.OpenSubGraph(sub);

        Assert.Same(first, second);
        Assert.Single(model.OpenSubGraphs);
    }

    [Fact]
    public void Closing_the_active_tab_returns_to_the_recipe()
    {
        var model = Model();
        var document = model.OpenSubGraph(AddSubGraph(model));

        model.CloseSubGraph(document);

        Assert.Same(model.MainDocument, model.ActiveDocument);
        Assert.Empty(model.OpenSubGraphs);
    }

    [Fact]
    public void The_tab_is_named_after_the_sub_graph_and_follows_a_rename()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        var document = model.OpenSubGraph(sub);

        sub.Name = "Measure Hole";

        Assert.Equal("Measure Hole", document.Title);
    }

    [Fact]
    public void Deleting_a_sub_graph_closes_its_tab()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        model.OpenSubGraph(sub);
        model.ActivateDocument(model.MainDocument);

        var node = model.Editor.Nodes.Single(candidate => candidate.Node == sub);
        model.Editor.RemoveNode(node);

        Assert.Empty(model.OpenSubGraphs);
        Assert.Same(model.MainDocument, model.ActiveDocument);
    }

    // ---- what follows the tab -------------------------------------------------------

    /// <summary>
    /// A Graph Input in the recipe itself would do nothing, so the toolbox
    /// offers the boundaries only while a sub-graph is being edited.
    /// </summary>
    [Fact]
    public void Boundaries_are_offered_only_inside_a_sub_graph()
    {
        var model = Model();
        var sub = AddSubGraph(model);

        Assert.DoesNotContain(Offered(model), name => name == "Graph Input");

        model.OpenSubGraph(sub);
        Assert.Contains("Graph Input", Offered(model));
        Assert.Contains("Graph Output", Offered(model));

        model.ActivateDocument(model.MainDocument);
        Assert.DoesNotContain(Offered(model), name => name == "Graph Output");
    }

    /// <summary>
    /// Each tab has its own history, as two documents do. Undo in the
    /// sub-graph's tab undoes what was done there, and leaves the recipe's
    /// history alone.
    /// </summary>
    [Fact]
    public void Undo_in_a_sub_graph_undoes_what_was_done_there()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        var document = model.OpenSubGraph(sub);

        model.AddNodeAt(document, new PaletteItemViewModel(Type("basic.threshold")), new Point(200, 60));
        Assert.Equal(3, sub.Inner.Nodes.Count);

        model.UndoCommand.Execute(null);

        Assert.Equal(2, sub.Inner.Nodes.Count);
        Assert.Contains(sub, model.MainDocument.Graph.Nodes);

        model.ActivateDocument(model.MainDocument);
        Assert.Contains("Sub Graph", model.UndoText);
    }

    [Fact]
    public void The_properties_panel_follows_the_tab()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        var document = model.OpenSubGraph(sub);

        var threshold = model.AddNodeAt(document, new PaletteItemViewModel(Type("basic.threshold")), new Point(200, 60))!;

        Assert.Same(threshold, model.Editor.SelectedNode);

        model.ActivateDocument(model.MainDocument);
        Assert.Same(sub, model.Editor.SelectedNode?.Node);
    }

    // ---- the node outside keeping up ------------------------------------------------

    [Fact]
    public void Adding_a_boundary_inside_grows_the_node_on_the_recipes_canvas()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        var document = model.OpenSubGraph(sub);

        model.AddNodeAt(document, new PaletteItemViewModel(Type("subgraph.input")), new Point(0, 200));

        var outside = model.MainDocument.Editor.Nodes.Single(node => node.Node == sub);
        Assert.Equal(2, outside.Inputs.Count);
    }

    /// <summary>
    /// Wiring a boundary retypes it while the wire is still being added to
    /// the canvas. Rebuilding the canvas at that moment drew the wire twice;
    /// the rebuild waits for the gesture to finish.
    /// </summary>
    [Fact]
    public void Wiring_a_boundary_inside_types_it_and_draws_one_wire()
    {
        var model = Model();
        var sub = AddSubGraph(model);
        var document = model.OpenSubGraph(sub);
        var editor = document.Editor;

        var threshold = model.AddNodeAt(document, new PaletteItemViewModel(Type("basic.threshold")), new Point(200, 60))!;
        var boundary = editor.Nodes.Single(node => node.Node is GraphInputNode);

        editor.CompleteConnectionCommand.Execute((boundary.Outputs[0], threshold.Inputs[0]));

        Assert.Single(editor.Connections);
        Assert.Equal(typeof(ImageData), sub.Inputs.Single().DataType);

        // Both canvases show the new type, having read the node again.
        Assert.Equal("Image", editor.Nodes.Single(node => node.Node is GraphInputNode).Outputs[0].TypeDisplayName);

        var outside = model.MainDocument.Editor.Nodes.Single(node => node.Node == sub);
        Assert.Equal("Image", outside.Inputs[0].TypeDisplayName);
    }

    // ---- in the window --------------------------------------------------------------

    [Fact]
    public void A_sub_graph_opens_as_a_tab_beside_the_recipe_and_closes_with_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            var docking = (DockingManager)window.FindName("Docking");

            var sub = AddSubGraph(model);
            var document = model.OpenSubGraph(sub);

            var tab = docking.Layout.Descendents().OfType<LayoutDocument>()
                .Single(candidate => candidate.ContentId == document.ContentId);

            Assert.Equal(sub.Name, tab.Title);
            Assert.Same(
                docking.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "graph").Parent,
                tab.Parent);

            model.CloseSubGraph(document);

            Assert.DoesNotContain(
                docking.Layout.Descendents().OfType<LayoutDocument>(),
                candidate => candidate.ContentId == document.ContentId);
        });

    [Fact]
    public void The_recipes_tab_shows_the_recipes_document() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            var host = (System.Windows.Controls.ContentControl)window.FindName("MainGraphHost");

            Assert.Same(model.MainDocument, host.Content);
            Assert.NotNull(host.ContentTemplate);
        });

    private static IEnumerable<string> Offered(MainViewModel model) =>
        model.Palette.SelectMany(group => group.Items).Select(item => item.DisplayName);
}
