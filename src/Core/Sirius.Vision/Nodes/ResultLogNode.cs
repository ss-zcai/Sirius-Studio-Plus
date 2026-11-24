using System.Globalization;
using System.Text;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using IoPath = System.IO.Path;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Writes one row per inspection to a CSV file: the time, the image, and
/// every value wired in.
/// </summary>
/// <remarks>
/// <para>
/// Where a verdict goes when nobody is watching the status line. Each
/// socket is a column, headed by the node and output it is wired from;
/// wiring one adds the next. Numbers, yes/no results, grades and text are
/// written as themselves, a shape set as how many shapes it holds.
/// </para>
/// <para>
/// Writes on an inspection - a Run, each image of a folder run - and not
/// on the re-runs that follow an edit. A file held open elsewhere (Excel
/// locks a CSV it has open) is not an error: the rows are kept and written
/// the next time the file can be opened.
/// </para>
/// <para>
/// When the columns change, a new file is started rather than rows of one
/// shape appended under the heading of another.
/// </para>
/// </remarks>
[VisionNode(
    "Result Log",
    TypeKey = "judging.resultLog",
    Category = NodeCategory.Judging,
    Icon = "ph_result_log",
    Description = "Writes a CSV row per inspection: time, image, and every value wired in - numbers, " +
                  "yes/no, grades, text. Wiring a value adds another column.")]
public sealed class ResultLogNode : VisionNode, IInspectionNode, IGrowingPorts
{
    private const string First = "Value";

    private readonly GrowingInputs _row;
    private readonly object _gate = new();
    private readonly List<string> _held = [];

    private string _logFile = string.Empty;
    private bool _newFileEachDay = true;
    private string? _activeFile;
    private string? _activeHeader;
    private int _rows;

    public ResultLogNode()
    {
        AddInput<object>(First, isRequired: false);
        RowsOut = AddOutput<int>("Rows");
        FileOut = AddOutput<string>("File");

        _row = new GrowingInputs(
            First,
            limit: 32,
            () => Sockets,
            (after, name) => InsertInput<object>(Inputs.ToList().IndexOf(after) + 1, name, isRequired: false),
            RemoveInput,
            FindInput);
    }

    /// <summary>The value sockets, in column order.</summary>
    public IReadOnlyList<InputPort> Sockets => Inputs
        .Where(port => port.DataType == typeof(object) && !IsParameterPort(port))
        .ToArray();

    /// <summary>Rows written this session.</summary>
    public OutputPort RowsOut { get; }

    /// <summary>The file being written.</summary>
    public OutputPort FileOut { get; }

    [Param("Log File",
        Editor = ParamEditor.FilePath,
        SaveFile = true,
        Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        Description = "The CSV file to write. With New File Each Day, the date is added to its name.")]
    public string LogFile
    {
        get => _logFile;
        set => SetParam(ref _logFile, value ?? string.Empty);
    }

    [Param("New File Each Day",
        Description = "Start a file per day - results-2026-10-29.csv - rather than one that grows for ever.")]
    public bool NewFileEachDay
    {
        get => _newFileEachDay;
        set => SetParam(ref _newFileEachDay, value);
    }

    public bool Fit() => _row.Fit();

    public InputPort? EnsureInput(string name) => _row.Ensure(name);

    protected override void OnEvaluate(EvalContext context)
    {
        lock (_gate)
        {
            if (context.IsInspection)
                Write(context, DateTime.Now);

            context.Set(RowsOut, _rows);
            context.Set(FileOut, _activeFile ?? string.Empty);
        }
    }

    /// <summary>Adds this inspection's row, with the heading when the file is new.</summary>
    internal void Write(EvalContext context, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(_logFile))
        {
            context.Warn("Choose a Log File to write to.");
            return;
        }

        var wired = _row.Connected.ToArray();
        var header = Line(["Time", "Image", .. Headings(wired)]);
        var row = Line(
        [
            now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ImageName() ?? string.Empty,
            .. wired.Select(port => Format(context.Get<object>(port))),
        ]);

        var file = FileFor(now, header);

        // Rows held while the file was locked belong to the file they were
        // meant for; a new file - a new day, new columns - starts clean
        // and the held rows are written to it with its own heading.
        _held.Add(row);

        try
        {
            var fresh = !File.Exists(file) || new FileInfo(file).Length == 0;

            var directory = IoPath.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder();
            if (fresh)
                text.Append(header).Append("\r\n");

            foreach (var held in _held)
                text.Append(held).Append("\r\n");

            File.AppendAllText(file, text.ToString(), Encoding.UTF8);

            _rows += _held.Count;
            _held.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Warn(
                $"Could not write to {IoPath.GetFileName(file)} - is it open in another program? " +
                $"{_held.Count} row(s) are held and will be written next time.");
        }
    }

    /// <summary>
    /// The file to write: the chosen one, dated when asked; and a new one,
    /// stamped with the time, when the file there has other columns.
    /// </summary>
    private string FileFor(DateTime now, string header)
    {
        var chosen = _newFileEachDay ? Dated(_logFile, now) : _logFile;

        // Still writing where this session was, with the same columns.
        if (_activeFile is not null && _activeHeader == header &&
            (_activeFile == chosen || _activeFile.StartsWith(Stem(chosen) + "-", StringComparison.OrdinalIgnoreCase)))
        {
            return _activeFile;
        }

        var file = chosen;

        if (File.Exists(file) && FirstLine(file) is { } existing && existing != header)
            file = $"{Stem(chosen)}-{now:HHmmss}{IoPath.GetExtension(chosen)}";

        _activeFile = file;
        _activeHeader = header;
        return file;
    }

    /// <summary>"results.csv" on 29 October 2026 as "results-2026-10-29.csv".</summary>
    internal static string Dated(string path, DateTime day) =>
        $"{Stem(path)}-{day:yyyy-MM-dd}{IoPath.GetExtension(path)}";

    private static string Stem(string path) =>
        IoPath.Combine(IoPath.GetDirectoryName(path) ?? string.Empty, IoPath.GetFileNameWithoutExtension(path));

    private static string? FirstLine(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Each column headed by where it is wired from: "Measure Value", or
    /// just "Grade" when the output shares its node's name. Repeats are
    /// numbered.
    /// </summary>
    private static IEnumerable<string> Headings(IReadOnlyList<InputPort> wired)
    {
        var names = wired
            .Select(port => port.Connection?.Source is { } source
                ? string.Equals(source.DisplayName, source.Node.Name, StringComparison.OrdinalIgnoreCase)
                    ? source.Node.Name
                    : $"{source.Node.Name} {source.DisplayName}"
                : port.DisplayName)
            .ToArray();

        return names.Select((name, i) => names.Count(other => other == name) > 1
            ? $"{name} {names.Take(i + 1).Count(other => other == name)}"
            : name);
    }

    /// <summary>The image this inspection looked at, from the recipe's source.</summary>
    private string? ImageName()
    {
        if (Graph is not { } graph)
            return null;

        if (graph.Nodes.OfType<ISequenceNode>().FirstOrDefault()?.CurrentName is { } name)
            return name;

        var path = graph.Nodes.OfType<ImageFileSourceNode>().FirstOrDefault()?.FilePath;
        return string.IsNullOrEmpty(path) ? null : IoPath.GetFileName(path);
    }

    /// <summary>A value as a CSV field.</summary>
    internal static string Format(object? value) => value switch
    {
        null => string.Empty,
        double number => number.ToString("0.######", CultureInfo.InvariantCulture),
        float number => ((double)number).ToString("0.######", CultureInfo.InvariantCulture),
        bool yes => yes ? "TRUE" : "FALSE",
        ShapeSet shapes => shapes.Count.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Fields joined by commas, each quoted when it holds a comma, a quote or a line break.</summary>
    internal static string Line(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(field =>
            field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field));
}
