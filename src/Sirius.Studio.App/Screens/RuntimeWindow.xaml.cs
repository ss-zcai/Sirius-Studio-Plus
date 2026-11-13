using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Plc;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A project running on its screen: what the operator sees (runtime MVP
/// step 4).
/// </summary>
/// <remarks>
/// <para>
/// Sirius Edge shows it full screen, with no title bar; Sirius Builder's
/// Test Run shows it in a window of its own, to try a project while
/// building it. Either way it loads each station's recipe as saved, so what
/// runs is what is on disk.
/// </para>
/// <para>
/// Closing stops every station first, finishing the inspection each has in
/// hand, and asks before it does: an operator should not end inspection by
/// brushing Alt+F4. A PIN for it comes with user levels.
/// </para>
/// <para>
/// <b>Controllers</b> - the project's PLCs and their workflows - start once
/// the window is shown, talk to the stations through a station bus on this
/// window's thread, and are stopped before the stations when it closes, so
/// a workflow is never left asking a station that has gone. Their state is
/// along the bottom.
/// </para>
/// </remarks>
public partial class RuntimeWindow : Window
{
    private readonly ProjectRuntime _runtime;
    private readonly ProjectStationBus _bus;
    private readonly ProjectControllers _controllers;
    private readonly System.Windows.Threading.DispatcherTimer _controllerClock;
    private readonly ScreenViewModel _screen;
    private bool _closing;
    private bool _closed;

    private RuntimeWindow(Project project, bool fullScreen, IEnumerable<string>? problems = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _runtime = new ProjectRuntime(project);

        // The stations are driven from this window's thread; a workflow asks
        // from its own, and the bus brings each request here.
        _bus = new ProjectStationBus(_runtime, new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        _controllers = ProjectControllers.Open(project, _bus);

        var screen = ScreenLayout.ScreenFor(project, _runtime.Stations, out var problem);
        _screen = new ScreenViewModel(
            screen,
            _runtime,
            project.Folder,
            [.. problems ?? [], .. problem is null ? [] : new[] { problem }, .. _controllers.Problems]);

        _controllerClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _controllerClock.Tick += (_, _) => ShowControllers();
        Loaded += (_, _) => StartControllers();

        DataContext = _screen;
        Title = $"{project.Name} - Sirius Edge";
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

    /// <summary>The project's PLCs and their workflows, started once the window is shown.</summary>
    public ProjectControllers Controllers => _controllers;

    internal ScreenViewModel Screen => _screen;

    /// <summary>
    /// Shows <paramref name="project"/> running, in a window of its own over
    /// <paramref name="owner"/>, and returns once it is closed.
    /// </summary>
    /// <param name="shown">
    /// Called once the window is on screen - for Builder to open the
    /// simulated PLCs beside it, which a window shown before it, being
    /// behind a dialog, could not be used.
    /// </param>
    public static void ShowWindowed(Window? owner, Project project, Action<RuntimeWindow>? shown = null)
    {
        var window = new RuntimeWindow(project, fullScreen: false)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (shown is not null)
            window.ContentRendered += (_, _) => shown(window);

        window.ShowDialog();
    }

    // ---- controllers -----------------------------------------------------------------

    internal void StartControllers()
    {
        if (_closed || _controllers.Runners.Count == 0)
            return;

        _controllers.StartAll();
        ShowControllers();
        _controllerClock.Start();
    }

    private void ShowControllers()
    {
        ControllerStatus.Text = _controllers.Runners.Count == 0 ? string.Empty : _controllers.StatusText;
        ControllerStatus.ToolTip = ControllerStatus.Text;
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

    /// <summary>
    /// A slider let go, or moved by a key: its handles go to the setting now.
    /// Bound with an explicit update, so a drag is one change to the
    /// station, not one for every pixel the handle passed.
    /// </summary>
    private void OnSliderCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is not Controls.RangeSlider slider)
            return;

        if (!slider.IsSingle)
            slider.GetBindingExpression(Controls.RangeSlider.LowProperty)?.UpdateSource();

        slider.GetBindingExpression(Controls.RangeSlider.HighProperty)?.UpdateSource();
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

        // A controller is stopped without blocking this thread: its pass
        // may be waiting on a station, which needs this thread to finish.
        var controlling = _controllers.Runners.Any(runner => runner.State != Sirius.Plc.Workflows.ControllerState.Stopped);

        if (!running && !controlling)
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
            _controllerClock.Stop();
            await _controllers.StopAllAsync().ConfigureAwait(true);
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
        _controllerClock.Stop();

        // Stopped already, or never started: letting go of their drivers is all that is left.
        _ = _controllers.DisposeAsync().AsTask();
        _bus.Dispose();
        _screen.Dispose();
        _runtime.Dispose();
    }
}
