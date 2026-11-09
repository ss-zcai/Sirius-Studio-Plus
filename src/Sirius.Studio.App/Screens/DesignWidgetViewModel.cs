using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Screens;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A widget on the designer's canvas: where it is, and what it is bound to.
/// </summary>
/// <remarks>
/// Each property set from the properties panel is one undoable step, made
/// through <see cref="ScreenDesignerViewModel.Edit"/>. Moving and resizing
/// on the canvas go through <see cref="Place"/>, inside a gesture the
/// window opens and closes, so a drag is one step too.
/// </remarks>
public sealed class DesignWidgetViewModel : ObservableObject
{
    /// <summary>The smallest a widget can be made, in screen units.</summary>
    public const double MinimumSize = 20;

    private readonly ScreenDesignerViewModel _owner;
    private bool _isSelected;
    private IReadOnlyList<BindingChoice> _choices = [];
    private VisionGraph? _choicesFrom;

    public DesignWidgetViewModel(Widget widget, ScreenDesignerViewModel owner)
    {
        Widget = widget;
        _owner = owner;
        BrowseCommand = new RelayCommand(Browse);
        RefreshChoices();
    }

    public Widget Widget { get; }

    public WidgetKind Kind => Widget.Kind;

    public double X => Widget.X;

    public double Y => Widget.Y;

    public double Width => Widget.Width;

    public double Height => Widget.Height;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // ---- what the canvas shows -----------------------------------------

    /// <summary>"Picture", "PASS / FAIL" - what kind of widget it is.</summary>
    public string KindLabel => Kind switch
    {
        WidgetKind.Image => "Picture",
        WidgetKind.Verdict => "PASS / FAIL",
        WidgetKind.Counter => "Total · Pass · Fail · Yield",
        WidgetKind.Button => "Button",
        WidgetKind.Setting => "Setting",
        WidgetKind.Logo => "Logo",
        WidgetKind.Value => "Value",
        _ => "Text",
    };

    /// <summary>What it shows: "Top › Diameter", "Trigger · all stations", the text itself.</summary>
    public string Caption => Kind switch
    {
        WidgetKind.Text => Widget.Text ?? string.Empty,
        WidgetKind.Button => $"{ButtonCaption}  ·  {(Widget.Station ?? "all stations")}",
        WidgetKind.Logo => Widget.File ?? "no picture chosen",
        _ when ScreenLayout.NeedsName(Kind) => $"{Widget.Station ?? "?"} › {Widget.Name ?? "?"}",
        _ => Widget.Station ?? "?",
    };

    private string ButtonCaption => !string.IsNullOrWhiteSpace(Widget.Text)
        ? Widget.Text!
        : Widget.Action switch
        {
            WidgetAction.RunStop => "Run / Stop",
            WidgetAction.ResetCounts => "Reset Counts",
            _ => "Trigger",
        };

    /// <summary>The logo itself, for a Logo on the canvas; null for anything else or a picture not found.</summary>
    public string? LogoPath =>
        Kind == WidgetKind.Logo && !string.IsNullOrWhiteSpace(Widget.File) &&
        System.IO.File.Exists(Path.Combine(_owner.Recipes.Project.Folder, Widget.File!))
            ? Path.GetFullPath(Path.Combine(_owner.Recipes.Project.Folder, Widget.File!))
            : null;

    // ---- which properties it has ----------------------------------------

    public bool HasStation => ScreenLayout.NeedsStation(Kind) || Kind == WidgetKind.Button;

    public bool HasName => ScreenLayout.NeedsName(Kind);

    public bool HasText => Kind is WidgetKind.Text or WidgetKind.Button or WidgetKind.Value or WidgetKind.Setting;

    public bool HasAction => Kind == WidgetKind.Button;

    public bool HasNumberFormat => Kind is WidgetKind.Value or WidgetKind.Setting;

    public bool HasFile => Kind == WidgetKind.Logo;

    public bool HasFontSize => Kind is not (WidgetKind.Image or WidgetKind.Logo);

    /// <summary>What the Text box is called for this kind.</summary>
    public string TextLabel => Kind switch
    {
        WidgetKind.Button => "Caption (blank for the usual)",
        WidgetKind.Value or WidgetKind.Setting => "Label (blank for the name)",
        _ => "Text",
    };

    public IReadOnlyList<StationOption> StationOptions =>
        Kind == WidgetKind.Button ? _owner.ButtonStationOptions : _owner.StationOptions;

    // ---- the properties ---------------------------------------------------

    /// <summary>The station, as the station list's value: empty is every station, for a button.</summary>
    public string StationValue
    {
        get => Widget.Station ?? string.Empty;
        set
        {
            var station = string.IsNullOrEmpty(value) ? null : value;

            if (station == Widget.Station)
                return;

            _owner.Edit(() => Widget.Station = station);
            RefreshChoices();
            Raise();
        }
    }

    /// <summary>What the widget can show from its station's recipe: published names, then what would be published.</summary>
    public IReadOnlyList<BindingChoice> Choices => _choices;

    /// <summary>The choice it is bound to, or null when its name is not found.</summary>
    public BindingChoice? Choice
    {
        get => _choices.FirstOrDefault(choice =>
            choice.IsPublished && string.Equals(choice.Name, Widget.Name, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || _choicesFrom is not { } recipe || Widget.Station is not { } station)
                return;

            if (value.IsPublished && string.Equals(value.Name, Widget.Name, StringComparison.OrdinalIgnoreCase))
                return;

            // Binding publishes when it has to: the output is labelled in
            // its recipe, and nothing is wired.
            var (name, published) = ScreenBinding.Bind(recipe, value);

            if (published)
            {
                var written = _owner.Recipes.Published(station, recipe);

                _owner.StatusText = written
                    ? $"Published {value.Source} as '{name}' in {station}'s recipe, and saved it."
                    : $"Published {value.Source} as '{name}'. Save the recipe in Studio to keep it.";
            }

            _owner.Edit(() => Widget.Name = name);
            RefreshChoices();
            Raise();
        }
    }

    /// <summary>Said under the name when it is not among the station's published names.</summary>
    public string? NameProblem =>
        HasName && !string.IsNullOrWhiteSpace(Widget.Name) && Choice is null
            ? $"'{Widget.Name}' is not published by {Widget.Station ?? "any station"}."
            : null;

    public string? Text
    {
        get => Widget.Text;
        set => Set(() => Widget.Text = string.IsNullOrEmpty(value) ? null : value, Widget.Text, value);
    }

    public WidgetAction Action
    {
        get => Widget.Action ?? WidgetAction.Trigger;
        set => Set(() => Widget.Action = value, Widget.Action, value);
    }

    public double FontSize
    {
        get => Widget.FontSize;
        set => Set(() => Widget.FontSize = Math.Clamp(value, 6, 200), Widget.FontSize, value);
    }

    public string? Format
    {
        get => Widget.Format;
        set => Set(() => Widget.Format = string.IsNullOrWhiteSpace(value) ? null : value, Widget.Format, value);
    }

    public string? Unit
    {
        get => Widget.Unit;
        set => Set(() => Widget.Unit = string.IsNullOrWhiteSpace(value) ? null : value, Widget.Unit, value);
    }

    public string? File
    {
        get => Widget.File;
        set => Set(() => Widget.File = string.IsNullOrWhiteSpace(value) ? null : value, Widget.File, value);
    }

    /// <summary>Position and size, typed in the properties panel.</summary>
    public double EditX
    {
        get => Widget.X;
        set => _owner.Edit(() => Place(value, Y, Width, Height));
    }

    public double EditY
    {
        get => Widget.Y;
        set => _owner.Edit(() => Place(X, value, Width, Height));
    }

    public double EditWidth
    {
        get => Widget.Width;
        set => _owner.Edit(() => Place(X, Y, value, Height));
    }

    public double EditHeight
    {
        get => Widget.Height;
        set => _owner.Edit(() => Place(X, Y, Width, value));
    }

    public IRelayCommand BrowseCommand { get; }

    /// <summary>
    /// Puts the widget at a place and size, kept on the canvas and no
    /// smaller than <see cref="MinimumSize"/>. Not undoable on its own: the
    /// caller is inside an edit or a gesture.
    /// </summary>
    public void Place(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, MinimumSize, _owner.Width);
        height = Math.Clamp(height, MinimumSize, _owner.Height);
        x = Math.Clamp(x, 0, _owner.Width - width);
        y = Math.Clamp(y, 0, _owner.Height - height);

        if (x == Widget.X && y == Widget.Y && width == Widget.Width && height == Widget.Height)
            return;

        Widget.X = x;
        Widget.Y = y;
        Widget.Width = width;
        Widget.Height = height;

        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(EditX));
        OnPropertyChanged(nameof(EditY));
        OnPropertyChanged(nameof(EditWidth));
        OnPropertyChanged(nameof(EditHeight));

        _owner.Changed();
    }

    /// <summary>Reads the station's recipe again for what the widget can show.</summary>
    internal void RefreshChoices()
    {
        _choicesFrom = HasName ? _owner.Recipes.Recipe(Widget.Station) : null;
        _choices = _choicesFrom is null ? [] : ScreenBinding.Choices(_choicesFrom, Kind);

        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Choice));
        OnPropertyChanged(nameof(NameProblem));
    }

    /// <summary>
    /// Picks a picture for a logo. One from outside the project is copied
    /// into its screens folder, so the project folder stays complete.
    /// </summary>
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Title = "Logo",
        };

        if (dialog.ShowDialog() != true)
            return;

        var folder = _owner.Recipes.Project.Folder;
        var chosen = Path.GetFullPath(dialog.FileName);
        string relative;

        if (chosen.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetRelativePath(folder, chosen);
        }
        else
        {
            var target = Path.Combine(folder, ScreenLayout.ScreensFolder, Path.GetFileName(chosen));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(chosen, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.StatusText = $"Could not copy the picture into the project: {ex.Message}";
                return;
            }

            relative = Path.GetRelativePath(folder, target);
        }

        File = relative.Replace('\\', '/');
    }

    private void Set<T>(Action change, T? current, T? wanted)
    {
        if (EqualityComparer<T?>.Default.Equals(current, wanted))
            return;

        _owner.Edit(change);
        Raise();
    }

    /// <summary>Everything shown from the widget, after any change to it.</summary>
    private void Raise()
    {
        OnPropertyChanged(string.Empty);
    }
}
