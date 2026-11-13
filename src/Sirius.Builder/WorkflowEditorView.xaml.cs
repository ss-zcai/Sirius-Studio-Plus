using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.Core.Nodes;

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

    /// <summary>A point on the canvas control, in the graph's own coordinates - through its pan and zoom.</summary>
    private Point InView(Point onScreen) =>
        new(
            Canvas.ViewportLocation.X + (onScreen.X / Canvas.ViewportZoom),
            Canvas.ViewportLocation.Y + (onScreen.Y / Canvas.ViewportZoom));
}
