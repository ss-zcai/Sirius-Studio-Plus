using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.Core.Nodes;

namespace Sirius.Builder;

/// <summary>A controller's workflow, edited: palette, canvas, settings.</summary>
public partial class WorkflowEditorView : UserControl
{
    private const string DragFormat = "Sirius.WorkflowNode";

    private Point _pressedAt;
    private NodeDescriptor? _pressed;

    public WorkflowEditorView()
    {
        InitializeComponent();
    }

    private WorkflowDocumentViewModel? Model => DataContext as WorkflowDocumentViewModel;

    /// <summary>A double-click adds the node in view; a press is otherwise the start of a drag.</summary>
    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(this);
        _pressed = (sender as FrameworkElement)?.DataContext as NodeDescriptor;

        if (e.ClickCount == 2 && _pressed is { } descriptor && Model is { } model)
        {
            model.AddNode(descriptor, InView(new Point(Canvas.ActualWidth / 3, Canvas.ActualHeight / 3)));
            _pressed = null;
            e.Handled = true;
        }
    }

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } descriptor)
            return;

        var moved = e.GetPosition(this) - _pressedAt;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, descriptor), DragDropEffects.Copy);
    }

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
