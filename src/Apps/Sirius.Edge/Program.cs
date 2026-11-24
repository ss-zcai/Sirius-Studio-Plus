using System.IO;
using Microsoft.Win32;
using Sirius.Core.Projects;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;
using Sirius.Studio.App.Screens;

namespace Sirius.Edge;

/// <summary>
/// SiriusEdge.exe: a Sirius project running on the machine, full screen,
/// for the operator.
/// </summary>
/// <remarks>
/// <c>SiriusEdge.exe "C:\Lines\Line 3\Line 3.sproj"</c>, from a shortcut or
/// a start-up task; with no project named, it asks for one. <c>--windowed</c>
/// shows it in an ordinary window instead, for trying it at a desk.
/// </remarks>
public static class Program
{
    public const string Title = "Sirius Edge";

    /// <returns>0 when it ran and closed, 1 when no project was chosen or one would not open, 2 when it could not start.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var app = SiriusHost.CreateApplication(Title);

        if (SiriusHost.NativeProblem() is { } native)
        {
            Dialog.Error(native, Title);
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
            Dialog.Error($"{path} could not be opened: {ex.Message}", Title);
            return 1;
        }

        SiriusHost.LoadLibraries();

        var windowed = args.Any(arg => string.Equals(arg, "--windowed", StringComparison.OrdinalIgnoreCase));
        var window = RuntimeWindow.Create(loaded.Project, fullScreen: !windowed, loaded.Warnings);

        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);

        SiriusHost.ShutDown();
        return 0;
    }

    private static string? AskForProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Project (*.sproj)|*.sproj",
            Title = $"{Title} - which project?",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
