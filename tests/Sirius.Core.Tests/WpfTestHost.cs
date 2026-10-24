using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// One user-interface thread, shared by every test that builds real WPF.
/// </summary>
/// <remarks>
/// <para>
/// Each case used to get a thread of its own, which worked until a test
/// showed a real window. WPF - and AvalonDock in particular - keeps static,
/// thread-affine state: <c>Application.Current</c> is one per process, and
/// AvalonDock's focus manager hooks the input manager and holds dispatcher
/// references across docking managers. Give it a second UI thread and it
/// calls back into the first, which by then has gone; the blocking call
/// fails inside a window procedure where nothing can catch it, and the test
/// host dies rather than a test failing.
/// </para>
/// <para>
/// So there is exactly one UI thread, it lives for the whole collection, and
/// it runs a real message loop - which is also what the application does.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfTestHost>
{
    public const string Name = "WPF controls";
}

public sealed class WpfTestHost : IDisposable
{
    private static Dispatcher? _dispatcher;

    private readonly Thread _thread;

    public WpfTestHost()
    {
        var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            // The application has one, and code under test posts to it.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            // Created here so its thread affinity matches the controls'.
            if (Application.Current is null)
                _ = new Application();

            // Or the first test to close its window shuts the application
            // down, after which Application.Current is null again, the next
            // test tries to build a second one, and no window can be shown.
            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _dispatcher = dispatcher;
            ready.Set();

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
            throw new Xunit.Sdk.XunitException("the test UI thread never started");
    }

    /// <summary>The shared user-interface thread's dispatcher.</summary>
    public static Dispatcher Dispatcher =>
        _dispatcher ?? throw new Xunit.Sdk.XunitException("no UI thread");

    public void Dispose()
    {
        if (_dispatcher is { } dispatcher)
        {
            dispatcher.Invoke(() => Settle(dispatcher));
            dispatcher.InvokeShutdown();
        }

        _thread.Join(TimeSpan.FromSeconds(10));
        _dispatcher = null;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the shared user-interface thread and
    /// rethrows whatever it threw.
    /// </summary>
    /// <remarks>
    /// The wait is bounded so a deadlock fails by name instead of stopping
    /// the whole run, and the thread is a background one so a wedged case
    /// cannot keep the process alive.
    /// </remarks>
    public static void OnStaThread(Action action, int timeoutSeconds = 60)
    {
        var dispatcher = _dispatcher
            ?? throw new Xunit.Sdk.XunitException(
                "no UI thread - the test class needs [Collection(WpfCollection.Name)]");

        var operation = dispatcher.InvokeAsync(action);

        if (!operation.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            throw new Xunit.Sdk.XunitException(
                $"the UI thread did not finish within {timeoutSeconds}s");

        if (operation.Task.Exception is { } failure)
            throw new Xunit.Sdk.XunitException(failure.GetBaseException().ToString());
    }

    /// <summary>
    /// Runs everything the dispatcher has queued, and the Win32 messages
    /// behind it, until nothing is left.
    /// </summary>
    /// <remarks>
    /// A window is torn down over several passes, and AvalonDock finishes
    /// setting itself up on the render pass after one is shown. Invoking at
    /// <see cref="DispatcherPriority.SystemIdle"/> waits for every higher
    /// priority - input, render, loaded - and pushing a frame pumps the
    /// message queue, so both halves drain. Repeated, because work done in
    /// one pass can queue more.
    /// </remarks>
    public static void Settle(Dispatcher dispatcher, int passes = 5)
    {
        for (var pass = 0; pass < passes; pass++)
            dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    /// <summary>
    /// Merges the theme dictionaries, because control markup resolves brushes
    /// and styles through <see cref="Application.Current"/>.
    /// </summary>
    /// <remarks>
    /// Loaded by pack URI, from the compiled application assembly, rather than
    /// from the .xaml files on disk. A loose file resolves a bare
    /// <c>clr-namespace:</c> against whichever assembly is doing the loading -
    /// here the test assembly - so the converters the theme declares would come
    /// back as unknown types. The pack URI also means this exercises the markup
    /// that actually ships.
    /// </remarks>
    public static void EnsureApplicationResources()
    {
        if (Application.Current is null)
        {
            _ = new Application();
            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var resources = Application.Current!.Resources;

        if (resources.MergedDictionaries.Count > 0)
            return;

        foreach (var name in new[] { "Icons.xaml", "Dark.xaml" })
        {
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/SiriusStudioPlus;component/Themes/{name}",
                    UriKind.Absolute),
            });
        }
    }
}
