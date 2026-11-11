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

            if (e.PropertyName == nameof(Station.Saving))
            {
                OnPropertyChanged(nameof(SavesImages));
                OnPropertyChanged(nameof(SavingText));
            }
        };
    }

    public Station Station { get; }

    public string Name => Station.Name;

    /// <summary>The recipe file in full, for the tooltip.</summary>
    public string RecipePath { get; }

    /// <summary>"Top.sgraph".</summary>
    public string RecipeFile => Path.GetFileName(RecipePath);

    public bool RecipeMissing => !File.Exists(RecipePath);

    /// <summary>Whether it keeps any pictures when it runs.</summary>
    public bool SavesImages => Station.Saving.IsOn;

    /// <summary>"Saves failed images, PNG, 30 days".</summary>
    public string SavingText => Station.Saving.Summary;

    /// <summary>Where its pictures go.</summary>
    public string ImagesFolder => Station.Saving.FolderFor(_project, Station);

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
