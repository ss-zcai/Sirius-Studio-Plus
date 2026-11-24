using System.IO;
using Sirius.UI;
using Sirius.UI.Hosting;

namespace Sirius.Builder;

/// <summary>
/// SiriusBuilder.exe: builds a Sirius project - stations, screens, line
/// settings - from recipes made in Sirius Studio, and test-runs it.
/// </summary>
/// <remarks>
/// <c>SiriusBuilder.exe "C:\Lines\Line 3\Line 3.sproj"</c> opens that
/// project; with none named, the one open when Builder last closed.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = SiriusHost.CreateApplication(BuilderViewModel.AppTitle);

        if (SiriusHost.NativeProblem() is { } native)
        {
            Dialog.Error(native, BuilderViewModel.AppTitle);
            return 2;
        }

        // Recipes are read, and test-run, as Studio reads and runs them.
        SiriusHost.LoadLibraries();

        var model = new BuilderViewModel();
        var window = new BuilderWindow(model);

        // Where it was, maximised or not, as Studio does.
        Sirius.UI.Layout.WindowPlacement.Remember(window, Path.Combine(SiriusHost.SettingsFolder, "builder-window.json"));

        window.Closed += (_, _) => app.Shutdown();

        window.Loaded += (_, _) =>
        {
            var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? BuilderViewModel.LastProject();

            if (path is not null && File.Exists(path))
                model.OpenProjectFile(path);
        };

        app.Run(window);

        SiriusHost.ShutDown();
        return 0;
    }
}
