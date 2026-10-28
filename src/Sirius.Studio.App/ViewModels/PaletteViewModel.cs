using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>A single entry in the node palette.</summary>
public sealed class PaletteItemViewModel
{
    public PaletteItemViewModel(NodeDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public NodeDescriptor Descriptor { get; }

    public string DisplayName => Descriptor.DisplayName;

    public string? Description => Descriptor.Description;

    public string? IconKey => Descriptor.Icon;
}

/// <summary>Palette entries grouped by category.</summary>
public sealed class PaletteGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public PaletteGroupViewModel(NodeCategory category, IEnumerable<PaletteItemViewModel> items)
    {
        Category = category;
        Items = new ObservableCollection<PaletteItemViewModel>(items);
    }

    /// <summary>
    /// Whether this category is open in the toolbox. Groups expand and
    /// collapse independently, so any number can be open at once.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public NodeCategory Category { get; }

    /// <summary>"Text and Code Reading" rather than "TextAndCodeReading".</summary>
    public string DisplayName => NameOf(Category);

    /// <summary>A category's name as the toolbox shows it.</summary>
    internal static string NameOf(NodeCategory category) => SplitCamelCase(category.ToString());

    public ObservableCollection<PaletteItemViewModel> Items { get; }

    private static string SplitCamelCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 6);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]))
                builder.Append(' ');
            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
