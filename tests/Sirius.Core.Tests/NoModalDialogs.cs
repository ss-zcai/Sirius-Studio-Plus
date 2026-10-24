using System.Runtime.CompilerServices;
using System.Windows;
using Sirius.Studio.App;

namespace Sirius.Core.Tests;

/// <summary>
/// No test ever opens a real dialog.
/// </summary>
/// <remarks>
/// <para>
/// A modal blocks the thread that opened it until somebody clicks. In an
/// automated run nobody does, so the suite stops - and it stops silently,
/// because the dialog appears on whoever's screen happens to be attached
/// rather than in the test output. That happened: a "Save this project
/// before closing it?" box turned up mid-run, with nothing to say which
/// test had asked it or what answering would do.
/// </para>
/// <para>
/// So the whole assembly answers its own prompts, and records them. The
/// answers are the ones that change nothing: No to a save prompt, Cancel to
/// a confirmation. A test that depends on the other answer has to say so by
/// setting <see cref="Dialog.Present"/> itself for its own duration.
/// </para>
/// <para>
/// This is a net, not a licence. A test that reaches a prompt has usually
/// set up the wrong state - the one that caused this added a node and then
/// asked for a new project, which is an unsaved-changes prompt by
/// definition. <see cref="Asked"/> is public so a test can assert it was
/// never put in that position.
/// </para>
/// </remarks>
internal static class NoModalDialogs
{
    private static readonly List<string> Prompts = [];

    /// <summary>Every prompt a test has reached, newest last.</summary>
    public static IReadOnlyList<string> Asked
    {
        get
        {
            lock (Prompts)
                return Prompts.ToArray();
        }
    }

    public static void Forget()
    {
        lock (Prompts)
            Prompts.Clear();
    }

    [ModuleInitializer]
    internal static void Install() => Dialog.Present = (_, message, title, buttons, _) =>
    {
        lock (Prompts)
            Prompts.Add($"{title}: {message}");

        return buttons switch
        {
            // The answer that discards nothing and writes nothing.
            MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    };
}
