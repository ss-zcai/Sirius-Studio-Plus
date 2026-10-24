using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace Sirius.Studio.App.Layout;

/// <summary>
/// Places node panels in the docking layout.
/// </summary>
/// <remarks>
/// Pure layout-model manipulation, deliberately separate from the window: it
/// needs no DockingManager, which makes the placement rules testable.
/// </remarks>
public static class PanelLayout
{
    /// <summary>Height given to a freshly created panel pane.</summary>
    public static readonly GridLength DefaultPaneHeight = new(300);

    /// <summary>
    /// Places a panel alongside the existing node panels if there are any,
    /// otherwise in a new pane directly below the graph.
    /// </summary>
    /// <remarks>
    /// The anchorable is added to the pane <b>before</b> the pane joins the
    /// layout. AvalonDock prunes empty panes as they are attached, so filling
    /// the pane afterwards leaves it detached and the panel invisible - and
    /// nothing reports a problem, because adding to a detached pane still
    /// succeeds.
    /// </remarks>
    public static void Attach(LayoutRoot root, LayoutAnchorable anchorable, Action<string>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(anchorable);

        trace?.Invoke($"    attach root=#{root.GetHashCode():X} rootPanel={(root.RootPanel is null ? "<null>" : root.RootPanel.Orientation.ToString())} children={root.RootPanel?.ChildrenCount ?? -1}");

        // Respect where the panels already live, so moving them is not undone.
        var sibling = root
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => IsPanelId(a.ContentId) && a.Parent is LayoutAnchorablePane);

        if (sibling?.Parent is LayoutAnchorablePane existing)
        {
            trace?.Invoke("    branch=sibling");
            existing.Children.Add(anchorable);
            return;
        }

        var pane = new LayoutAnchorablePane { DockHeight = DefaultPaneHeight };
        pane.Children.Add(anchorable);

        if (root.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault() is { } documents)
        {
            trace?.Invoke($"    branch=below parent={documents.Parent?.GetType().Name ?? "<none>"}" +
                          $" orientation={(documents.Parent as LayoutPanel)?.Orientation.ToString() ?? "n/a"}");
            AttachBelow(documents, pane, trace);
            trace?.Invoke($"    after: paneParent={pane.Parent?.GetType().Name ?? "<none>"}" +
                          $" paneInRoot={root.Descendents().Contains(pane)}" +
                          $" docsInRoot={root.Descendents().Contains(documents)}");
            return;
        }

        trace?.Invoke("    branch=rootPanel");
        root.RootPanel?.Children.Add(pane);
    }

    /// <summary>Size given to a freshly floated panel.</summary>
    public static readonly Size DefaultFloatingSize = new(640, 420);

    /// <summary>
    /// Where the next floating panel should open, stepped so that several do
    /// not land exactly on top of one another.
    /// </summary>
    public static Point CascadeFrom(Point origin, int opened)
    {
        const double step = 28;
        const int wrapAfter = 8;

        var index = opened % wrapAfter;
        return new Point(origin.X + (index * step), origin.Y + (index * step));
    }

    /// <summary>
    /// Drops floating windows that hold nothing.
    /// </summary>
    /// <remarks>
    /// A profile chart floats, and its identity is a fresh guid that no later
    /// session can match, so restoring a layout that mentions one cancels its
    /// content - leaving the window itself behind with nothing in it. An
    /// empty floating window is still a window on screen.
    /// </remarks>
    public static int PruneEmptyFloatingWindows(LayoutRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var empty = root.FloatingWindows
            .Where(window => !window.Descendents().OfType<LayoutContent>().Any())
            .ToArray();

        foreach (var window in empty)
            root.FloatingWindows.Remove(window);

        return empty.Length;
    }

    /// <summary>Node panels are identified by a "kind:nodeId" content id.</summary>
    public static bool IsPanelId(string? contentId) =>
        contentId?.Contains(':', StringComparison.Ordinal) == true;

    /// <summary>
    /// The node panels in a layout tree.
    /// </summary>
    /// <remarks>
    /// Deserialising a layout builds <b>new</b> anchorable objects, so anything
    /// holding the old ones is left pointing at detached orphans - it can no
    /// longer close the panels on screen, and will build duplicates beside
    /// them. Whoever tracks panels has to re-read them from the restored tree,
    /// and this is what it reads.
    ///
    /// The shell's own panels - toolbox, properties, the graph - are excluded.
    /// Tracking those alongside node panels would let a later pass close them,
    /// since no node claims them.
    /// </remarks>
    public static IEnumerable<LayoutAnchorable> NodePanels(LayoutRoot root) =>
        root.Descendents()
            .OfType<LayoutAnchorable>()
            .Where(anchorable => IsPanelId(anchorable.ContentId));

    /// <summary>
    /// Stacks <paramref name="pane"/> underneath the graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document pane is not necessarily a direct child of a panel. After a
    /// layout restore AvalonDock wraps it in a LayoutDocumentPaneGroup, which
    /// accepts only document panes - inserting an anchorable pane there is
    /// rejected without error, leaving the panel attached to nothing. So this
    /// climbs to the outermost container that a panel actually holds, and
    /// positions relative to that.
    /// </para>
    /// <para>
    /// Inserting beside that container is only "below" it when the panel stacks
    /// vertically; AvalonDock collapses a panel holding a single child, so the
    /// graph often ends up directly in the horizontal root. In that case the
    /// container is wrapped in a vertical panel of its own.
    /// </para>
    /// </remarks>
    private static void AttachBelow(LayoutDocumentPane documents, LayoutAnchorablePane pane, Action<string>? trace)
    {
        ILayoutElement container = documents;
        while (container.Parent is ILayoutElement ancestor and not LayoutPanel)
            container = ancestor;

        trace?.Invoke($"    container={container.GetType().Name} parent={container.Parent?.GetType().Name ?? "<none>"}");

        if (container.Parent is not LayoutPanel panel)
        {
            // Nothing recognisable to stack under; put it on the root so the
            // panel is at least reachable.
            documents.Root?.RootPanel?.Children.Add(pane);
            return;
        }

        if (panel.Orientation == Orientation.Vertical)
        {
            panel.InsertChildAt(panel.IndexOfChild(container) + 1, pane);
            return;
        }

        var index = panel.IndexOfChild(container);
        var stack = new LayoutPanel { Orientation = Orientation.Vertical };

        // Fill the stack before attaching it: an empty container is pruned the
        // moment it joins a live layout, orphaning whatever is added after.
        stack.Children.Add(pane);

        // Replacing detaches the container, which is why its index is captured
        // first - and why it goes back immediately afterwards, above the pane.
        panel.ReplaceChildAt(index, stack);
        stack.InsertChildAt(0, container);
    }
}
