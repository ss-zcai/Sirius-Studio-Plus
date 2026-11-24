using System.IO;
using Sirius.Core.Projects;
using Sirius.Core.Screens;

namespace Sirius.Builder;

/// <summary>A screen as the Project panel lists it: the start screen, a page or a popup.</summary>
public sealed class ScreenItemViewModel
{
    public ScreenItemViewModel(Project project, string name)
    {
        Name = name;
        FilePath = ScreenSet.PathFor(project, name);

        if (!File.Exists(FilePath))
        {
            // Only the start screen is listed without a file: made from the stations when first shown.
            Info = new ScreenInfo(name, ScreenKind.Page, 1280, 800);
            Problem = IsStart ? null : "file missing";
            return;
        }

        try
        {
            var screen = Screen.Load(FilePath);
            screen.Name = name;
            Info = ScreenSet.Info(screen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Info = new ScreenInfo(name, ScreenKind.Page, 1280, 800);
            Problem = "cannot be read";
        }
    }

    public string Name { get; }

    public string FilePath { get; }

    /// <summary>Its kind and size as saved.</summary>
    public ScreenInfo Info { get; }

    public bool IsStart => ScreenSet.IsStart(Name);

    public bool IsPopup => Info.Kind == ScreenKind.Popup;

    /// <summary>The start screen keeps its name and cannot be removed.</summary>
    public bool CanChange => !IsStart;

    /// <summary>"Start page", "Page", "Popup · 640 × 420".</summary>
    public string KindText => IsStart ? "Start page" : IsPopup ? $"Popup · {Info.Width:0} × {Info.Height:0}" : "Page";

    /// <summary>What is wrong with its file, or null.</summary>
    public string? Problem { get; }

    public bool HasProblem => Problem is not null;

    public override string ToString() => Name;
}
