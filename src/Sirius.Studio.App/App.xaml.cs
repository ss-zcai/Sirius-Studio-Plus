using System.ComponentModel;
using System.Reflection;
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

        DropMenusFromTheLeft();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        base.OnStartup(e);
    }

    /// <summary>
    /// Makes menus open below their heading's left edge, whatever the
    /// Windows handedness setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows' Tablet PC "right-handed" setting - on by default on many
    /// touch-capable machines - tells WPF to drop every menu leftwards, so
    /// it does not open under the hand holding the pen. On a desktop app
    /// driven by a mouse it just makes the File menu hang off to the left
    /// of "File", which reads as a layout fault. Context menus and
    /// drop-downs follow the same setting.
    /// </para>
    /// <para>
    /// WPF reads the setting into a cached field and offers no way to
    /// override it, so the cache is set directly. If a later WPF renames
    /// the field, nothing happens and menus fall back to the system's
    /// choice.
    /// </para>
    /// </remarks>
    internal static void DropMenusFromTheLeft()
    {
        if (!SystemParameters.MenuDropAlignment)
            return;

        typeof(SystemParameters)
            .GetField("_menuDropAlignment", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, false);
    }

    /// <summary>Puts it back when Windows re-reads its settings - a change to handedness, or a resume.</summary>
    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.MenuDropAlignment))
            DropMenusFromTheLeft();
    }

    /// <summary>Lets every open camera go, so the next program to use one finds it free.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Sirius.Vision.Cameras.CameraDrivers.CloseAll();
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
