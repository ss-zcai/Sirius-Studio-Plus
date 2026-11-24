using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.UI;

namespace Sirius.UI.Plc;

/// <summary>A controller's PLC: try the connection, read and write, and drive a simulated PLC by hand.</summary>
public partial class PlcWindow : Window
{
    public PlcWindow(PlcViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        Model = model;
        DataContext = model;

        // The workflow's log follows its newest line - once the list has
        // taken the line in. This handler was added before the list's own,
        // so scrolling from inside it made the list build its rows a line
        // short of what it held: "An ItemsControl is inconsistent with its
        // items source", which closed Builder on the second part of a Test
        // Run, as soon as the log was long enough to scroll.
        model.WorkflowLog.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add)
                return;

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (IsLoaded && WorkflowLogList.Items.Count > 0)
                        WorkflowLogList.ScrollIntoView(WorkflowLogList.Items[^1]);
                },
                System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    internal PlcViewModel Model { get; }

    /// <summary>Opens the window, not modal, over <paramref name="owner"/>.</summary>
    public static PlcWindow Open(Window? owner, PlcViewModel model)
    {
        var window = new PlcWindow(model)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.Show();
        return window;
    }

    /// <summary>An address typed and Enter pressed: it is the project's now.</summary>
    private void OnCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void OnClosed(object? sender, EventArgs e) => _ = Model.DisposeAsync().AsTask();
}
