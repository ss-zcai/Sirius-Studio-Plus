using System.Diagnostics;
using System.Windows;

namespace NodeGraphKit.Demo;

public partial class App : Application
{
    /// <summary>
    /// Set NODEGRAPH_BINDING_LOG to a file path to have WPF's binding errors
    /// written there - the quickest way to find a misspelt binding path,
    /// which otherwise fails silently.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("NODEGRAPH_BINDING_LOG") is { Length: > 0 } path)
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(path));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Trace.AutoFlush = true;
        }

        // Menus drop to the right even on a PC set up right-handed for a pen.
        Controls.MenuAlignment.UseLeftAligned();

        base.OnStartup(e);
    }
}
