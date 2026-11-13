using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Core.Projects;

/// <summary>
/// A controller of a project: one PLC - which driver talks to it, and
/// where it is - and the workflow that watches, reads and writes it and has
/// stations inspect (backlog F23, decision 74).
/// </summary>
/// <remarks>
/// The workflow is a file of its own, <c>controllers/&lt;name&gt;.sflow</c>;
/// the connection is the project's, written in the <c>.sproj</c>, so moving
/// a project to another line changes an address, not the workflow.
/// </remarks>
public sealed class Controller : ObservableObject
{
    private string _name;
    private string _driver;
    private string _address;
    private IReadOnlyDictionary<string, string> _options;

    public Controller(string name, string driver, string address, string workflow, IReadOnlyDictionary<string, string>? options = null)
    {
        _name = name;
        _driver = driver;
        _address = address;
        Workflow = workflow;
        _options = options ?? new Dictionary<string, string>();
    }

    /// <summary>What it is called: "Line 1 PLC".</summary>
    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    /// <summary>The driver's key: "simulated", "siemens-s7".</summary>
    public string Driver
    {
        get => _driver;
        internal set => SetProperty(ref _driver, value);
    }

    /// <summary>Where the PLC is: an IP address; a simulated PLC's name.</summary>
    public string Address
    {
        get => _address;
        internal set => SetProperty(ref _address, value);
    }

    /// <summary>The driver's own settings by name: a rack and slot, a unit ID.</summary>
    public IReadOnlyDictionary<string, string> Options
    {
        get => _options;
        internal set => SetProperty(ref _options, value);
    }

    /// <summary>Its workflow, relative to the project folder, with forward slashes.</summary>
    public string Workflow { get; }

    public override string ToString() => Name;
}
