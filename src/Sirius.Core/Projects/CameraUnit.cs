using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Core.Projects;

/// <summary>
/// Which camera, and how it is set: what a camera unit holds (backlog F29,
/// runtime design section 15). Each setting left null is the recipe's own -
/// "As the recipe".
/// </summary>
/// <param name="Device">
/// The camera, as a Camera node names it: "Basler acA1920-40gm [23456789]",
/// found by the serial number in brackets. Empty until one is chosen.
/// </param>
/// <param name="Exposure">Exposure time in microseconds, or null for the recipe's.</param>
/// <param name="Gain">Gain in decibels, or null for the recipe's.</param>
/// <param name="Trigger">"FreeRun", "Software" or "Hardware", or null for the recipe's.</param>
/// <param name="Timeout">How long to wait for a frame, in milliseconds, or null for the recipe's.</param>
public sealed record CameraSetup(
    string Device,
    double? Exposure = null,
    double? Gain = null,
    string? Trigger = null,
    int? Timeout = null)
{
    /// <summary>No camera chosen, everything as the recipe.</summary>
    public static readonly CameraSetup None = new(string.Empty);

    public bool HasDevice => !string.IsNullOrWhiteSpace(Device);

    /// <summary>Every setting the recipe's: only the camera is the unit's.</summary>
    public bool AllAsRecipe => Exposure is null && Gain is null && Trigger is null && Timeout is null;

    /// <summary>Why it cannot be used, or null.</summary>
    public string? Problem()
    {
        if (Exposure is { } exposure && (exposure < 10 || exposure > 1_000_000 || !double.IsFinite(exposure)))
            return "Exposure must be between 10 and 1,000,000 µs.";

        if (Gain is { } gain && (gain < 0 || gain > 48 || !double.IsFinite(gain)))
            return "Gain must be between 0 and 48 dB.";

        if (Trigger is { } trigger && !Triggers.Contains(trigger, StringComparer.OrdinalIgnoreCase))
            return $"The trigger must be one of {string.Join(", ", Triggers)}.";

        if (Timeout is { } timeout && (timeout < 100 || timeout > 60_000))
            return "Timeout must be between 100 and 60,000 ms.";

        return null;
    }

    /// <summary>The triggers a Camera node knows, by name.</summary>
    public static readonly IReadOnlyList<string> Triggers = ["FreeRun", "Software", "Hardware"];
}

/// <summary>
/// A camera of a project: the camera a station takes its pictures from, and
/// how it is set - in Builder, without opening the recipe in Studio
/// (backlog F29).
/// </summary>
/// <remarks>
/// <para>
/// The camera belongs to the machine and the recipe to the judgement: a
/// camera replaced, or a recipe moved to another machine, changes the unit,
/// not the recipe. A station naming a unit has its recipe's Camera node
/// take the unit's camera and settings when it runs
/// (<see cref="Runtime.StationCamera"/>); Studio, opening the recipe on its
/// own, uses the node's own.
/// </para>
/// <para>
/// Written in the project file, as a controller's connection is.
/// </para>
/// </remarks>
public sealed class CameraUnit : ObservableObject
{
    private string _name;
    private CameraSetup _setup;

    public CameraUnit(string name, CameraSetup? setup = null)
    {
        _name = name;
        _setup = setup ?? CameraSetup.None;
    }

    /// <summary>What it is called: "Top camera".</summary>
    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    /// <summary>Which camera, and how it is set.</summary>
    public CameraSetup Setup
    {
        get => _setup;
        internal set => SetProperty(ref _setup, value ?? CameraSetup.None);
    }

    public override string ToString() => Name;
}
