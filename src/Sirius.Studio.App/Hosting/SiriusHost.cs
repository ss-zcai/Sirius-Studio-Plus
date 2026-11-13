using System.IO;
using System.Windows;
using System.Windows.Threading;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Cameras;

namespace Sirius.Studio.App.Hosting;

/// <summary>
/// What every Sirius program starts with: the theme, the node library, the
/// custom nodes and camera drivers - loaded exactly as Studio loads them, so
/// a recipe judges the same in Studio, Sirius Builder and Sirius Edge
/// (decision 71).
/// </summary>
/// <remarks>
/// Here, in Studio's assembly, because the theme, the image viewer and the
/// screens are: Builder and Edge are programs of their own that use them.
/// </remarks>
public static class SiriusHost
{
    /// <summary>
    /// The application, themed, menus dropping from the left, and faults
    /// reported rather than ending it. Call once, on the STA thread.
    /// </summary>
    /// <param name="title">The program's name, for messages.</param>
    public static Application CreateApplication(string title)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadTheme(app);
        App.DropMenusFromTheLeft();

        app.DispatcherUnhandledException += (_, e) => Report(e, title);
        return app;
    }

    /// <summary>Why the vision libraries cannot run on this PC, or null.</summary>
    public static string? NativeProblem() => NativeLibraries.Problem();

    /// <summary>The node library, the user's custom nodes and the installed camera drivers.</summary>
    public static void LoadLibraries()
    {
        VisionNodeLibrary.Register();

        // Controllers' workflows: their nodes, and the simulated PLC. Never
        // in a recipe's palette (NodeCategories.IsWorkflow).
        Sirius.Plc.PlcNodeLibrary.Register();

        CustomNodesForCurrentUser().LoadAll();
        CameraDrivers.LoadInstalled();
    }

    /// <summary>Lets go of the cameras.</summary>
    public static void ShutDown() => CameraDrivers.ShutDown();

    /// <summary>
    /// The theme Studio's App.xaml merges, from Studio's assembly - so every
    /// program's controls look as they do in Studio.
    /// </summary>
    public static void LoadTheme(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var source in new[]
                 {
                     "pack://application:,,,/Nodify;component/Themes/Styles/Controls.xaml",
                     "pack://application:,,,/SiriusStudioPlus;component/Themes/Icons.xaml",
                     "pack://application:,,,/SiriusStudioPlus;component/Themes/Logo.xaml",
                     "pack://application:,,,/SiriusStudioPlus;component/Themes/Dark.xaml",
                 })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });
        }
    }

    /// <summary>Where Studio keeps the user's settings, which the other programs read too.</summary>
    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiriusStudioPlus");

    private static CustomNodesViewModel CustomNodesForCurrentUser() =>
        new(
            Path.Combine(SettingsFolder, "custom-nodes.json"),
            Path.Combine(AppContext.BaseDirectory, "plugins"),
            Path.Combine(SettingsFolder, "plugins"));

    private static bool _reporting;

    /// <summary>Reports a fault and keeps running: a station going on inspecting matters more than one window.</summary>
    private static void Report(DispatcherUnhandledExceptionEventArgs e, string title)
    {
        e.Handled = true;

        if (_reporting)
            return;

        _reporting = true;
        Console.Error.WriteLine(e.Exception.ToString());

        try
        {
            Dialog.Error(e.Exception.Message, title);
        }
        finally
        {
            _reporting = false;
        }
    }
}
