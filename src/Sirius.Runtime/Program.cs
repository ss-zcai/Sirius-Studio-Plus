namespace Sirius.Runtime;

/// <summary>SiriusRuntime.exe: see <see cref="Sirius.Studio.App.Screens.RuntimeLauncher"/>.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args) => Sirius.Studio.App.Screens.RuntimeLauncher.Run(args);
}
