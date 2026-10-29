using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Studio.App.Controls;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// Base for anything that can be a dockable panel.
/// </summary>
/// <remarks>
/// Most panels belong to a node, but not all: a profile line drawn on the
/// image viewer gets a panel of its own without any node behind it. The
/// docking layout only needs a title and a stable identity, so that is all
/// this requires.
/// </remarks>
public abstract class PanelViewModel : ObservableObject, IDisposable
{
    /// <summary>Identifies this panel in a saved layout.</summary>
    public abstract string ContentId { get; }

    /// <summary>Panel title, shown on its tab.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Reads this panel's data from the evaluated graph, off the UI thread.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Commit"/> because the expensive part - turning a
    /// native image into a bitmap - would otherwise run on the UI thread and
    /// undo the point of evaluating off it. Implementations must not raise
    /// property changes here; they stage results for <see cref="Commit"/>.
    ///
    /// Safe only while no evaluation pass is running, which the run loop
    /// guarantees by not completing the pass until the update is done.
    /// </remarks>
    public virtual void Prepare(GraphScheduler scheduler)
    {
    }

    /// <summary>Publishes what <see cref="Prepare"/> staged. UI thread.</summary>
    public virtual void Commit()
    {
    }

    /// <summary>
    /// Releases anything this panel holds.
    /// </summary>
    /// <remarks>
    /// Most panels hold only managed data and need nothing here. A panel that
    /// keeps a native buffer alive between passes - the viewer, for interactive
    /// profiling - overrides this, and deleting its node must release it.
    /// </remarks>
    public virtual void Dispose()
    {
    }
}

/// <summary>
/// A dockable chart for one profile line drawn on an image viewer.
/// </summary>
/// <remarks>
/// Has no node behind it: the line is an annotation on the viewer, so the
/// viewer samples it and pushes the result here. Its title is the line's name,
/// which is also drawn beside the line on the image, so the window and the
/// thing it describes can be matched up by eye.
/// </remarks>
public sealed class ProfileLinePanelViewModel : PanelViewModel
{
    private readonly string _contentId;
    private string _title;
    private ProfileData? _data;
    private int _thickness = 1;

    public ProfileLinePanelViewModel(string contentId, string title)
    {
        _contentId = contentId;
        _title = title;
    }

    public override string ContentId => _contentId;

    public override string Title => _title;

    /// <summary>Raised when the chart needs re-sampling - the band changed.</summary>
    public event EventHandler? ResampleRequested;

    public void Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title == _title)
            return;

        _title = title;
        OnPropertyChanged(nameof(Title));
    }

    public ProfileData? Data
    {
        get => _data;
        set => SetProperty(ref _data, value);
    }

    /// <summary>Band widths offered in the panel, in pixels.</summary>
    public IReadOnlyList<int> BandOptions { get; } = new[] { 1, 3, 5, 9, 15 };

    /// <summary>Width of the band averaged across the line.</summary>
    public int Thickness
    {
        get => _thickness;
        set
        {
            if (SetProperty(ref _thickness, value))
                ResampleRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Base for a dockable panel that belongs to one node.
/// </summary>
/// <remarks>
/// Viewers, histograms and line profiles all follow the same rule: the node is
/// the panel. Adding the node opens a window, deleting it closes that window,
/// and the node's name is the tab title. Having one abstraction means a future
/// panel-producing node needs a view-model and a DataTemplate, nothing more.
/// </remarks>
public abstract class NodePanelViewModel : PanelViewModel
{
    protected NodePanelViewModel(VisionNode node)
    {
        Node = node;

        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VisionNode.Name))
                OnPropertyChanged(nameof(Title));
        };
    }

    public VisionNode Node { get; }

    /// <summary>Distinguishes panel kinds within a saved layout.</summary>
    protected abstract string ContentIdPrefix { get; }

    /// <summary>
    /// Identifies this panel in a saved layout. Tied to the node id, so a
    /// restored layout reattaches each panel to the node it belonged to.
    /// </summary>
    public override string ContentId => $"{ContentIdPrefix}:{Node.Id}";

    /// <summary>Panel title; follows the node name so the two cannot drift.</summary>
    public override string Title => Node.Name;

    /// <summary>
    /// Reads this panel's data from the evaluated graph, off the UI thread.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Commit"/> because the expensive part - turning a
    /// native image into a bitmap - would otherwise run on the UI thread and
    /// undo the point of evaluating off it. Implementations must not raise
    /// property changes here; they stage results for <see cref="Commit"/>.
    ///
    /// Safe only while no evaluation pass is running, which the run loop
    /// guarantees by not completing the pass until the update is done.
    /// </remarks>
    public abstract override void Prepare(GraphScheduler scheduler);

    /// <summary>Publishes what <see cref="Prepare"/> staged. UI thread.</summary>
    public abstract override void Commit();
}

/// <summary>A dockable histogram, bound to one <see cref="HistogramNode"/>.</summary>
public sealed class HistogramPanelViewModel : NodePanelViewModel
{
    private HistogramData? _data;

    public HistogramPanelViewModel(HistogramNode node)
        : base(node)
    {
        Source = node;
    }

    public HistogramNode Source { get; }

    protected override string ContentIdPrefix => "histogram";

    public HistogramData? Data
    {
        get => _data;
        private set => SetProperty(ref _data, value);
    }

    private HistogramData? _pending;

    public override void Prepare(GraphScheduler scheduler) =>
        _pending = scheduler.GetOutput<HistogramData>(Source.HistogramOut);

    public override void Commit() => Data = _pending;
}

/// <summary>A dockable table of shapes, bound to one <see cref="ShapeTableNode"/>.</summary>
public sealed class ShapeTablePanelViewModel : NodePanelViewModel
{
    private readonly ShapeSelectionService _selection;
    private ShapeSet? _data;
    private ShapeSet? _pending;
    private Calibration? _calibration;
    private Calibration? _pendingCalibration;

    public ShapeTablePanelViewModel(ShapeTableNode node, ShapeSelectionService selection)
        : base(node)
    {
        Source = node;
        _selection = selection;

        _selection.Changed += (_, port) =>
        {
            if (ReferenceEquals(port, Producer))
                OnPropertyChanged(nameof(SelectedIndex));
        };
    }

    public ShapeTableNode Source { get; }

    protected override string ContentIdPrefix => "shapetable";

    /// <summary>
    /// The port these shapes came from, which is the key the viewer showing
    /// the same wire agrees on.
    /// </summary>
    private OutputPort? Producer => Source.ShapesIn.Connection?.Source;

    public ShapeSet? Data
    {
        get => _data;
        private set => SetProperty(ref _data, value);
    }

    /// <summary>The selected row, counting from zero, or -1 for none.</summary>
    public int SelectedIndex
    {
        get => _selection.For(Producer) ?? -1;
        set => _selection.Select(Producer, value);
    }

    /// <summary>What a pixel is worth, when a Calibration is wired in.</summary>
    public Calibration? Calibration
    {
        get => _calibration;
        private set => SetProperty(ref _calibration, value);
    }

    // Reads the input rather than an output: the node is terminal, and the
    // shapes it lists belong to whatever produced them.
    public override void Prepare(GraphScheduler scheduler)
    {
        _pending = scheduler.GetInput<ShapeSet>(Source.ShapesIn);
        _pendingCalibration = scheduler.GetInput(Source.CalibrationIn) as Calibration?;
    }

    public override void Commit()
    {
        Calibration = _pendingCalibration;
        Data = _pending;

        // A new set may be shorter than the old one, so the row that was
        // selected can cease to exist.
        if (SelectedIndex >= (Data?.Count ?? 0))
            _selection.Select(Producer, null);

        OnPropertyChanged(nameof(SelectedIndex));
    }
}

/// <summary>
/// A dockable profile chart, bound to a <see cref="LineProfileNode"/> or a
/// <see cref="LineChartNode"/>.
/// </summary>
public sealed class ProfilePanelViewModel : NodePanelViewModel
{
    private readonly Func<GraphScheduler, IReadOnlyList<ChartSeries>> _read;
    private IReadOnlyList<ChartSeries> _series = [];
    private IReadOnlyList<ChartSeries> _pending = [];

    /// <summary>The profile a Line Profile node makes.</summary>
    public ProfilePanelViewModel(LineProfileNode node)
        : base(node)
    {
        _read = scheduler => scheduler.GetOutput<ProfileData>(node.ProfileOut) is { } data
            ? [new ChartSeries(node.Name, data)]
            : [];
    }

    /// <summary>
    /// Every profile wired into a Line Chart - read from its inputs, because
    /// the node is terminal and each profile belongs to its producer.
    /// </summary>
    public ProfilePanelViewModel(LineChartNode node)
        : base(node)
    {
        _read = scheduler => Gather(scheduler, node);
    }

    protected override string ContentIdPrefix => "profile";

    /// <summary>The one profile when there is one, or null - for the single-profile chart.</summary>
    public ProfileData? Data => _series.Count == 1 ? _series[0].Data : null;

    /// <summary>Every profile, each named for where it came from.</summary>
    public IReadOnlyList<ChartSeries> Series
    {
        get => _series;
        private set
        {
            if (SetProperty(ref _series, value))
                OnPropertyChanged(nameof(Data));
        }
    }

    public override void Prepare(GraphScheduler scheduler) => _pending = _read(scheduler);

    public override void Commit() => Series = _pending;

    /// <summary>
    /// The profiles in socket order, named for the node that made each -
    /// numbered when two nodes share a name, so the legend can tell them
    /// apart.
    /// </summary>
    internal static IReadOnlyList<ChartSeries> Gather(GraphScheduler scheduler, LineChartNode node)
    {
        var found = node.ProfileInputs
            .Select(port => (Port: port, Data: scheduler.GetInput<ProfileData>(port)))
            .Where(entry => entry.Data is not null)
            .ToArray();

        var names = found.Select(entry => entry.Port.Connection?.Source.Node.Name ?? entry.Port.Name).ToArray();

        return found
            .Select((entry, i) => new ChartSeries(
                names.Count(name => name == names[i]) > 1
                    ? $"{names[i]} {names.Take(i + 1).Count(name => name == names[i])}"
                    : names[i],
                entry.Data!))
            .ToArray();
    }
}
