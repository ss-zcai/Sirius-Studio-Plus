using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NodeGraphKit.Model;

namespace NodeGraphKit.ViewModels;

/// <summary>One entry in the toolbox.</summary>
public sealed class PaletteItemViewModel(NodeDefinition definition)
{
    public NodeDefinition Definition { get; } = definition;

    public string DisplayName => Definition.DisplayName;

    public string? ToolTipText => Definition.Description;

    public string? IconKey => Definition.Icon;

    internal bool Matches(string search) =>
        Contains(Definition.DisplayName, search) ||
        Contains(Definition.Category, search) ||
        Contains(Definition.Description, search);

    private static bool Contains(string? text, string search) =>
        text?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
}

/// <summary>A toolbox group: one category's entries, under a header that opens and closes.</summary>
public sealed class PaletteGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public PaletteGroupViewModel(string category, IEnumerable<PaletteItemViewModel> items)
    {
        DisplayName = category;
        Items = new ObservableCollection<PaletteItemViewModel>(items);
    }

    public string DisplayName { get; }

    public ObservableCollection<PaletteItemViewModel> Items { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

/// <summary>
/// The toolbox: the catalogue grouped by category, with a search box.
/// </summary>
/// <remarks>
/// The toolbox does not know which canvas it adds to. A double click, or
/// Enter in the search box, raises <see cref="AddRequested"/> and the host
/// puts the node on the canvas it means; dragging an entry onto a canvas
/// needs no wiring at all.
/// </remarks>
public sealed class PaletteViewModel : ObservableObject
{
    private readonly NodeCatalog _catalog;
    private string _search = string.Empty;

    public PaletteViewModel(NodeCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _catalog.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<PaletteGroupViewModel> Groups { get; } = [];

    /// <summary>Filters the toolbox by name, group or description.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                Refresh();
                OnPropertyChanged(nameof(IsSearching));
            }
        }
    }

    public bool IsSearching => Search.Length > 0;

    public bool HasNoMatches => Groups.Count == 0;

    /// <summary>What Enter in the search box adds.</summary>
    public PaletteItemViewModel? FirstMatch => Groups.FirstOrDefault()?.Items.FirstOrDefault();

    /// <summary>A node type the user asked to add without saying where.</summary>
    public event EventHandler<NodeDefinition>? AddRequested;

    public void RequestAdd(NodeDefinition definition) => AddRequested?.Invoke(this, definition);

    private void Refresh()
    {
        var search = Search.Trim();
        var collapsed = Groups.Where(g => !g.IsExpanded).Select(g => g.DisplayName).ToHashSet();

        Groups.Clear();

        foreach (var group in _catalog.Definitions.GroupBy(d => d.Category))
        {
            var items = group
                .Select(definition => new PaletteItemViewModel(definition))
                .Where(item => search.Length == 0 || item.Matches(search))
                .ToArray();

            if (items.Length == 0)
                continue;

            // A search opens every group that has a match; otherwise each
            // keeps the state it was left in.
            Groups.Add(new PaletteGroupViewModel(group.Key, items)
            {
                IsExpanded = search.Length > 0 || !collapsed.Contains(group.Key),
            });
        }

        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(FirstMatch));
    }
}
