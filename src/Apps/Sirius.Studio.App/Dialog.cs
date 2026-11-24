using System.Windows;

namespace Sirius.Studio.App;

/// <summary>
/// Every question and every report the shell puts in front of the operator.
/// </summary>
public static class Dialog
{
    /// <summary>
    /// What actually shows a dialog. Replaceable, and that is the point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A modal opened from a test blocks the thread that opened it until
    /// somebody clicks. In an automated run nobody does, so the test hangs
    /// until it is noticed - and in this project it was noticed by the
    /// person whose screen the dialog appeared on, in the middle of
    /// something else, with no indication of which test had asked or what
    /// answering would do.
    /// </para>
    /// <para>
    /// So the test assembly replaces this with an automatic answer. That is
    /// a safety net rather than a licence: a test that reaches a prompt is
    /// usually a test that set up the wrong state, and the net stops it
    /// stopping the run rather than making it correct.
    /// </para>
    /// </remarks>
    public static Func<Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult>
        Present { get; set; } = MessageWindow.Show;

    /// <summary>Asks a yes/no/cancel question.</summary>
    public static MessageBoxResult Ask(string message, string title, Window? owner = null) =>
        Present(Owner(owner), message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    /// <summary>Asks something that can only be gone through with or abandoned.</summary>
    public static bool Confirm(string message, string title, Window? owner = null) =>
        Present(Owner(owner), message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;

    /// <summary>Says something happened, where nothing is being asked.</summary>
    public static void Inform(string message, string title, Window? owner = null) =>
        Present(Owner(owner), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Reports something that worked, but not entirely.</summary>
    public static void Warn(string message, string title, Window? owner = null) =>
        Present(Owner(owner), message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Reports something that did not work.</summary>
    public static void Error(string message, string title, Window? owner = null) =>
        Present(Owner(owner), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// The window a dialog belongs to, when the caller did not name one.
    /// </summary>
    /// <remarks>
    /// Taken from the application rather than required at every call site:
    /// the view-model raises most of these and has no business holding a
    /// window. Null is handled - a failure during startup still has to be
    /// reportable, and there may be no window yet.
    /// </remarks>
    private static Window? Owner(Window? explicitOwner)
    {
        if (explicitOwner is not null)
            return explicitOwner;

        try
        {
            return Application.Current?.MainWindow;
        }
        catch (Exception)
        {
            // Reaching Application.Current from the wrong thread throws, and
            // an unowned dialog beats no dialog.
            return null;
        }
    }
}
