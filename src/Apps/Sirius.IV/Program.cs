using System.IO;
using Sirius.Core.Licensing;
using Sirius.UI;
using Sirius.UI.Hosting;

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
    /// <summary>What Sirius IV's licence codes are made for: <c>LicenceMaker make --product IV</c>.</summary>
    public const string LicenceProduct = "IV";

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

        // A licence for this PC first (decision 97): without one, only the
        // licence window opens.
        var licence = LicenceStore.ForThisPc(LicenceProduct, BrowserViewModel.AppTitle);
        var state = licence.Check();

        if (!state.IsValid && !LicenceWindow.Ask(null, licence, state))
            return 3;

        var model = new BrowserViewModel(Path.Combine(SiriusHost.SettingsFolder, "sirius-iv.json"));
        var window = new ViewerWindow(model)
        {
            StartWith = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)),
        };

        window.WatchLicence(licence);

        // Where it was, maximised or not, as Studio does.
        Sirius.UI.Layout.WindowPlacement.Remember(window, Path.Combine(SiriusHost.SettingsFolder, "iv-window.json"));

        window.Closed += (_, _) => app.Shutdown();
        app.Run(window);
        return 0;
    }
}
