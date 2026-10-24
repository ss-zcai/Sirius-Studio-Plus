using System.Windows;
using System.Windows.Threading;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The shell's answer to a viewer asking for a frame to profile.
/// </summary>
/// <remarks>
/// The viewer cannot capture the frame itself - the graph owns the buffer and
/// reclaims it between passes - so it raises a request and the shell turns
/// that into an evaluation pass. This drives the real view-model, with a real
/// dispatcher, because the request is answered asynchronously and a stub that
/// answers it synchronously would pass whether or not the application does.
/// </remarks>
[Collection(WpfCollection.Name)]
public class ShellProfileRequestTests
{
    public ShellProfileRequestTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static void OnStaThread(Action<Dispatcher> action) =>
        WpfTestHost.OnStaThread(() => action(WpfTestHost.Dispatcher));

    /// <summary>Runs the message loop until <paramref name="until"/> holds, or time runs out.</summary>
    private static bool Pump(Dispatcher dispatcher, Func<bool> until, int milliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (until())
                return true;

            dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        return until();
    }

    private static NodeDescriptor Descriptor(string name) =>
        NodeCatalog.All.First(descriptor => descriptor.DisplayName == name);

    [Fact]
    public void A_viewer_asking_to_profile_gets_a_frame()
    {
        OnStaThread(dispatcher =>
        {
            var shell = new MainViewModel();

            var source = shell.Editor.AddNode(Descriptor("Blank Image"), new Point(0, 0));
            var viewer = shell.Editor.AddNode(Descriptor("Image Viewer"), new Point(200, 0));

            Assert.True(shell.Editor.Graph.TryConnect(
                ((ConstantImageNode)source.Node).ImageOut,
                ((ViewerNode)viewer.Node).ImageIn).Success);

            shell.Editor.Rebuild();

            var panel = Pump(dispatcher, () => shell.NodePanels.OfType<ViewerPanelViewModel>().Any())
                ? shell.NodePanels.OfType<ViewerPanelViewModel>().Single()
                : throw new Xunit.Sdk.XunitException("the viewer node never produced a panel");

            Assert.True(
                Pump(dispatcher, () => panel.Image is not null),
                "the first pass never reached the panel");

            // What the viewer control does when the profile tool is picked.
            panel.IsProfilingEnabled = true;

            Assert.True(
                Pump(dispatcher, () => panel.ProfileSource is not null),
                "asking to profile never produced a frame to sample");
        });
    }
}
