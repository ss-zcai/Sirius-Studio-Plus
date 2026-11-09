using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sirius.Studio.App;

namespace Sirius.Builder;

/// <summary>Sirius Builder's window.</summary>
public partial class BuilderWindow : Window
{
    private readonly BuilderViewModel _model;

    public BuilderWindow(BuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
    }

    internal BuilderViewModel Model => _model;

    /// <summary>Back in front: a recipe may have been saved in Studio meanwhile.</summary>
    private void OnActivated(object? sender, EventArgs e) => _model.Refresh();

    private void OnRunRequested(object? sender, EventArgs e) => _model.TestRun(this);

    private void OnStationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: StationViewModel station })
            {
                _model.OpenInStudioCommand.Execute(station);
                return;
            }
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_model.ConfirmScreen("Sirius Builder"))
            e.Cancel = true;
    }
}
