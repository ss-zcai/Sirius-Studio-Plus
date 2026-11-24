using System.Runtime.CompilerServices;
using Emgu.CV;
using Sirius.Core.Graph;

namespace Sirius.Vision;

/// <summary>
/// Whether OpenCV's native library loads on this PC, and if not, why.
/// </summary>
/// <remarks>
/// <para>
/// Every image node calls into <c>cvextern.dll</c>, which Emgu CV loads the
/// first time it is used. When it cannot be loaded, each node fails with the
/// same unhelpful "type initializer threw an exception", and the cause is
/// two levels down. Checking once at start-up, and saying what to do, turns
/// that into one message with a fix in it.
/// </para>
/// <para>
/// The commonest cause is not the application at all: cvextern needs
/// Windows Media Foundation (MFPlat, MF, MFReadWrite), which Windows "N"
/// editions and Windows Server leave out unless it is added. The Visual C++
/// runtime it also needs ships with the application.
/// </para>
/// </remarks>
public static class NativeLibraries
{
    /// <summary>
    /// Null when OpenCV loads; otherwise what went wrong and what to do.
    /// </summary>
    public static string? Problem()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(CvInvoke).TypeHandle);
            return null;
        }
        catch (Exception ex)
        {
            return Explain(ex, HasMediaFoundation(), HasCvExtern());
        }
    }

    /// <summary>
    /// What to tell somebody whose OpenCV did not load, given what is
    /// missing from the PC.
    /// </summary>
    public static string Explain(Exception failure, bool hasMediaFoundation, bool hasCvExtern)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var advice = (hasMediaFoundation, hasCvExtern) switch
        {
            (_, false) =>
                "cvextern.dll is missing from the application folder. Extract the whole zip again, and check " +
                "whether antivirus software has quarantined it.",

            (false, _) =>
                "Windows Media Foundation is missing from this PC - a Windows \"N\" edition, or Windows Server " +
                "without the Media Foundation feature. Install the Media Feature Pack (Settings > Apps > " +
                "Optional features > Add a feature > Media Feature Pack), or on Windows Server add the " +
                "Media Foundation feature, then restart Windows.",

            _ =>
                "The cause is above. Send this message to whoever supports the application.",
        };

        return "Image processing is unavailable: OpenCV's native library could not be loaded, so every " +
               "image node will fail.\n\n" +
               advice + "\n\n" +
               "Details: " + ExceptionText.Describe(failure);
    }

    private static bool HasMediaFoundation() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll"));

    /// <summary>Beside the application in a published build, under runtimes in a development one.</summary>
    private static bool HasCvExtern()
    {
        var root = AppContext.BaseDirectory;

        return File.Exists(Path.Combine(root, "cvextern.dll")) ||
               File.Exists(Path.Combine(root, "runtimes", "win-x64", "native", "cvextern.dll"));
    }
}
