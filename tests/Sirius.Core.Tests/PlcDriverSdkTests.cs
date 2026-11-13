using System.IO;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Plc;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The PLC driver SDK (docs/plc-drivers.md): a driver library found and
/// loaded, its settings in Builder, and the conformance check every driver
/// is run through.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PlcDriverSdkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-drivers-" + Guid.NewGuid().ToString("N"));

    public PlcDriverSdkTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        PlcNodeLibrary.Register();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    // ---- loading a driver library ---------------------------------------------------

    [Fact]
    public void A_driver_library_is_found_in_its_folder_and_its_drivers_are_made_known()
    {
        // TestPlcDriver: built against Sirius.Plc, not referenced by these tests - a library as the PLC team builds one.
        var folder = Path.Combine(AppContext.BaseDirectory, "plc-drivers-test");
        var library = Path.Combine(folder, "TestPlcDriver", "TestPlcDriver.dll");

        Assert.True(File.Exists(library), library);
        Assert.True(PlcDriverLoader.Probe(library).ReferencesPlc);
        Assert.Equal(PlcDriverInterface.Version.Major, PlcDriverLoader.Probe(library).PlcVersion!.Major);
        Assert.False(PlcDriverLoader.Probe(typeof(IPlcDriver).Assembly.Location).ReferencesPlc);

        PlcDriverLoader.LoadFolders(folder);

        // Found, in a context of its own, its settings declared.
        var loaded = PlcDrivers.Find("loaded-plc");
        Assert.NotNull(loaded);
        Assert.Equal("TestPlcDriver.LoadedDriverFactory", loaded!.GetType().FullName);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly));
        Assert.Equal("Unit", Assert.Single(loaded.Options).Label);

        // Its driver is made through the program's interface, its setting defaulted.
        var driver = PlcDrivers.Create(new PlcConnection("loaded-plc", "bench"));
        Assert.Equal("loaded PLC, unit 1", driver.Description);

        // The one with a key no project could keep is refused, in words.
        Assert.DoesNotContain(PlcDrivers.Factories, factory => factory.Name == "Badly Keyed");
        Assert.Contains(PlcDrivers.Problems, problem => problem.Contains("BadlyKeyedFactory's key 'Bench PLC'", StringComparison.Ordinal));

        // Loading it again adds nothing.
        Assert.Empty(PlcDriverLoader.Load(library));
    }

    [Theory]
    [InlineData("siemens-s7", null)]
    [InlineData("modbus-tcp2", null)]
    [InlineData("Siemens-S7", "must be lower case")]
    [InlineData("s7 plc", "must be lower case")]
    [InlineData("7siemens", "must be lower case")]
    [InlineData("", "is empty.")]
    [InlineData("simulated", "is the simulated PLC's.")]
    public void A_drivers_key_is_lower_case_words_joined_by_hyphens(string key, string? problem)
    {
        if (problem is null)
            Assert.Null(PlcDriverLoader.KeyProblem(key));
        else
            Assert.Contains(problem, PlcDriverLoader.KeyProblem(key));
    }

    [Fact]
    public void A_driver_is_given_its_settings_defaults_for_those_never_set()
    {
        PlcDrivers.Register(new BenchDriverFactory());

        var made = (BenchDriver)PlcDrivers.Create(new PlcConnection("bench-plc", "10.0.0.5", new Dictionary<string, string> { ["slot"] = "2" }));

        Assert.Equal("10.0.0.5", made.Connection.Address);
        Assert.Equal(("S7-1200", "0", "2"), (made.Connection.Option("cpu"), made.Connection.Option("rack"), made.Connection.Option("slot")));

        // Settings that cannot work are a PlcException, in words.
        var refused = Assert.Throws<PlcException>(() => PlcDrivers.Create(new PlcConnection("bench-plc", "10.0.0.5", new Dictionary<string, string> { ["cpu"] = "S5" })));
        Assert.Equal("The CPU 'S5' is not one this driver knows: S7-1200, S7-1500, S7-300.", refused.Message);
    }

    // ---- the conformance check -------------------------------------------------------------

    private static PlcDriverCheckPlan BenchPlan() => new()
    {
        WriteAndReadBack =
        {
            new(new PlcVariable("M10.0", PlcType.Bool), true),
            new(new PlcVariable("MW20", PlcType.Int16), (short)-1234),
            new(new PlcVariable("MD30", PlcType.Int32), 123456789),
            new(new PlcVariable("MD40", PlcType.Real), 3.25f),
            new(new PlcVariable("DB1.0", PlcType.String, 10), "PART-42"),
        },
        ReadOnly = { new PlcVariable("I0.0", PlcType.Bool) },
    };

    [Fact]
    public async Task The_simulated_plc_passes_every_check()
    {
        var results = await PlcDriverCheck.RunAsync(() => new SimulatedPlc(), BenchPlan());

        Assert.All(results, result => Assert.True(result.Passed, result.ToString()));
        Assert.Contains(results, result => result.Check == "Writes and reads back MD30 (Int32)");
        Assert.Contains(results, result => result.Check == "Disconnects, and connects again");
    }

    [Fact]
    public async Task A_sloppy_driver_fails_the_checks_it_should_and_says_why()
    {
        var plan = BenchPlan();
        plan.Missing = new PlcVariable("DB999.0", PlcType.Int16);

        var results = await PlcDriverCheck.RunAsync(() => new SloppyDriver(), plan);
        string Failure(string check) => Assert.Single(results, result => result.Check == check && !result.Passed).Detail;

        Assert.Equal("read back a Int32, not the Int16 a Int16 is carried as.", Failure("Writes and reads back MW20 (Int16)"));
        Assert.Equal("takes 'not an address' as an address; AddressProblem should say what is wrong with it.", Failure("Knows its own addresses"));
        Assert.Equal(
            "threw InvalidOperationException: no such block - a driver says what went wrong with a PlcException.",
            Failure("Refuses DB999.0 (Int16) with a PlcException"));
        Assert.Equal("read with a cancelled token instead of stopping.", Failure("Stops when cancelled"));
        Assert.Equal("IsConnected is still true after DisconnectAsync.", Failure("Disconnects, and connects again"));

        // What it does right, it passes.
        Assert.True(results.Single(result => result.Check == "Connects").Passed);
        Assert.True(results.Single(result => result.Check == "Writes and reads back MD30 (Int32)").Passed);
    }

    // ---- the driver's settings in Builder -----------------------------------------------

    [Fact]
    public void The_plc_window_shows_a_drivers_settings_and_keeps_them_with_the_controller()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            PlcDrivers.Register(new BenchDriverFactory());

            var project = Project.Create(_root, "Line", new VisionGraph(), "Top");
            var controller = project.AddController("Press PLC", "simulated", "Press", path => File.WriteAllText(path, "{}"));
            var plc = new PlcViewModel(project, controller);
            var window = new PlcWindow(plc);

            try
            {
                Assert.False(plc.HasOptions);

                plc.DriverKey = "bench-plc";
                plc.PlcAddress = "192.168.0.10";

                Assert.Equal(["CPU", "Rack", "Slot"], plc.Options.Select(row => row.Label));
                Assert.Equal(["S7-1200", "0", "1"], plc.Options.Select(row => row.Value));
                Assert.True(plc.Options[0].HasChoices);
                Assert.Equal("The PLC's IP address, as 192.168.0.10.", plc.AddressHint);
                Assert.Equal("In the PLC's words: M0.0, MW10, DB34.20.0", plc.AddressExamples);

                plc.Options[2].Value = "2";

                var saved = Project.Load(project.FilePath).Project.Controllers.Single();
                Assert.Equal(("bench-plc", "192.168.0.10"), (saved.Driver, saved.Address));
                Assert.Equal("2", saved.Options["slot"]);
                Assert.Equal("S7-1200", saved.Options["cpu"]);
            }
            finally
            {
                window.Close();
                WpfTestHost.Wait(plc.DisposeAsync().AsTask());
            }
        });
    }
}

/// <summary>A driver as a library would declare one: settings with defaults and choices.</summary>
public sealed class BenchDriverFactory : IPlcDriverFactory
{
    public string Key => "bench-plc";

    public string Name => "Bench PLC";

    public string AddressHint => "The PLC's IP address, as 192.168.0.10.";

    public string AddressExamples => "M0.0, MW10, DB34.20.0";

    public IReadOnlyList<PlcDriverOption> Options { get; } =
    [
        new("cpu", "CPU", "S7-1200", "Which family of CPU.", ["S7-1200", "S7-1500", "S7-300"]),
        new("rack", "Rack", "0", "The CPU's rack: 0 almost always."),
        new("slot", "Slot", "1", "The CPU's slot: 1 for an S7-1200 or 1500, 2 for an S7-300."),
    ];

    public IPlcDriver Create(PlcConnection connection)
    {
        var cpu = connection.Option("cpu");

        if (cpu is not null && Options[0].Choices!.All(choice => choice != cpu))
            throw new PlcException($"The CPU '{cpu}' is not one this driver knows: {string.Join(", ", Options[0].Choices!)}.");

        return new BenchDriver(connection);
    }
}

/// <summary>The bench driver: a simulated PLC that remembers what it was made with.</summary>
public sealed class BenchDriver(PlcConnection connection) : IPlcDriver
{
    private readonly SimulatedPlc _plc = new();

    public PlcConnection Connection => connection;

    public string Description => $"bench PLC at {connection.Address}";

    public bool IsConnected => _plc.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken) => _plc.ConnectAsync(cancellationToken);

    public Task DisconnectAsync() => _plc.DisconnectAsync();

    public string? AddressProblem(PlcVariable variable) => _plc.AddressProblem(variable);

    public Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken) => _plc.ReadAsync(variable, cancellationToken);

    public Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken) => _plc.WriteAsync(variable, value, cancellationToken);

    public ValueTask DisposeAsync() => _plc.DisposeAsync();
}

/// <summary>
/// The mistakes a first driver makes: an Int16 handed back as an int, any
/// text taken as an address, the wrong exception, a cancelled token
/// ignored, a disconnect that does not.
/// </summary>
internal sealed class SloppyDriver : IPlcDriver
{
    private readonly Dictionary<string, object> _memory = new(StringComparer.OrdinalIgnoreCase);

    public string Description => "sloppy PLC";

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public string? AddressProblem(PlcVariable variable) => null;

    public Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken)
    {
        if (variable.Address.StartsWith("DB999", StringComparison.Ordinal))
            throw new InvalidOperationException("no such block");

        var value = _memory.TryGetValue(variable.Address, out var kept) ? kept : PlcValues.Default(variable.Type);
        return Task.FromResult(value is short whole ? (object)(int)whole : value);
    }

    public Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken)
    {
        _memory[variable.Address] = value;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
