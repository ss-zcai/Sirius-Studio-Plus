using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Projects;
using Sirius.Plc;
using Sirius.Plc.Workflows;

namespace Sirius.Builder;

/// <summary>One address of the simulated PLC's memory, as the PLC window lists it.</summary>
public sealed record MemoryRow(string Address, string Value, string Type, bool IsBit);

/// <summary>A choice of PLC type, with the name a PLC programmer reads.</summary>
public sealed record PlcTypeChoice(PlcType Type, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The PLC window: a controller's connection tried, any address read or
/// written as any type, and - for a simulated PLC - its memory, shown and
/// set by hand. During a test run, also the controller's workflow log.
/// </summary>
/// <remarks>
/// <para>
/// Reads and writes go through a connection of the window's own, made by
/// the controller's driver - so trying a real PLC here is exactly what the
/// workflow will do, without the workflow.
/// </para>
/// <para>
/// A simulated PLC's memory is the one its workflow sees
/// (<see cref="SimulatedPlcMemory.Named"/>): set M0.0 here during a test
/// run, and the workflow's Monitor fires.
/// </para>
/// </remarks>
public sealed partial class PlcViewModel : ObservableObject, IAsyncDisposable
{
    private const int LinesKept = 200;

    private readonly Project _project;
    private readonly Dispatcher _dispatcher;
    private IPlcDriver? _driver;
    private SimulatedPlcMemory? _memory;

    [ObservableProperty]
    private string _connectionStatus = "Not connected.";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _address = "M0.0";

    [ObservableProperty]
    private PlcTypeChoice _type;

    [ObservableProperty]
    private string _length = "20";

    [ObservableProperty]
    private string _value = "true";

    [ObservableProperty]
    private string _readResult = string.Empty;

    public PlcViewModel(Project project, Controller controller, WorkflowRunner? runner = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Runner = runner;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _type = Types[0];

        Drivers = PlcDrivers.All.Select(driver => new PropertyOption(driver.Name, driver.Key)).ToArray();
        _driverKey = controller.Driver;
        _plcAddress = controller.Address;

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy);
        ReadCommand = new AsyncRelayCommand(ReadAsync, () => !IsBusy);
        WriteCommand = new AsyncRelayCommand(WriteAsync, () => !IsBusy);
        SetCommand = new RelayCommand(SetInMemory, () => IsSimulated);
        ToggleCommand = new RelayCommand<MemoryRow>(Toggle);
        ClearMemoryCommand = new RelayCommand(() => _memory?.Clear(), () => IsSimulated);
        PlugCommand = new RelayCommand(Plug, () => IsSimulated);

        AttachMemory();

        if (runner is not null)
        {
            foreach (var entry in runner.Log.TakeLast(LinesKept))
                WorkflowLog.Add(entry.ToString());

            runner.Logged += OnLogged;
        }
    }

    public Controller Controller { get; }

    /// <summary>The controller's workflow, running - during a test run - or null.</summary>
    public WorkflowRunner? Runner { get; }

    public bool HasRunner => Runner is not null;

    public string Title => $"{Controller.Name} - PLC";

    public static IReadOnlyList<PlcTypeChoice> Types { get; } =
        Enum.GetValues<PlcType>().Select(type => new PlcTypeChoice(type, WorkflowDocumentViewModel.PlcTypeLabel(type))).ToArray();

    // ---- the connection ----------------------------------------------------------------

    public IReadOnlyList<PropertyOption> Drivers { get; }

    private string _driverKey;
    private string _plcAddress;

    /// <summary>Which driver; changed here, it is the project's at once. Not while a test run uses it.</summary>
    public string DriverKey
    {
        get => _driverKey;
        set
        {
            if (value is null || !SetProperty(ref _driverKey, value))
                return;

            SaveConnection();
        }
    }

    /// <summary>Where the PLC is - an IP address; a simulated PLC's name.</summary>
    public string PlcAddress
    {
        get => _plcAddress;
        set
        {
            if (!SetProperty(ref _plcAddress, value?.Trim() ?? string.Empty))
                return;

            SaveConnection();
        }
    }

    /// <summary>The connection can be changed - not while a test run is using it.</summary>
    public bool CanChangeConnection => Runner is null;

    public string AddressHint => IsSimulated
        ? "Any name: two controllers with one name share one simulated PLC."
        : "The PLC's IP address.";

    public bool IsSimulated => string.Equals(_driverKey, SimulatedPlcDriver.Key, StringComparison.OrdinalIgnoreCase);

    public IAsyncRelayCommand TestConnectionCommand { get; }

    private void SaveConnection()
    {
        _project.SetConnection(Controller, _driverKey, _plcAddress);
        _ = DropConnectionAsync();
        AttachMemory();
        OnPropertyChanged(nameof(IsSimulated));
        OnPropertyChanged(nameof(AddressHint));
        SetCommand.NotifyCanExecuteChanged();
        ClearMemoryCommand.NotifyCanExecuteChanged();
        PlugCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Connects, with the controller's driver, and says how it went and how long it took.</summary>
    internal async Task<bool> TestConnectionAsync()
    {
        await DropConnectionAsync().ConfigureAwait(true);
        ConnectionStatus = "Connecting…";

        var connected = await ConnectAsync().ConfigureAwait(true);

        if (connected)
            Log($"Connected to {_driver!.Description}.");

        return connected;
    }

    private async Task<bool> ConnectAsync()
    {
        if (_driver is { IsConnected: true })
            return true;

        IsBusy = true;
        var clock = Stopwatch.StartNew();

        try
        {
            _driver ??= PlcDrivers.Create(new PlcConnection(_driverKey, _plcAddress, Controller.Options));

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _driver.ConnectAsync(limit.Token).ConfigureAwait(true);

            IsConnected = true;
            ConnectionStatus = $"Connected to {_driver.Description}, in {clock.ElapsedMilliseconds} ms.";
            return true;
        }
        catch (OperationCanceledException)
        {
            return Failed("No answer within 5 s.");
        }
        catch (PlcException ex)
        {
            return Failed(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        bool Failed(string why)
        {
            IsConnected = false;
            ConnectionStatus = $"Could not connect: {why}";
            Log(ConnectionStatus);
            return false;
        }
    }

    private async Task DropConnectionAsync()
    {
        if (_driver is { } driver)
        {
            _driver = null;
            await driver.DisposeAsync().ConfigureAwait(true);
        }

        IsConnected = false;
        ConnectionStatus = "Not connected.";
    }

    // ---- reading and writing ---------------------------------------------------------------

    public IAsyncRelayCommand ReadCommand { get; }

    public IAsyncRelayCommand WriteCommand { get; }

    public bool IsString => Type.Type == PlcType.String;

    partial void OnTypeChanged(PlcTypeChoice value) => OnPropertyChanged(nameof(IsString));

    partial void OnIsBusyChanged(bool value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        ReadCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
    }

    private PlcVariable Variable => new(
        Address.Trim(),
        Type.Type,
        Type.Type == PlcType.String && int.TryParse(Length, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : 0);

    /// <summary>Reads the address as the type chosen - connecting first if need be.</summary>
    internal async Task ReadAsync()
    {
        var variable = Variable;

        if (!await ReadyAsync(variable).ConfigureAwait(true))
            return;

        IsBusy = true;

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var value = await _driver!.ReadAsync(variable, limit.Token).ConfigureAwait(true);

            ReadResult = PlcValues.Text(value);
            Log($"Read {variable}: {ReadResult}");
        }
        catch (Exception ex) when (ex is PlcException or OperationCanceledException)
        {
            ReadResult = string.Empty;
            Log($"Could not read {variable}: {(ex is OperationCanceledException ? "no answer within 5 s." : ex.Message)}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the value, as the type chosen.</summary>
    internal async Task WriteAsync()
    {
        var variable = Variable;

        if (!await ReadyAsync(variable).ConfigureAwait(true))
            return;

        IsBusy = true;

        try
        {
            var value = PlcValues.Convert(Value, variable.Type);

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _driver!.WriteAsync(variable, value, limit.Token).ConfigureAwait(true);

            Log($"Wrote {variable}: {PlcValues.Text(value)}");
        }
        catch (Exception ex) when (ex is PlcException or OperationCanceledException)
        {
            Log($"Could not write {variable}: {(ex is OperationCanceledException ? "no answer within 5 s." : ex.Message)}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ReadyAsync(PlcVariable variable)
    {
        if (variable.Address.Length == 0)
        {
            Log("Type an address first.");
            return false;
        }

        return await ConnectAsync().ConfigureAwait(true);
    }

    // ---- the simulated PLC's memory ---------------------------------------------------------

    public ObservableCollection<MemoryRow> Memory { get; } = [];

    public IRelayCommand SetCommand { get; }

    public IRelayCommand<MemoryRow> ToggleCommand { get; }

    public IRelayCommand ClearMemoryCommand { get; }

    public IRelayCommand PlugCommand { get; }

    public string PlugText => _memory?.IsUnplugged == true ? "Plug In" : "Unplug";

    public string PlugTip => _memory?.IsUnplugged == true
        ? "Put the cable back: the PLC answers again, and the workflow reconnects by itself"
        : "Pull the cable out: nothing answers until it is plugged in - to see what the workflow does";

    /// <summary>Sets the address to the value, as the type chosen - as the PLC's own program would.</summary>
    internal void SetInMemory()
    {
        if (_memory is not { } memory)
            return;

        var variable = Variable;

        if (variable.Address.Length == 0 || variable.Address.Any(char.IsWhiteSpace))
        {
            Log("An address is a word with no spaces: M0.0, MW10, DB34.20.0.");
            return;
        }

        try
        {
            var value = PlcValues.Convert(Value, variable.Type);
            memory.Set(variable.Address, value);
            Log($"Set {variable.Address} to {PlcValues.Text(value)}.");
        }
        catch (PlcException ex)
        {
            Log(ex.Message);
        }
    }

    /// <summary>A bit flipped - the quickest way to raise a trigger by hand.</summary>
    internal void Toggle(MemoryRow? row)
    {
        if (row is null || _memory is not { } memory)
            return;

        var now = !PlcValues.IsOn(memory.Get(row.Address));
        memory.Set(row.Address, now);
        Log($"Set {row.Address} to {(now ? "true" : "false")}.");
    }

    private void Plug()
    {
        if (_memory is not { } memory)
            return;

        if (memory.IsUnplugged)
            memory.PlugIn();
        else
            memory.Unplug();

        Log(memory.IsUnplugged ? "Unplugged: the simulated PLC no longer answers." : "Plugged in again.");
    }

    private void AttachMemory()
    {
        if (_memory is { } old)
        {
            old.Changed -= OnMemoryChanged;
            old.PluggedChanged -= OnPluggedChanged;
        }

        _memory = IsSimulated ? SimulatedPlcMemory.Named(_plcAddress) : null;

        if (_memory is { } memory)
        {
            memory.Changed += OnMemoryChanged;
            memory.PluggedChanged += OnPluggedChanged;
        }

        ShowMemory();
        OnPluggedChanged(null, EventArgs.Empty);
    }

    private void OnMemoryChanged(object? sender, (string Address, object Value) change) =>
        _dispatcher.BeginInvoke(ShowMemory);

    private void OnPluggedChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(PlugText));
            OnPropertyChanged(nameof(PlugTip));
        });

    internal void ShowMemory()
    {
        Memory.Clear();

        foreach (var (address, value) in _memory?.Values ?? [])
            Memory.Add(new MemoryRow(address, PlcValues.Text(value), TypeOf(value), value is bool));
    }

    private static string TypeOf(object value) => value switch
    {
        bool => "Bool",
        byte => "Byte",
        short => "Int16",
        ushort => "UInt16",
        int => "Int32",
        uint => "UInt32",
        float => "Real",
        double => "LReal",
        string => "String",
        _ => value.GetType().Name,
    };

    // ---- the log --------------------------------------------------------------------------

    /// <summary>What this window did: connections, reads, writes, sets.</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>The workflow's own log, during a test run.</summary>
    public ObservableCollection<string> WorkflowLog { get; } = [];

    private void Log(string line)
    {
        Lines.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");

        while (Lines.Count > LinesKept)
            Lines.RemoveAt(0);
    }

    private void OnLogged(object? sender, WorkflowLogEntry entry)
    {
        if (entry.Level == WorkflowLogLevel.Trace)
            return;

        _dispatcher.BeginInvoke(() =>
        {
            WorkflowLog.Add(entry.ToString());

            while (WorkflowLog.Count > LinesKept)
                WorkflowLog.RemoveAt(0);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Runner is not null)
            Runner.Logged -= OnLogged;

        if (_memory is { } memory)
        {
            memory.Changed -= OnMemoryChanged;
            memory.PluggedChanged -= OnPluggedChanged;
        }

        await DropConnectionAsync().ConfigureAwait(true);
    }
}
