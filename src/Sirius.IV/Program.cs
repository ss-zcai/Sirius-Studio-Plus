using System.IO;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;

namespace Sirius.IV;

/// <summary>
/// SiriusIV.exe: Sirius IV, the image viewer. Browses a folder of pictures -
/// a station's saved ones, with their annotations - one at a time or several
/// side by side.
/// </summary>
/// <remarks>
/// <c>SiriusIV.exe "D:\Line 3\images\Top"</c> opens that folder; a picture's
/// path opens its folder with it chosen; with nothing named, the folder open
/// when it last closed.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = SiriusHost.CreateApplication(BrowserViewModel.AppTitle);

        // Pictures are read through OpenCV, as Studio reads them.
        if (SiriusHost.NativeProblem() is { } native)
        {
            Dialog.Error(native, BrowserViewModel.AppTitle);
            return 2;
        }

        var model = new BrowserViewModel(Path.Combine(SiriusHost.SettingsFolder, "sirius-iv.json"));
        var window = new ViewerWindow(model)
        {
            StartWith = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)),
        };

        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);
        return 0;
    }
}
