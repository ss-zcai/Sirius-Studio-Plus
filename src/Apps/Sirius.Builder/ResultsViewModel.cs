using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Projects;

namespace Sirius.Builder;

/// <summary>How a result counts, as the Results window offers it.</summary>
public sealed record CountingChoice(ResultCounting Value, string Text)
{
    public static IReadOnlyList<CountingChoice> All { get; } =
    [
        new(ResultCounting.Pass, "Pass"),
        new(ResultCounting.Fail, "Fail"),
        new(ResultCounting.NotCounted, "Not counted"),
    ];

    public override string ToString() => Text;
}

/// <summary>A result as the Results window edits it: each box as typed.</summary>
public sealed partial class ResultRow : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _codeText;

    [ObservableProperty]
    private CountingChoice _counting;

    [ObservableProperty]
    private string _colour;

    public ResultRow(ResultCode result)
    {
        _name = result.Name;
        _codeText = result.Code.ToString(CultureInfo.InvariantCulture);
        _counting = CountingChoice.All.First(choice => choice.Value == result.CountsAs);
        _colour = result.Colour;
    }

    /// <summary>The result, or null when its code is not a whole number.</summary>
    internal ResultCode? ToResult() =>
        int.TryParse(CodeText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? new ResultCode(Name?.Trim() ?? string.Empty, code, Counting.Value, Colour)
            : null;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "(no name)" : Name.Trim();
}

/// <summary>
/// A station's results being edited (F33): the list, best first - name,
/// colour, PLC code, how each counts - and which a late part, and one not
/// inspected, gets. Nothing is written until <see cref="Apply"/>.
/// </summary>
public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly Project _project;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private ResultRow? _lateRow;

    [ObservableProperty]
    private ResultRow? _notInspectedRow;

    private readonly IReadOnlyList<string>? _grades;
    private string? _gradeNote;

    /// <param name="grades">
    /// The grades the station's recipe gives, to hold the list against; null
    /// when the recipe could not be read, and nothing is said.
    /// </param>
    public ResultsViewModel(Project project, Station station, IReadOnlyList<string>? grades = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Station = station ?? throw new ArgumentNullException(nameof(station));
        _grades = grades;

        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand<ResultRow>(Remove);
        UpCommand = new RelayCommand<ResultRow>(row => Move(row, -1));
        DownCommand = new RelayCommand<ResultRow>(row => Move(row, +1));
        StandardCommand = new RelayCommand(() => Load(ResultList.Default));

        Load(station.Results);
    }

    public Station Station { get; }

    /// <summary>The results, best first: later is worse when a part's are combined.</summary>
    public ObservableCollection<ResultRow> Rows { get; } = [];

    /// <summary>What a late part, or one not inspected, may be given: any result that is not a pass.</summary>
    public ObservableCollection<ResultRow> MissedChoices { get; } = [];

    public static IReadOnlyList<CountingChoice> Countings => CountingChoice.All;

    public static IReadOnlyList<string> Colours => ResultList.Colours;

    public IRelayCommand AddCommand { get; }

    public IRelayCommand<ResultRow> RemoveCommand { get; }

    public IRelayCommand<ResultRow> UpCommand { get; }

    public IRelayCommand<ResultRow> DownCommand { get; }

    /// <summary>Back to PASS and FAIL - what a station without a list gives.</summary>
    public IRelayCommand StandardCommand { get; }

    /// <summary>The list as it stands in the window, or null with <see cref="Problem"/> saying why it cannot be one.</summary>
    public ResultList? Draft()
    {
        var results = new List<ResultCode>();

        foreach (var row in Rows)
        {
            if (row.ToResult() is not { } result)
            {
                Problem = $"{row}: the PLC code is a whole number.";
                return null;
            }

            results.Add(result);
        }

        // The first result that fails is what a missed part gets unless chosen: kept as "unless chosen".
        var firstFail = Rows.FirstOrDefault(row => row.Counting.Value == ResultCounting.Fail);
        var list = new ResultList(
            results,
            LateRow is null || ReferenceEquals(LateRow, firstFail) ? null : LateRow.Name,
            NotInspectedRow is null || ReferenceEquals(NotInspectedRow, firstFail) ? null : NotInspectedRow.Name);

        Problem = list.Problem();
        GradeNote = Problem is null && _grades is not null ? list.GradeMismatch(_grades) : null;
        return Problem is null ? list : null;
    }

    /// <summary>
    /// Where the list and the grades of the station's recipe do not meet - a
    /// grade no result is called, a result never given - or null. A note, not
    /// a problem: the list can still be kept.
    /// </summary>
    public string? GradeNote
    {
        get => _gradeNote;
        private set
        {
            if (SetProperty(ref _gradeNote, value))
                OnPropertyChanged(nameof(HasGradeNote));
        }
    }

    public bool HasGradeNote => _gradeNote is not null;

    partial void OnLateRowChanged(ResultRow? value) => NoteAgain();

    partial void OnNotInspectedRowChanged(ResultRow? value) => NoteAgain();

    /// <summary>The note again, keeping any problem the window shows as it is.</summary>
    private void NoteAgain()
    {
        if (_grades is null)
            return;

        var problem = Problem;
        Draft();
        Problem = problem;
    }

    /// <summary>Keeps the list in the project. Returns false, with <see cref="Problem"/> set, when it cannot be.</summary>
    public bool Apply()
    {
        if (Draft() is not { } list)
            return false;

        try
        {
            _project.SetResults(Station, list);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem = ex.Message;
            return false;
        }
    }

    private void Load(ResultList list)
    {
        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;

        Rows.Clear();

        foreach (var result in list)
            Watch(new ResultRow(result));

        RefreshChoices();
        LateRow = RowFor(list.LateResult);
        NotInspectedRow = RowFor(list.NotInspectedResult);
        Problem = null;
        GradeNote = _grades is null ? null : list.GradeMismatch(_grades);
    }

    private ResultRow? RowFor(ResultCode? result) =>
        result is null ? null : Rows.FirstOrDefault(row => string.Equals(row.Name?.Trim(), result.Name, StringComparison.OrdinalIgnoreCase));

    private void Add()
    {
        var names = Rows.Select(row => row.Name?.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = names.Contains("REVIEW") ? NextName(names) : "REVIEW";
        var code = Rows.Select(row => int.TryParse(row.CodeText, CultureInfo.InvariantCulture, out var used) ? used : 0).DefaultIfEmpty(0).Max() + 1;
        var colour = Colours[Rows.Count % Colours.Count];

        Watch(new ResultRow(new ResultCode(name, code, ResultCounting.NotCounted, colour)));
        RefreshChoices();
        Draft();
    }

    private static string NextName(HashSet<string?> names)
    {
        for (var i = 1; ; i++)
        {
            var name = $"RESULT{i}";

            if (!names.Contains(name))
                return name;
        }
    }

    private void Remove(ResultRow? row)
    {
        if (row is null)
            return;

        row.PropertyChanged -= OnRowChanged;
        Rows.Remove(row);
        RefreshChoices();
        Draft();
    }

    private void Move(ResultRow? row, int by)
    {
        if (row is null)
            return;

        var at = Rows.IndexOf(row);
        var to = at + by;

        if (at < 0 || to < 0 || to >= Rows.Count)
            return;

        Rows.Move(at, to);
        RefreshChoices();
        Draft();
    }

    private void Watch(ResultRow row)
    {
        row.PropertyChanged += OnRowChanged;
        Rows.Add(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultRow.Counting) or nameof(ResultRow.Name))
            RefreshChoices();

        Draft();
    }

    /// <summary>The results a missed part may be given, kept in step with the list; a choice that went is the first fail again.</summary>
    private void RefreshChoices()
    {
        var late = LateRow;
        var notInspected = NotInspectedRow;

        MissedChoices.Clear();

        foreach (var row in Rows.Where(row => row.Counting.Value != ResultCounting.Pass))
            MissedChoices.Add(row);

        var firstFail = Rows.FirstOrDefault(row => row.Counting.Value == ResultCounting.Fail);
        LateRow = late is not null && MissedChoices.Contains(late) ? late : firstFail;
        NotInspectedRow = notInspected is not null && MissedChoices.Contains(notInspected) ? notInspected : firstFail;
    }
}
