using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Projects;

namespace Sirius.Builder;

/// <summary>A project camera as the Project panel lists it.</summary>
public sealed class CameraUnitViewModel : ObservableObject
{
    private readonly Project _project;

    public CameraUnitViewModel(Project project, CameraUnit camera)
    {
        _project = project;
        Camera = camera;
    }

    public CameraUnit Camera { get; }

    public string Name => Camera.Name;

    /// <summary>"Basler acA1920-40gm [23456789]", or that none is chosen.</summary>
    public string DeviceText => Camera.Setup.HasDevice ? Camera.Setup.Device : "no camera chosen";

    public bool HasDevice => Camera.Setup.HasDevice;

    /// <summary>"exposure 8000 µs · gain 2 dB", or "settings as the recipe".</summary>
    public string SettingsText
    {
        get
        {
            var setup = Camera.Setup;

            if (setup.AllAsRecipe)
                return "settings as the recipe";

            var parts = new List<string>();

            if (setup.Exposure is { } exposure)
                parts.Add($"exposure {exposure.ToString("0.##", CultureInfo.CurrentCulture)} µs");

            if (setup.Gain is { } gain)
                parts.Add($"gain {gain.ToString("0.##", CultureInfo.CurrentCulture)} dB");

            if (setup.Trigger is { } trigger)
                parts.Add(trigger == "FreeRun" ? "free run" : $"{trigger.ToLowerInvariant()} trigger");

            if (setup.Timeout is { } timeout)
                parts.Add($"timeout {timeout} ms");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>"for Top, Bottom", or that no station uses it.</summary>
    public string UsedByText
    {
        get
        {
            var stations = _project.StationsUsing(Camera);
            return stations.Count == 0 ? "no station uses it" : "for " + string.Join(", ", stations.Select(station => station.Name));
        }
    }

    public override string ToString() => Name;
}
