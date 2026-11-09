using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Sirius.Core.Screens;

namespace Sirius.Builder;

/// <summary>
/// The screen designer, the middle of Sirius Builder's window: dragging
/// widgets about, resizing them by their handles, dropping new ones from the
/// palette.
/// </summary>
/// <remarks>
/// Pointer positions are read against the canvas itself, so they are in the
/// screen's own units whatever the zoom. A move or resize snaps to the grid
/// and is one undoable step, opened on the press and closed on the release.
/// </remarks>
public partial class ScreenDesignerView : UserControl
{
    private const string DragFormat = "SiriusScreenWidgetKind";

    private ScreenDesignerViewModel? _model;

    // ---- the gesture in hand ------------------------------------------------

    private DesignWidgetViewModel? _dragged;
    private string? _edge;
    private Point _pressedAt;
    private Rect _startedAs;

    private Point _paletteDownAt;

    public ScreenDesignerView()
    {
        InitializeComponent();
    }

    /// <summary>Save and Run pressed, and the screen saved: show the project running.</summary>
    public event EventHandler? RunRequested;

    internal ScreenDesignerViewModel? Model => _model;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;

        _model = DataContext as ScreenDesignerViewModel;

        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;

        Fit();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenDesignerViewModel.Width) or nameof(ScreenDesignerViewModel.Height))
            Fit();
    }

    // ---- the zoom --------------------------------------------------------

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Fit();

    /// <summary>The whole screen in view, as large as it fits.</summary>
    private void Fit()
    {
        var width = Scroller.ActualWidth - 60;
        var height = Scroller.ActualHeight - 60;

        if (_model is null || width <= 0 || height <= 0 || _model.Width <= 0 || _model.Height <= 0)
            return;

        var scale = Math.Clamp(Math.Min(width / _model.Width, height / _model.Height), 0.1, 2);
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
    }

    // ---- moving and resizing -----------------------------------------------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_model is null)
            return;

        var source = e.OriginalSource as DependencyObject;
        var widget = Find<DesignWidgetViewModel>(source);

        if (widget is null)
        {
            _model.Selected = null;
            DesignCanvas.Focus();
            return;
        }

        _model.Selected = widget;
        _dragged = widget;
        _edge = (source as Rectangle)?.Tag as string;
        _pressedAt = e.GetPosition(DesignCanvas);
        _startedAs = new Rect(widget.X, widget.Y, widget.Width, widget.Height);

        _model.BeginGesture();
        DesignCanvas.CaptureMouse();
        DesignCanvas.Focus();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged is not { } widget || e.LeftButton != MouseButtonState.Pressed)
            return;

        var now = e.GetPosition(DesignCanvas);
        var dx = now.X - _pressedAt.X;
        var dy = now.Y - _pressedAt.Y;
        var r = _startedAs;
        var snap = ScreenDesignerViewModel.Snap;

        if (_edge is null)
        {
            widget.Place(snap(r.X + dx), snap(r.Y + dy), r.Width, r.Height);
            return;
        }

        var left = r.Left;
        var top = r.Top;
        var right = r.Right;
        var bottom = r.Bottom;
        var minimum = DesignWidgetViewModel.MinimumSize;

        if (_edge.Contains('L'))
            left = Math.Min(snap(r.Left + dx), right - minimum);

        if (_edge.Contains('R'))
            right = Math.Max(snap(r.Right + dx), left + minimum);

        if (_edge.Contains('T'))
            top = Math.Min(snap(r.Top + dy), bottom - minimum);

        if (_edge.Contains('B'))
            bottom = Math.Max(snap(r.Bottom + dy), top + minimum);

        widget.Place(left, top, right - left, bottom - top);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnCanvasLostCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragged is null || _model is null)
            return;

        _dragged = null;
        _edge = null;

        if (DesignCanvas.IsMouseCaptured)
            DesignCanvas.ReleaseMouseCapture();

        _model.EndGesture();
    }

    // ---- the palette -----------------------------------------------------

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e) =>
        _paletteDownAt = e.GetPosition(this);

    /// <summary>Dragging a palette entry onto the screen puts the widget where it is dropped.</summary>
    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Button { DataContext: WidgetKind kind } button)
            return;

        var moved = e.GetPosition(this) - _paletteDownAt;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(button, new DataObject(DragFormat, kind), DragDropEffects.Copy);
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (_model is null || e.Data.GetData(DragFormat) is not WidgetKind kind)
            return;

        var at = e.GetPosition(DesignCanvas);
        _model.Add(kind, at.X, at.Y);
        e.Handled = true;
    }

    // ---- keys --------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null)
            return;

        // Typing in a box is typing, not editing the canvas.
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Commit the box being typed in before saving it.
                (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                _model.SaveCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;

        switch (e.Key)
        {
            case Key.Delete:
                _model.DeleteCommand.Execute(null);
                break;
            case Key.D when control:
                _model.DuplicateCommand.Execute(null);
                break;
            case Key.Z when control:
                _model.UndoCommand.Execute(null);
                break;
            case Key.Y when control:
                _model.RedoCommand.Execute(null);
                break;
            case Key.S when control:
                _model.SaveCommand.Execute(null);
                break;
            case Key.Left:
                _model.Nudge(-step, 0);
                break;
            case Key.Right:
                _model.Nudge(step, 0);
                break;
            case Key.Up:
                _model.Nudge(0, -step);
                break;
            case Key.Down:
                _model.Nudge(0, step);
                break;
            case Key.Escape:
                _model.Selected = null;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- running ---------------------------------------------------------

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_model?.Save() == true)
            RunRequested?.Invoke(this, EventArgs.Empty);
    }

    private static T? Find<T>(DependencyObject? source)
        where T : class
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T found })
                return found;

            if (current is ItemsControl)
                return null;
        }

        return null;
    }
}
