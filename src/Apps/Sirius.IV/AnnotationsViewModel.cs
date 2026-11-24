using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Annotations;
using Sirius.Core.Shapes;

namespace Sirius.IV;

/// <summary>
/// The Annotations panel (F20b): the picture in the viewer last clicked -
/// what its <c>.sann</c> says about it, its shapes a row each, and its
/// layers, each switched on and off.
/// </summary>
/// <remarks>
/// The Shapes table lists the layers switched on, in the order the viewer
/// draws them, so a row and a shape on the picture share a number and a
/// selection.
/// </remarks>
public sealed class AnnotationsViewModel : ObservableObject
{
    private readonly BrowserViewModel _owner;
    private TileViewModel? _tile;
    private IReadOnlyList<AnnotationFact> _facts = [];
    private ShapeTable _shapes = ShapeTable.Of(AnnotationTable.ShapeColumns, []);

    public AnnotationsViewModel(BrowserViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserViewModel.CurrentTile))
                Follow(owner.CurrentTile);
        };

        owner.LayersChanged += (_, _) => Refresh();
        Follow(owner.CurrentTile);
    }

    /// <summary>The viewer followed.</summary>
    public TileViewModel? Tile => _tile;

    /// <summary>"Top › 20261121\0042_FAIL.png", or what to do.</summary>
    public string Caption => _tile switch
    {
        null => "No viewer",
        { File: null } tile => $"Viewer {tile.Number}: no picture",
        { File: { } file } tile => _owner.Tiles.Count > 1 ? $"Viewer {tile.Number} · {file.RelativePath}" : file.RelativePath,
    };

    public string? Verdict => _tile?.Verdict;

    /// <summary>Station, recipe, inspection, time, verdict, size; the part; the results.</summary>
    public IReadOnlyList<AnnotationFact> Facts
    {
        get => _facts;
        private set => SetProperty(ref _facts, value);
    }

    /// <summary>The shapes of the layers switched on, a row each.</summary>
    public ShapeTable Shapes
    {
        get => _shapes;
        private set => SetProperty(ref _shapes, value);
    }

    /// <summary>Every layer the picture has, switched on or off.</summary>
    public ObservableCollection<LayerToggle> Layers { get; } = [];

    public bool HasLayers => Layers.Count > 0;

    /// <summary>Whether the picture has an annotation file to show.</summary>
    public bool HasAnnotations => _tile?.Annotations is not null;

    /// <summary>What the panel says when there is nothing to list.</summary>
    public string EmptyText => _tile switch
    {
        null or { File: null } => "Choose a picture in the list; this shows what its annotation file says.",
        { IsLoading: true } => "Reading…",
        _ => "This picture has no annotation file (.sann) beside it.",
    };

    public string ResultsHeader => $"Results ({_facts.Count})";

    public string ShapesHeader => $"Shapes ({_shapes.Rows.Count})";

    /// <summary>The shape picked, shared with the viewer: a row clicked lights its shape, a shape clicked its row.</summary>
    public int SelectedShapeIndex
    {
        get => _tile?.SelectedShapeIndex ?? -1;
        set
        {
            if (_tile is not null)
                _tile.SelectedShapeIndex = value;
        }
    }

    private void Follow(TileViewModel? tile)
    {
        if (ReferenceEquals(tile, _tile))
            return;

        if (_tile is not null)
            _tile.PropertyChanged -= OnTileChanged;

        _tile = tile;

        if (_tile is not null)
            _tile.PropertyChanged += OnTileChanged;

        OnPropertyChanged(nameof(Tile));
        Refresh();
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TileViewModel.Annotations) or nameof(TileViewModel.Shapes) or nameof(TileViewModel.File) or nameof(TileViewModel.IsLoading):
                Refresh();
                break;
            case nameof(TileViewModel.SelectedShapeIndex):
                OnPropertyChanged(nameof(SelectedShapeIndex));
                break;
            case nameof(TileViewModel.Verdict):
                OnPropertyChanged(nameof(Verdict));
                break;
        }
    }

    private void Refresh()
    {
        var annotations = _tile?.Annotations;

        Facts = annotations is null ? [] : AnnotationTable.Facts(annotations);
        Shapes = AnnotationTable.Shapes(_tile?.ShownLayers ?? []);

        // The same layers again - stepping along a station's pictures - keep their toggles.
        var layers = annotations?.Layers ?? [];
        var same = layers.Count == Layers.Count && layers.Select(layer => layer.Name).SequenceEqual(Layers.Select(toggle => toggle.Name), StringComparer.OrdinalIgnoreCase);

        if (same)
        {
            for (var i = 0; i < layers.Count; i++)
                Layers[i].Update(layers[i].Shapes.Count, _owner.IsLayerShown(layers[i].Name));
        }
        else
        {
            Layers.Clear();

            foreach (var layer in layers)
                Layers.Add(new LayerToggle(_owner, layer.Name, layer.Shapes.Count));
        }

        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ResultsHeader));
        OnPropertyChanged(nameof(ShapesHeader));
        OnPropertyChanged(nameof(SelectedShapeIndex));
    }
}

/// <summary>A layer of the picture's annotations, with how many shapes, drawn or not.</summary>
public sealed class LayerToggle : ObservableObject
{
    private readonly BrowserViewModel _owner;
    private int _count;
    private bool _shown;

    internal LayerToggle(BrowserViewModel owner, string name, int count)
    {
        _owner = owner;
        Name = name;
        _count = count;
        _shown = owner.IsLayerShown(name);
    }

    public string Name { get; }

    /// <summary>"Main View (12)".</summary>
    public string Text => $"{Name} ({_count})";

    /// <summary>Drawn - in every viewer, on every picture.</summary>
    public bool IsShown
    {
        get => _shown;
        set
        {
            if (SetProperty(ref _shown, value))
                _owner.SetLayerShown(Name, value);
        }
    }

    internal void Update(int count, bool shown)
    {
        if (_count != count)
        {
            _count = count;
            OnPropertyChanged(nameof(Text));
        }

        SetProperty(ref _shown, shown, nameof(IsShown));
    }
}
