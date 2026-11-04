using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NodeGraphKit.ViewModels;

namespace NodeGraphKit.Controls;

/// <summary>The toolbox. Bind its DataContext to a <see cref="PaletteViewModel"/>.</summary>
public partial class ToolboxView : UserControl
{
    private Point _dragOrigin;
    private PaletteItemViewModel? _pressed;

    public ToolboxView()
    {
        InitializeComponent();
    }

    private PaletteViewModel? ViewModel => DataContext as PaletteViewModel;

    /// <summary>Puts the cursor in the search box - bind Ctrl+F to this.</summary>
    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        });
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _pressed = (sender as FrameworkElement)?.DataContext as PaletteItemViewModel;

        // A Border has no MouseDoubleClick, so the second click is caught here.
        if (e.ClickCount == 2 && _pressed is not null)
        {
            ViewModel?.RequestAdd(_pressed.Definition);
            _pressed = null;
            e.Handled = true;
        }
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is null)
            return;

        // Only past the system's drag threshold, or every click would start a drag.
        var delta = _dragOrigin - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(GraphEditorView.DragFormat, item.Definition), DragDropEffects.Copy);
    }

    /// <summary>Enter adds the first match, so a tool can be placed without the mouse; Esc clears.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                if (model.FirstMatch is { } first)
                {
                    model.RequestAdd(first.Definition);

                    // Selected rather than cleared: Enter again adds another,
                    // and typing starts the next search.
                    SearchBox.SelectAll();
                }

                e.Handled = true;
                break;

            case Key.Escape:
                model.Search = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model)
            model.Search = string.Empty;

        SearchBox.Focus();
    }
}
