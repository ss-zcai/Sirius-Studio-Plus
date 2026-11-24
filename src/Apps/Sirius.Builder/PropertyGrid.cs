using System.Collections;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Builder;

/// <summary>How a property is edited in the grid.</summary>
public enum PropertyEditor
{
    /// <summary>Typed text, committed on Enter or on leaving the box.</summary>
    Text,

    /// <summary>One of a list.</summary>
    Choice,

    /// <summary>On or off.</summary>
    Check,

    /// <summary>A file, typed or picked with "…".</summary>
    File,

    /// <summary>A colour: one of a list, or typed as a name or #RRGGBB, with a swatch.</summary>
    Colour,

    /// <summary>Shown, not edited.</summary>
    ReadOnly,
}

/// <summary>One entry of a property's list: what is shown, and what is stored.</summary>
public sealed record PropertyOption(string Label, object? Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// One row of the property grid: a name on the left, its editor on the
/// right, read and written through the owner's own properties.
/// </summary>
/// <remarks>
/// Values pass as text for <see cref="PropertyEditor.Text"/> - the owner
/// parses, and a value it refuses simply reads back as it was - and as the
/// stored value for a choice or a check.
/// </remarks>
public sealed class PropertyRow : ObservableObject
{
    private readonly Func<object?> _get;
    private readonly Action<object?>? _set;
    private readonly Func<IEnumerable>? _options;
    private readonly Func<bool>? _visible;
    private readonly Func<bool>? _enabled;

    public PropertyRow(
        string category,
        string name,
        PropertyEditor editor,
        Func<object?> get,
        Action<object?>? set = null,
        string? description = null,
        Func<IEnumerable>? options = null,
        Func<bool>? visible = null,
        Func<bool>? enabled = null,
        ICommand? browse = null)
    {
        Category = category;
        Name = name;
        Editor = set is null && editor != PropertyEditor.ReadOnly ? PropertyEditor.ReadOnly : editor;
        Description = description;
        Browse = browse;
        _get = get;
        _set = set;
        _options = options;
        _visible = visible;
        _enabled = enabled;
    }

    public string Category { get; }

    public string Name { get; }

    public PropertyEditor Editor { get; }

    /// <summary>Shown under the grid while the row is being edited, and as its tooltip.</summary>
    public string? Description { get; }

    /// <summary>"…" for a file.</summary>
    public ICommand? Browse { get; }

    public object? Value
    {
        get => _get();
        set
        {
            if (Equals(value, _get()))
                return;

            _set?.Invoke(value);

            // A refused value reads back as it was.
            OnPropertyChanged();
        }
    }

    /// <summary>The value as text, for a read-only row.</summary>
    public string Text => Value switch
    {
        null => string.Empty,
        bool on => on ? "Yes" : "No",
        var value => Options is { } options
            ? options.OfType<PropertyOption>().FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value.ToString() ?? string.Empty
            : value.ToString() ?? string.Empty,
    };

    public IEnumerable? Options => _options?.Invoke();

    /// <summary>A colour row's colour, for its swatch.</summary>
    public System.Windows.Media.Brush? Swatch =>
        Editor == PropertyEditor.Colour
            ? Sirius.Studio.App.Screens.ScreenColours.Brush(Sirius.Studio.App.Screens.ScreenColours.Normalise(Value as string, out _))
            : null;

    public bool IsVisible => _visible?.Invoke() ?? true;

    public bool IsEnabled => _enabled?.Invoke() ?? true;

    /// <summary>Reads everything again - after the owner changed.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A category of the property grid and its rows.</summary>
public sealed class PropertyCategory : ObservableObject
{
    public PropertyCategory(string name, IReadOnlyList<PropertyRow> rows)
    {
        Name = name;
        Rows = rows;
    }

    public string Name { get; }

    public IReadOnlyList<PropertyRow> Rows { get; }

    public bool IsVisible => Rows.Any(row => row.IsVisible);

    public void Refresh()
    {
        foreach (var row in Rows)
            row.Refresh();

        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>The rows, gathered into their categories in the order each first appears.</summary>
    public static IReadOnlyList<PropertyCategory> From(IEnumerable<PropertyRow> rows) =>
        rows.GroupBy(row => row.Category)
            .Select(group => new PropertyCategory(group.Key, group.ToArray()))
            .ToArray();
}
