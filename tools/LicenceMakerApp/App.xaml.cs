using System.Windows;
using Sirius.Tools.LicenceMaker;

namespace Sirius.Tools.LicenceMakerApp;

/// <summary>Sirius Licence Maker: one window, on the key where it is kept.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow(new LicenceMakerViewModel(Signing.DefaultKeyFile));
        MainWindow = window;
        window.Show();
    }
}
