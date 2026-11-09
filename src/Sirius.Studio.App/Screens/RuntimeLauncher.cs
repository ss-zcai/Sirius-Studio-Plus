using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Sirius.Core.Projects;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Cameras;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// The runtime program: a project's screen, full screen, without Studio.
/// </summary>
/// <remarks>
/// <para>
/// <c>SiriusRuntime.exe "C:\Lines\Line 3\Line 3.sproj"</c>, from a shortcut
/// or a start-up task; with no project named, it asks for one.
/// <c>--windowed</c> shows it in an ordinary window instead, for trying it
/// at a desk.
/// </para>
/// <para>
/// Here rather than in the runtime's own project, so the program is a
/// single call and everything it does is in the same assembly as Studio -
/// the theme, the screens, the camera drivers and custom nodes, loaded the
/// way Studio loads them, so a recipe judges the same in both.
/// </para>
/// </remarks>
public static class RuntimeLauncher
{
    /// <summary>The runtime program's whole life. Call on an STA thread.</summary>
    /// <returns>0 when it ran and closed, 1 when no project was chosen or one would not open, 2 when it could not start.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadTheme(app);
        App.DropMenusFromTheLeft();
        app.DispatcherUnhandledException += OnUnhandledException;

        if (NativeLibraries.Problem() is { } native)
        {
            Dialog.Error(native, "Sirius Runtime");
            return 2;
        }

        var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? AskForProject();

        if (path is null)
            return 1;

        ProjectLoadResult loaded;

        try
        {
            loaded = Project.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialog.Error($"{path} could not be opened: {ex.Message}", "Sirius Runtime");
            return 1;
        }

        // As Studio loads them, so a recipe that uses a custom node or a
        // camera works here as it does there.
        VisionNodeLibrary.Register();
        CustomNodesForCurrentUser().LoadAll();
        CameraDrivers.LoadInstalled();

        var windowed = args.Any(arg => string.Equals(arg, "--windowed", StringComparison.OrdinalIgnoreCase));
        var window = RuntimeWindow.Create(loaded.Project, fullScreen: !windowed, loaded.Warnings);

        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);

        CameraDrivers.ShutDown();
        return 0;
    }

    /// <summary>
    /// The theme Studio's App.xaml merges, from Studio's assembly - so the
    /// runtime's controls look as they do in Studio.
    /// </summary>
    internal static void LoadTheme(Application app)
    {
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

    private static string? AskForProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Project (*.sproj)|*.sproj",
            Title = "Sirius Runtime - which project?",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static CustomNodesViewModel CustomNodesForCurrentUser()
    {
        var settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SiriusStudioPlus");

        return new CustomNodesViewModel(
            Path.Combine(settings, "custom-nodes.json"),
            Path.Combine(AppContext.BaseDirectory, "plugins"),
            Path.Combine(settings, "plugins"));
    }

    private static bool _reporting;

    /// <summary>Reports a fault and keeps running: a station going on inspecting matters more than one widget.</summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (_reporting)
            return;

        _reporting = true;
        Console.Error.WriteLine(e.Exception.ToString());

        try
        {
            Dialog.Error(e.Exception.Message, "Sirius Runtime");
        }
        finally
        {
            _reporting = false;
        }
    }
}
