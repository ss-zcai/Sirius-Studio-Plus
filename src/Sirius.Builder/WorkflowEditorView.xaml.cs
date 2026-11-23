using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sirius.Core.Nodes;
using Sirius.Studio.App.Layout;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Builder;

/// <summary>A controller's workflow, edited on Studio's canvas, with what its check finds.</summary>
public partial class WorkflowEditorView : UserControl
{
    /// <summary>What a node dragged from the tools panel is carried as.</summary>
    public const string DragFormat = "Sirius.WorkflowNode";

    public WorkflowEditorView()
    {
        InitializeComponent();
    }

    private WorkflowDocumentViewModel? Model => DataContext as WorkflowDocumentViewModel;

    /// <summary>Adds a node where the canvas is looking - a double-click in the tools panel.</summary>
    public void AddInView(NodeDescriptor descriptor) =>
        Model?.AddNode(descriptor, InView(new Point(Math.Max(Canvas.ActualWidth, 300) / 3, Math.Max(Canvas.ActualHeight, 300) / 3)));

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not NodeDescriptor descriptor || Model is not { } model)
            return;

        model.AddNode(descriptor, InView(e.GetPosition(Canvas)));
        e.Handled = true;
    }

    // ---- the view: zoom, and every node in it -------------------------------------------

    private bool _framed;

    public void ZoomIn() => Canvas.ZoomIn();

    public void ZoomOut() => Canvas.ZoomOut();

    /// <summary>Every node in view, with a margin, never closer than 1:1 - as Studio frames a recipe.</summary>
    public void Fit() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0 || Model?.Editor.Nodes.Count is null or 0)
                return;

            var (location, zoom) = GraphView.Frame(
                Canvas.ItemsExtent,
                new Size(Canvas.ActualWidth, Canvas.ActualHeight),
                Canvas.MinViewportZoom,
                Math.Min(GraphView.MaxZoom, Canvas.MaxViewportZoom));

            Canvas.ViewportZoom = zoom;
            Canvas.ViewportLocation = location;
        });

    /// <summary>Framed the first time it is shown; after that it stays where it was left.</summary>
    private void OnCanvasLoaded(object sender, RoutedEventArgs e)
    {
        if (_framed)
            return;

        _framed = true;
        Fit();
    }

    // ---- renamed on the canvas, as in Studio ---------------------------------------------

    /// <summary>A double click on a node's caption renames it there.</summary>
    private void OnCanvasDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && CaptionOf(source) is { } node)
        {
            node.IsEditingName = true;
            e.Handled = true;
        }
    }

    /// <summary>The node whose caption <paramref name="source"/> is in; null when it is not in one.</summary>
    internal static NodeViewModel? CaptionOf(DependencyObject source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is FrameworkElement { Tag: "NodeTitle", DataContext: NodeViewModel node })
                return node;

            if (current is Nodify.Node)
                return null;
        }

        return null;
    }

    /// <summary>Up through text runs as well as the visual tree.</summary>
    private static DependencyObject? Parent(DependencyObject child) =>
        child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);

    private void OnNameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox editor || !editor.IsVisible)
            return;

        // Deferred: the box cannot take focus at the moment it is shown.
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Enter renames; Escape leaves the name as it was.</summary>
    private void OnNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NodeViewModel node } editor)
            return;

        if (e.Key == Key.Enter)
        {
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            node.IsEditingName = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            node.IsEditingName = false;
            e.Handled = true;
        }
    }

    private void OnNameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NodeViewModel node })
            node.IsEditingName = false;
    }

    /// <summary>A point on the canvas control, in the graph's own coordinates - through its pan and zoom.</summary>
    private Point InView(Point onScreen) =>
        new(
            Canvas.ViewportLocation.X + (onScreen.X / Canvas.ViewportZoom),
            Canvas.ViewportLocation.Y + (onScreen.Y / Canvas.ViewportZoom));
}
