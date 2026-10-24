using System.Windows;
using System.Windows.Threading;

namespace Sirius.Studio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A node that misbehaves should surface as a message, not take the
        // application down mid-inspection.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
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
