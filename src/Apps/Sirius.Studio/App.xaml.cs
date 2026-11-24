using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

using Sirius.UI;
using Sirius.UI.Hosting;

namespace Sirius.Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A node that misbehaves should surface as a message, not take the
        // application down mid-inspection.
        DispatcherUnhandledException += OnUnhandledException;
        Sirius.UI.Diagnostics.CrashLog.Watch("Sirius Studio");

        SiriusHost.DropMenusFromTheLeft();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        // Camera drivers looked for from the first moment, off this thread -
        // the GigE search waits for cameras to answer - and marked as under
        // way, so a recipe loaded meanwhile waits for its camera's driver.
        _ = Sirius.Vision.Cameras.CameraDrivers.LoadInstalledInBackground();

        base.OnStartup(e);
    }

    /// <summary>Puts it back when Windows re-reads its settings - a change to handedness, or a resume.</summary>
    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.MenuDropAlignment))
            SiriusHost.DropMenusFromTheLeft();
    }

    /// <summary>
    /// Lets every open camera go, so the next program to use one finds it
    /// free - and opens none after, though an inspection may still be
    /// finishing.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Sirius.Vision.Cameras.CameraDrivers.ShutDown();
        base.OnExit(e);
    }

    private bool _reportingError;

    /// <summary>
    /// Reports the first unhandled exception and swallows the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-entrancy guard is the point. <c>MessageBox.Show</c> runs a modal
    /// message pump, which lets WPF's pending layout callback run inside it. If
    /// the exception came from layout - as a fault in a control's template or
    /// in a handler called during one does - that layout pass throws again,
    /// shows another dialog, pumps again, and the process dies of a stack
    /// overflow instead of reporting the fault.
    /// </para>
    /// <para>
    /// A stack overflow cannot be caught and prints only frames, so the
    /// original exception - the one worth reading - is lost entirely. That is
    /// a bad trade for a diagnostic.
    /// </para>
    /// </remarks>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        // Before the guard: a fault met again while its dialog is up is counted too.
        Sirius.UI.Diagnostics.CrashLog.Write("Sirius Studio", e.Exception);

        if (_reportingError)
            return;

        _reportingError = true;

        // Written out as well as shown: a dialog is no use when the app is
        // being run from a script or a log is wanted after the fact.
        Console.Error.WriteLine(e.Exception.ToString());

        try
        {
            // Themed like the rest, but with the stock dialog behind it:
            // this runs when something has already gone wrong, and a themed
            // window needs the theme's resources, which may be exactly what
            // failed. Losing the crash report would be worse than an ugly
            // one.
            try
            {
                Dialog.Error(e.Exception.ToString(), "Unexpected error");
            }
            catch (Exception)
            {
                MessageBox.Show(
                    e.Exception.ToString(),
                    "Unexpected error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _reportingError = false;
        }
    }
}
