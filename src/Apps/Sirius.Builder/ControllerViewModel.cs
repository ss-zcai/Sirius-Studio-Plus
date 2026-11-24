using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Projects;
using Sirius.Plc;

namespace Sirius.Builder;

/// <summary>A controller as the Controllers panel lists it.</summary>
public sealed class ControllerViewModel : ObservableObject
{
    private readonly Project _project;

    public ControllerViewModel(Project project, Controller controller)
    {
        _project = project;
        Controller = controller;
    }

    public Controller Controller { get; }

    public string Name => Controller.Name;

    /// <summary>"Simulated PLC · Line 1", "Siemens S7 · 192.168.0.10".</summary>
    public string ConnectionText
    {
        get
        {
            var driver = PlcDrivers.All.FirstOrDefault(known => string.Equals(known.Key, Controller.Driver, StringComparison.OrdinalIgnoreCase)).Name
                         ?? $"{Controller.Driver} (not installed)";

            return Controller.Address.Length == 0 ? driver : $"{driver} · {Controller.Address}";
        }
    }

    public string WorkflowPath => _project.WorkflowPath(Controller);

    public string WorkflowFile => Path.GetFileName(WorkflowPath);

    public bool WorkflowMissing => !File.Exists(WorkflowPath);

    public bool IsSimulated => string.Equals(Controller.Driver, SimulatedPlcDriver.Key, StringComparison.OrdinalIgnoreCase);

    public PlcConnection Connection => new(Controller.Driver, Controller.Address, Controller.Options);

    public override string ToString() => Name;
}
