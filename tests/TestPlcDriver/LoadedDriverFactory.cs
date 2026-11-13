using Sirius.Plc;

namespace TestPlcDriver;

/// <summary>A driver as a library declares one: found by the loader, made through the program's interface.</summary>
public sealed class LoadedDriverFactory : IPlcDriverFactory
{
    public string Key => "loaded-plc";

    public string Name => "Loaded PLC";

    public string AddressHint => "Any name.";

    public string AddressExamples => "M0.0";

    public IReadOnlyList<PlcDriverOption> Options { get; } = [new("unit", "Unit", "1", "A setting of its own.")];

    public IPlcDriver Create(PlcConnection connection) => new LoadedDriver(connection.Option("unit") ?? "?");

    private sealed class LoadedDriver(string unit) : IPlcDriver
    {
        private readonly SimulatedPlc _plc = new();

        public string Description => $"loaded PLC, unit {unit}";

        public bool IsConnected => _plc.IsConnected;

        public Task ConnectAsync(CancellationToken cancellationToken) => _plc.ConnectAsync(cancellationToken);

        public Task DisconnectAsync() => _plc.DisconnectAsync();

        public string? AddressProblem(PlcVariable variable) => _plc.AddressProblem(variable);

        public Task<object> ReadAsync(PlcVariable variable, CancellationToken cancellationToken) => _plc.ReadAsync(variable, cancellationToken);

        public Task WriteAsync(PlcVariable variable, object value, CancellationToken cancellationToken) => _plc.WriteAsync(variable, value, cancellationToken);

        public ValueTask DisposeAsync() => _plc.DisposeAsync();
    }
}

/// <summary>A key no project could keep: refused when the library is loaded.</summary>
public sealed class BadlyKeyedFactory : IPlcDriverFactory
{
    public string Key => "Bench PLC";

    public string Name => "Badly Keyed";

    public string AddressHint => string.Empty;

    public string AddressExamples => string.Empty;

    public IReadOnlyList<PlcDriverOption> Options { get; } = [];

    public IPlcDriver Create(PlcConnection connection) => new SimulatedPlc();
}
