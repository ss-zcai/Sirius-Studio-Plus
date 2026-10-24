using System.Windows.Controls;
using AvalonDock.Layout;
using Sirius.Studio.App.Layout;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Placement rules for node panels in the docking layout.
/// </summary>
/// <remarks>
/// These exercise the layout model directly - no DockingManager and no window -
/// which is why the behaviour that broke panel creation can be pinned down here
/// rather than only being visible by clicking around the running app.
/// </remarks>
public class PanelLayoutTests
{
    /// <summary>Toolbox on the left, graph in the middle, properties right.</summary>
    private static (LayoutRoot Root, LayoutDocumentPane Documents) BuildShell(bool wrapGraphVertically)
    {
        var documents = new LayoutDocumentPane(
            new LayoutDocument { Title = "Graph", ContentId = "graph" });

        var horizontal = new LayoutPanel { Orientation = Orientation.Horizontal };
        horizontal.Children.Add(new LayoutAnchorablePane(
            new LayoutAnchorable { Title = "Toolbox", ContentId = "toolbox" }));

        if (wrapGraphVertically)
        {
            var vertical = new LayoutPanel { Orientation = Orientation.Vertical };
            vertical.Children.Add(documents);
            horizontal.Children.Add(vertical);
        }
        else
        {
            horizontal.Children.Add(documents);
        }

        horizontal.Children.Add(new LayoutAnchorablePane(
            new LayoutAnchorable { Title = "Properties", ContentId = "properties" }));

        return (new LayoutRoot { RootPanel = horizontal }, documents);
    }

    private static LayoutAnchorable Panel(string contentId) =>
        new() { Title = contentId, ContentId = contentId };

    private static bool IsInLayout(LayoutRoot root, LayoutAnchorable anchorable) =>
        root.Descendents().OfType<LayoutAnchorable>().Contains(anchorable);

    /// <summary>
    /// The regression: attaching a pane to the layout before putting the panel
    /// in it leaves the pane empty at that moment, and AvalonDock prunes it -
    /// so the panel silently never appears.
    /// </summary>
    [Fact]
    public void An_attached_panel_is_reachable_from_the_layout()
    {
        var (root, _) = BuildShell(wrapGraphVertically: true);
        var panel = Panel("viewer:abc");

        PanelLayout.Attach(root, panel);

        Assert.True(IsInLayout(root, panel), "the panel was not reachable from the layout root");
        Assert.NotNull(panel.Parent);
    }

    [Fact]
    public void A_panel_is_placed_below_the_graph_not_beside_it()
    {
        var (root, documents) = BuildShell(wrapGraphVertically: true);
        var panel = Panel("viewer:abc");

        PanelLayout.Attach(root, panel);

        var pane = Assert.IsType<LayoutAnchorablePane>(panel.Parent);
        var parent = Assert.IsType<LayoutPanel>(pane.Parent);

        Assert.Equal(Orientation.Vertical, parent.Orientation);
        Assert.True(
            parent.IndexOfChild(pane) > parent.IndexOfChild(documents),
            "the panel pane should come after the graph, so it sits underneath");
    }

    /// <summary>
    /// AvalonDock collapses a panel holding a single child, so after a layout
    /// restore the graph often sits directly in the horizontal root. Inserting
    /// there would put panels in a column beside the toolbox.
    /// </summary>
    [Fact]
    public void A_graph_in_a_horizontal_parent_is_wrapped_so_the_panel_lands_below()
    {
        var (root, documents) = BuildShell(wrapGraphVertically: false);
        var panel = Panel("viewer:abc");

        PanelLayout.Attach(root, panel);

        var pane = Assert.IsType<LayoutAnchorablePane>(panel.Parent);
        var parent = Assert.IsType<LayoutPanel>(pane.Parent);

        Assert.Equal(Orientation.Vertical, parent.Orientation);
        Assert.Same(parent, documents.Parent);
        Assert.True(parent.IndexOfChild(pane) > parent.IndexOfChild(documents));
        Assert.True(IsInLayout(root, panel));
    }

    /// <summary>
    /// The structure a layout restore produces, and the one that actually
    /// broke panel creation.
    /// </summary>
    /// <remarks>
    /// AvalonDock wraps the document pane in a LayoutDocumentPaneGroup when it
    /// rebuilds a saved layout. That group accepts only document panes, so
    /// inserting an anchorable pane into it is rejected silently and the panel
    /// ends up attached to nothing.
    /// </remarks>
    [Fact]
    public void A_panel_is_placed_below_a_graph_wrapped_in_a_document_pane_group()
    {
        var documents = new LayoutDocumentPane(
            new LayoutDocument { Title = "Graph", ContentId = "graph" });

        var documentGroup = new LayoutDocumentPaneGroup(documents);

        var vertical = new LayoutPanel { Orientation = Orientation.Vertical };
        vertical.Children.Add(documentGroup);

        var horizontal = new LayoutPanel { Orientation = Orientation.Horizontal };
        horizontal.Children.Add(new LayoutAnchorablePane(
            new LayoutAnchorable { Title = "Toolbox", ContentId = "toolbox" }));
        horizontal.Children.Add(vertical);

        var root = new LayoutRoot { RootPanel = horizontal };
        var panel = Panel("viewer:abc");

        PanelLayout.Attach(root, panel);

        Assert.True(IsInLayout(root, panel), "the panel was orphaned inside a document pane group");

        var pane = Assert.IsType<LayoutAnchorablePane>(panel.Parent);
        var parent = Assert.IsType<LayoutPanel>(pane.Parent);
        Assert.Equal(Orientation.Vertical, parent.Orientation);
        Assert.True(
            parent.IndexOfChild(pane) > parent.IndexOfChild(documentGroup),
            "the panel pane should sit below the document group, not above it");
    }

    [Fact]
    public void A_second_panel_joins_the_first_rather_than_making_another_pane()
    {
        var (root, _) = BuildShell(wrapGraphVertically: true);
        var first = Panel("viewer:abc");
        var second = Panel("histogram:def");

        PanelLayout.Attach(root, first);
        PanelLayout.Attach(root, second);

        Assert.Same(first.Parent, second.Parent);
        Assert.True(IsInLayout(root, second));
    }

    /// <summary>
    /// Deleting the last panel and adding another is the sequence that failed:
    /// the pane is gone by then, so a new one has to be built correctly.
    /// </summary>
    [Fact]
    public void A_panel_added_after_the_last_one_closed_is_still_reachable()
    {
        var (root, documents) = BuildShell(wrapGraphVertically: true);

        var first = Panel("viewer:abc");
        PanelLayout.Attach(root, first);
        first.Close();

        Assert.False(IsInLayout(root, first));

        var second = Panel("viewer:xyz");
        PanelLayout.Attach(root, second);

        Assert.True(IsInLayout(root, second), "a panel added after the last one closed went missing");

        var pane = Assert.IsType<LayoutAnchorablePane>(second.Parent);
        var parent = Assert.IsType<LayoutPanel>(pane.Parent);
        Assert.Equal(Orientation.Vertical, parent.Orientation);
        Assert.True(parent.IndexOfChild(pane) > parent.IndexOfChild(documents));
    }

    [Fact]
    public void Panel_ids_are_distinguished_from_fixed_panels()
    {
        Assert.True(PanelLayout.IsPanelId("viewer:abc"));
        Assert.True(PanelLayout.IsPanelId("histogram:def"));
        Assert.False(PanelLayout.IsPanelId("toolbox"));
        Assert.False(PanelLayout.IsPanelId("properties"));
        Assert.False(PanelLayout.IsPanelId(null));
    }

    /// <summary>
    /// Runs on an STA thread, which creating a DockingManager requires.
    /// </summary>
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
            throw failure;
    }

    /// <summary>
    /// The same placement, but with a live DockingManager attached.
    /// </summary>
    /// <remarks>
    /// AvalonDock only tidies the tree - collapsing panels, dropping empty
    /// panes - once a manager owns the layout. A detached LayoutRoot never
    /// does, so a test without one cannot tell a correct placement from one
    /// that is about to be discarded.
    /// </remarks>
    /// <summary>
    /// The case that actually broke: the graph sitting directly in the
    /// horizontal root, with a live manager.
    /// </summary>
    /// <remarks>
    /// Wrapping it requires inserting a new vertical panel. Inserting that
    /// panel while empty gets it pruned immediately, so the pane - and the
    /// panel inside it - end up orphaned, reachable from nothing.
    /// </remarks>
    [Fact]
    public void A_panel_survives_wrapping_a_horizontal_graph_under_a_live_manager()
    {
        OnStaThread(() =>
        {
            var (root, documents) = BuildShell(wrapGraphVertically: false);
            var manager = new AvalonDock.DockingManager { Layout = root };

            var panel = Panel("viewer:abc");
            PanelLayout.Attach(manager.Layout, panel);

            Assert.True(
                IsInLayout(manager.Layout, panel),
                "the panel was orphaned while wrapping a horizontally parented graph");

            // The graph must survive the wrap too, and stay above the panels.
            Assert.Contains(documents, manager.Layout.Descendents().OfType<LayoutDocumentPane>());

            var pane = Assert.IsType<LayoutAnchorablePane>(panel.Parent);
            var parent = Assert.IsType<LayoutPanel>(pane.Parent);
            Assert.Equal(Orientation.Vertical, parent.Orientation);
            Assert.True(parent.IndexOfChild(pane) > parent.IndexOfChild(documents));
        });
    }

    [Fact]
    public void A_panel_survives_attachment_under_a_live_docking_manager()
    {
        OnStaThread(() =>
        {
            var (root, _) = BuildShell(wrapGraphVertically: true);
            var manager = new AvalonDock.DockingManager { Layout = root };

            var first = Panel("viewer:abc");
            PanelLayout.Attach(manager.Layout, first);
            Assert.True(IsInLayout(manager.Layout, first), "first panel went missing under a live manager");

            // Delete the only panel, then add another - the sequence that failed.
            first.Close();

            var second = Panel("viewer:xyz");
            PanelLayout.Attach(manager.Layout, second);

            Assert.True(
                IsInLayout(manager.Layout, second),
                "a panel added after the last one closed went missing under a live manager");
        });
    }

    // ---- adopting a restored layout -------------------------------------

    /// <summary>
    /// Deserialising a layout builds new anchorable objects. Whatever tracked
    /// the old ones is then holding orphans: it cannot close the panels on
    /// screen, so New leaves them behind and opening a project again stacks a
    /// second set beside them. Re-reading them from the tree is the fix, and
    /// this is the read.
    /// </summary>
    [Fact]
    public void The_node_panels_of_a_layout_can_be_found_again()
    {
        var (root, documents) = BuildShell(wrapGraphVertically: false);

        PanelLayout.Attach(root, new LayoutAnchorable { Title = "Image Viewer 1", ContentId = "viewer:abc" });
        PanelLayout.Attach(root, new LayoutAnchorable { Title = "Shape Table 1", ContentId = "shapetable:def" });

        var found = PanelLayout.NodePanels(root).ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, a => a.ContentId == "viewer:abc");
        Assert.Contains(found, a => a.ContentId == "shapetable:def");

        _ = documents;
    }

    /// <summary>
    /// The shell's own panels must not be adopted. They belong to no node, so
    /// a pass that closes panels without one would close the toolbox.
    /// </summary>
    [Fact]
    public void The_shells_own_panels_are_not_node_panels()
    {
        var (root, _) = BuildShell(wrapGraphVertically: false);

        var found = PanelLayout.NodePanels(root).Select(a => a.ContentId).ToList();

        Assert.DoesNotContain("toolbox", found);
        Assert.DoesNotContain("properties", found);
        Assert.Empty(found);
    }

    /// <summary>
    /// Two charts opened together must not sit exactly on top of each other,
    /// and the cascade has to stop walking off the screen.
    /// </summary>
    [Fact]
    public void Floating_positions_cascade_and_wrap()
    {
        var origin = new System.Windows.Point(200, 150);

        Assert.Equal(origin, PanelLayout.CascadeFrom(origin, 0));
        Assert.NotEqual(PanelLayout.CascadeFrom(origin, 0), PanelLayout.CascadeFrom(origin, 1));
        Assert.Equal(PanelLayout.CascadeFrom(origin, 0), PanelLayout.CascadeFrom(origin, 8));
    }

    /// <summary>
    /// A restored layout cancels content it cannot match, which for a profile
    /// chart is always - its id is a fresh guid each session. The window must
    /// not be left behind empty.
    /// </summary>
    [Fact]
    public void An_empty_floating_window_is_pruned()
    {
        var (root, _) = BuildShell(wrapGraphVertically: true);

        root.FloatingWindows.Add(new LayoutAnchorableFloatingWindow
        {
            RootPanel = new LayoutAnchorablePaneGroup(
                new LayoutAnchorablePane(Panel("profileline:kept"))),
        });
        root.FloatingWindows.Add(new LayoutAnchorableFloatingWindow());

        Assert.Equal(1, PanelLayout.PruneEmptyFloatingWindows(root));

        var remaining = Assert.Single(root.FloatingWindows);
        Assert.Contains(
            remaining.Descendents().OfType<LayoutAnchorable>(),
            anchorable => anchorable.ContentId == "profileline:kept");
    }
}
