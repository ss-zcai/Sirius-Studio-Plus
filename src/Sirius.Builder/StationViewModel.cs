using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Projects;

namespace Sirius.Builder;

/// <summary>A station as Builder's Stations panel lists it.</summary>
public sealed class StationViewModel : ObservableObject
{
    private readonly Project _project;
    private int _lineChanges;

    public StationViewModel(Project project, Station station)
    {
        _project = project;
        Station = station;
        RecipePath = project.RecipePath(station);
        RefreshLine();

        station.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Station.Name))
                OnPropertyChanged(nameof(Name));
        };
    }

    public Station Station { get; }

    public string Name => Station.Name;

    /// <summary>The recipe file in full, for the tooltip.</summary>
    public string RecipePath { get; }

    /// <summary>"Top.sgraph".</summary>
    public string RecipeFile => Path.GetFileName(RecipePath);

    public bool RecipeMissing => !File.Exists(RecipePath);

    /// <summary>How many of its settings were changed on the line, over the recipe's own.</summary>
    public int LineChanges
    {
        get => _lineChanges;
        private set
        {
            if (SetProperty(ref _lineChanges, value))
            {
                OnPropertyChanged(nameof(HasLineChanges));
                OnPropertyChanged(nameof(LineChangesText));
            }
        }
    }

    public bool HasLineChanges => _lineChanges > 0;

    /// <summary>"2 changed on the line".</summary>
    public string LineChangesText => $"{_lineChanges} changed on the line";

    /// <summary>Reads its line settings file again, and whether its recipe is there.</summary>
    public void RefreshLine()
    {
        LineChanges = LineSettingsViewModel.CountFor(_project, Station);
        OnPropertyChanged(nameof(RecipeMissing));
    }
}
