using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A project running on its screen: what the operator sees (runtime MVP
/// step 4).
/// </summary>
/// <remarks>
/// <para>
/// The runtime program shows it full screen, with no title bar; Studio's
/// Run as Runtime shows it in a window of its own, to try a project while
/// building it. Either way it loads each station's recipe as saved, so what
/// runs is what is on disk.
/// </para>
/// <para>
/// Closing stops every station first, finishing the inspection each has in
/// hand, and asks before it does: an operator should not end inspection by
/// brushing Alt+F4. A PIN for it comes with user levels.
/// </para>
/// </remarks>
public partial class RuntimeWindow : Window
{
    private readonly ProjectRuntime _runtime;
    private readonly ScreenViewModel _screen;
    private bool _closing;
    private bool _closed;

    private RuntimeWindow(Project project, bool fullScreen, IEnumerable<string>? problems = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _runtime = new ProjectRuntime(project);

        var screen = ScreenLayout.ScreenFor(project, _runtime.Stations, out var problem);
        _screen = new ScreenViewModel(screen, _runtime, project.Folder, [.. problems ?? [], .. problem is null ? [] : new[] { problem }]);

        DataContext = _screen;
        Title = $"{project.Name} - Sirius Runtime";
        Confirm = question => Dialog.Confirm(question, Title, this);

        if (fullScreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    internal ProjectRuntime Runtime => _runtime;

    internal ScreenViewModel Screen => _screen;

    /// <summary>
    /// Shows <paramref name="project"/> running, in a window of its own over
    /// <paramref name="owner"/>, and returns once it is closed.
    /// </summary>
    public static void ShowWindowed(Window? owner, Project project)
    {
        var window = new RuntimeWindow(project, fullScreen: false)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    /// <summary>The window the runtime program runs in, full screen or not.</summary>
    /// <param name="problems">What opening the project found, shown along the bottom.</param>
    public static RuntimeWindow Create(Project project, bool fullScreen, IEnumerable<string>? problems = null) =>
        new(project, fullScreen, problems);

    /// <summary>For tests: the window, not shown.</summary>
    internal static RuntimeWindow Unshown(Project project) => new(project, fullScreen: false);

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A setting's box: Enter changes the setting, Escape puts back what it
    /// holds - and neither closes the window.
    /// </summary>
    private void OnSettingBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box)
            return;

        var binding = box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);

        if (e.Key == Key.Enter)
        {
            binding?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            binding?.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowStyle != WindowStyle.None)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Asks before closing; a test answers for the operator.</summary>
    internal Func<string, bool> Confirm { get; set; }

    /// <remarks>
    /// <para>
    /// With nothing running the window simply closes, once asked. With a
    /// station running, this close is called off, the stations are stopped -
    /// each finishing the inspection in hand - and the window is closed
    /// again afterwards.
    /// </para>
    /// <para>
    /// That second close is posted, never made from in here: WPF refuses a
    /// Close while the window is still closing. Stopping stations that had
    /// already stopped finishes at once, and closing straight after it was
    /// exactly that - an exception on every Exit with nothing running.
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed)
            return;

        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        // Always asked full screen, where this is the operator's whole
        // application; in Studio's window, only while something runs.
        var running = _runtime.Stations.Any(station => station.IsBusy);
        var ask = running || WindowStyle == WindowStyle.None;

        if (ask && !Confirm(running ? "Stop every station and close?" : "Close the runtime?"))
        {
            e.Cancel = true;
            return;
        }

        if (!running)
        {
            ShutDown();
            return;
        }

        e.Cancel = true;
        _closing = true;
        _ = StopThenCloseAsync();
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            await _runtime.StopAllAsync().ConfigureAwait(true);
        }
        finally
        {
            ShutDown();
            await Dispatcher.InvokeAsync(Close);
        }
    }

    private void ShutDown()
    {
        if (_closed)
            return;

        _closed = true;
        _screen.Dispose();
        _runtime.Dispose();
    }
}
