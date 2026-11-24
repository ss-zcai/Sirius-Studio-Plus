using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.IV;

/// <summary>
/// The Match dialog: a rule chosen, and what it would make of the pictures
/// listed now - shown as it is typed, before it is used.
/// </summary>
public sealed class MatchingViewModel : ObservableObject
{
    /// <summary>How many sets the preview lists.</summary>
    public const int PreviewSets = 6;

    private readonly Func<MatchRule, List<IReadOnlyList<ImageFileItem>>> _sets;
    private MatchMode _mode;
    private int _groupSize;
    private string _characters;
    private string _pattern;
    private string _previewText = string.Empty;
    private IReadOnlyList<string> _preview = [];
    private string? _problem;

    /// <param name="current">The rule in use.</param>
    /// <param name="sets">The sets a rule makes of the pictures listed now.</param>
    public MatchingViewModel(MatchRule current, Func<MatchRule, List<IReadOnlyList<ImageFileItem>>> sets)
    {
        _sets = sets;
        _mode = current.Mode;
        _groupSize = current.GroupSize;
        _characters = current.Characters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _pattern = current.Pattern;
        Refresh();
    }

    public MatchMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
                return;

            OnPropertyChanged(nameof(IsOff));
            OnPropertyChanged(nameof(IsGroups));
            OnPropertyChanged(nameof(IsNameStart));
            OnPropertyChanged(nameof(IsPattern));
            Refresh();
        }
    }

    // One for each radio button.
    public bool IsOff
    {
        get => _mode == MatchMode.None;
        set => Choose(value, MatchMode.None);
    }

    public bool IsGroups
    {
        get => _mode == MatchMode.Groups;
        set => Choose(value, MatchMode.Groups);
    }

    public bool IsNameStart
    {
        get => _mode == MatchMode.NameStart;
        set => Choose(value, MatchMode.NameStart);
    }

    public bool IsPattern
    {
        get => _mode == MatchMode.Pattern;
        set => Choose(value, MatchMode.Pattern);
    }

    private void Choose(bool chosen, MatchMode mode)
    {
        if (chosen)
            Mode = mode;
    }

    /// <summary>2 to 9 pictures in a group.</summary>
    public static IReadOnlyList<int> GroupSizes { get; } = Enumerable.Range(2, MatchRule.MaxGroupSize - 1).ToArray();

    public int GroupSize
    {
        get => _groupSize;
        set
        {
            if (SetProperty(ref _groupSize, value))
            {
                Mode = MatchMode.Groups;
                Refresh();
            }
        }
    }

    /// <summary>As typed: checked when the rule is made.</summary>
    public string Characters
    {
        get => _characters;
        set
        {
            if (SetProperty(ref _characters, value ?? string.Empty))
            {
                Mode = MatchMode.NameStart;
                Refresh();
            }
        }
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (SetProperty(ref _pattern, value ?? string.Empty))
            {
                Mode = MatchMode.Pattern;
                Refresh();
            }
        }
    }

    /// <summary>The rule as set out in the dialog.</summary>
    public MatchRule Rule => new()
    {
        Mode = _mode,
        GroupSize = _groupSize,
        Characters = int.TryParse(_characters, out var characters) ? characters : 0,
        Pattern = _pattern.Trim(),
    };

    /// <summary>Why the rule cannot be used, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    /// <summary>"240 pictures make 120 sets: 118 of 2, 4 on their own".</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>The first few sets, a line each: "part0001_cam1.png  +  part0001_cam2.png".</summary>
    public IReadOnlyList<string> Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    private void Refresh()
    {
        var rule = Rule;
        Problem = rule.Problem();

        if (Problem is not null)
        {
            PreviewText = string.Empty;
            Preview = [];
            return;
        }

        var sets = _sets(rule);
        var pictures = sets.Sum(set => set.Count);

        if (pictures == 0)
        {
            PreviewText = "No pictures are listed to match. Open a folder first.";
            Preview = [];
            return;
        }

        if (!rule.IsOn)
        {
            PreviewText = $"Each of the {pictures} pictures on its own.";
            Preview = [];
            return;
        }

        // "118 of 2, 3 of 3, 4 on their own"
        var sizes = sets
            .GroupBy(set => set.Count)
            .OrderByDescending(group => group.Key)
            .Select(group => group.Key == 1
                ? $"{group.Count()} on {(group.Count() == 1 ? "its" : "their")} own"
                : $"{group.Count()} of {group.Key}");

        PreviewText = $"{pictures} pictures make {sets.Count} set{(sets.Count == 1 ? string.Empty : "s")}: {string.Join(", ", sizes)}.";
        Preview = sets
            .Take(PreviewSets)
            .Select(set => string.Join("  +  ", set.Select(item => item.RelativePath)))
            .ToArray();
    }
}
