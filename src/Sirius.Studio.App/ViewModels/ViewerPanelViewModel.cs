using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows.Media.Imaging;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.Controls;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Diagnostics;
using Sirius.Studio.App.Imaging;
using Sirius.Vision;
using Sirius.Vision.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// One dockable image panel, backed by a single <see cref="ViewerNode"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each Viewer node owns a panel, so several points in a graph can be watched
/// at once - source beside result, or two branches side by side - which is the
/// reason to have a node graph rather than a linear chain in the first place.
/// </para>
/// <para>
/// Replaces the earlier behaviour where one shared viewer followed the selected
/// node. That made the displayed image a side effect of selection: clicking a
/// node to edit its parameters silently changed what you were looking at.
/// </para>
/// </remarks>
public abstract class ImagePanelViewModel : NodePanelViewModel
{
    private BitmapSource? _image;
    private string? _sizeText;
    private ParameterViewModel? _regionParameter;
    private ParameterViewModel? _ringParameter;
    private ParameterViewModel? _pathParameter;
    private ShapeSet? _shapes;
    private ShapeSet? _pendingShapes;
    private IReadOnlyList<ShapeSpan> _spans = [];
    private IReadOnlyList<ShapeSpan> _pendingSpans = [];
    private OutputPort? _shownSource;
    private LineProfileSource? _profileSource;
    private LineProfileSource? _pendingProfileSource;
    private bool _isProfilingEnabled;
    private IReadOnlyList<MeasurementRecord> _measurements = [];
    private bool _writingMeasurements;
    private ViewerTool _activeTool = ViewerTool.None;

    /// <param name="measurementView">
    /// Which of the node's pictures this panel shows, for keying
    /// measurements. Empty for a Viewer node's own panel.
    /// </param>
    /// <remarks>
    /// The view is taken as a constructor argument rather than read from
    /// an overridable property, and that is not a style preference. This
    /// constructor reads the node's measurements, reading them asks each
    /// one whether it belongs to this view, and a virtual property
    /// answering that question runs the override before the derived
    /// constructor has assigned the field it reads - so opening a preview
    /// of a node that already had measurements on it threw a
    /// NullReferenceException. It only threw once there were measurements,
    /// because with none the predicate is never invoked.
    /// </remarks>
    protected ImagePanelViewModel(
        VisionNode node, ShapeSelectionService selection, string measurementView)
        : base(node)
    {
        _selection = selection;
        _measurementView = measurementView ?? string.Empty;

        _selection.Changed += (_, port) =>
        {
            if (!_spans.Any(span => ReferenceEquals(span.Source, port)))
                return;

            // What was picked last is what this panel shows, wherever it was
            // picked - here, or in a table reading one of the same wires.
            if (_selection.For(port) is not null)
                _shownSource = port;

            OnPropertyChanged(nameof(SelectedShapeIndex));
        };

        // Opening a recipe replaces the node's set wholesale, and a panel
        // already on screen has to notice.
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VisionNode.Measurements) && !_writingMeasurements)
                ReadMeasurements();
        };

        ReadMeasurements();
    }

    private readonly ShapeSelectionService _selection;

    /// <summary>
    /// The port the drawn shapes came from - the key shared with any table
    /// reading the same wire, so selecting a blob in one highlights it in the
    /// other. Null when this panel shows no shapes, or shapes from several
    /// ports - see <see cref="SpansOf"/>.
    /// </summary>
    protected virtual OutputPort? ShapeProducer => null;

    /// <summary>
    /// A run of the drawn shapes that came from one port: shapes
    /// <see cref="Start"/> onwards, <see cref="Count"/> of them, are that
    /// port's shapes 0 onwards.
    /// </summary>
    protected readonly record struct ShapeSpan(OutputPort Source, int Start, int Count);

    /// <summary>
    /// Where each of the drawn shapes came from, so a shape picked here
    /// selects the same shape in a table reading its wire - and back.
    /// </summary>
    /// <remarks>
    /// Selection is kept per producing port, by the shape's index in that
    /// port's set. A panel drawing several sets at once numbers them as one
    /// list, so it has to say which part of the list each port is, or the
    /// fifth shape here would light up the fifth row of a table that does
    /// not hold it. Called on a worker thread with the shapes
    /// <see cref="Read"/> gave.
    /// </remarks>
    protected virtual IReadOnlyList<ShapeSpan> SpansOf(GraphScheduler scheduler, ShapeSet? shapes) =>
        ShapeProducer is { } producer ? [new ShapeSpan(producer, 0, shapes?.Count ?? 0)] : [];

    /// <summary>
    /// Reads what this panel draws out of the evaluated graph.
    /// </summary>
    /// <remarks>
    /// The one thing panels showing an image disagree about. A viewer node
    /// reads its own two inputs; a preview reads whichever port it was opened
    /// on. Everything else here - the conversion, the profiling buffer, the
    /// selection - is the same either way.
    ///
    /// Called on a worker thread, so it must only read the scheduler's cache.
    /// </remarks>
    protected abstract (ImageData? Image, ShapeSet? Shapes) Read(GraphScheduler scheduler);

    /// <summary>
    /// Which of the node's pictures this panel shows, for keying
    /// measurements. Empty for a Viewer node's own panel, which has only
    /// the one.
    /// </summary>
    private readonly string _measurementView;

    /// <summary>Exposed for tests; set once, at construction.</summary>
    internal string MeasurementView => _measurementView;

    /// <summary>
    /// Measurements drawn on this panel, stored on the node so they are
    /// saved with the recipe and counted as a change to it.
    /// </summary>
    /// <remarks>
    /// The node keeps every view's measurements together; a panel reads and
    /// writes only its own and leaves the others untouched, so a preview of
    /// an output cannot clear what was drawn on the input.
    /// </remarks>
    public IReadOnlyList<MeasurementRecord> Measurements
    {
        get => _measurements;
        set
        {
            // Null rather than empty for the unkeyed view, so what is held
            // in memory reads the same as what is written to the file.
            var view = string.IsNullOrEmpty(MeasurementView) ? null : MeasurementView;

            var mine = (value ?? [])
                .Select(record => record with { View = view })
                .ToArray();

            _writingMeasurements = true;
            try
            {
                Node.Measurements =
                [
                    .. Node.Measurements.Where(record => !IsMine(record)),
                    .. mine,
                ];
            }
            finally
            {
                _writingMeasurements = false;
            }

            SetProperty(ref _measurements, mine);
        }
    }

    private bool IsMine(MeasurementRecord record) =>
        string.Equals(record.View ?? string.Empty, MeasurementView, StringComparison.Ordinal);

    private void ReadMeasurements() => SetProperty(
        ref _measurements,
        Node.Measurements.Where(IsMine).ToArray(),
        nameof(Measurements));

    /// <summary>The selected shape, counting from zero, or -1 for none.</summary>
    /// <remarks>
    /// One shape at a time, across every set drawn here: picking a shape
    /// puts down whatever was picked from the other sets, so a table on one
    /// of them never shows a row selected that this panel does not.
    /// </remarks>
    public int SelectedShapeIndex
    {
        get
        {
            // The set picked from last, then any other with a pick in it.
            foreach (var span in _spans.OrderByDescending(span => ReferenceEquals(span.Source, _shownSource)))
            {
                if (_selection.For(span.Source) is { } local && local < span.Count)
                    return span.Start + local;
            }

            return -1;
        }
        set
        {
            var hit = _spans.FirstOrDefault(span => value >= span.Start && value < span.Start + span.Count);

            // The pick first, so the panel never reads as having nothing
            // selected while the others are put down.
            if (hit.Source is not null)
            {
                _shownSource = hit.Source;
                _selection.Select(hit.Source, value - hit.Start);
            }

            foreach (var span in _spans)
            {
                if (!ReferenceEquals(span.Source, hit.Source))
                    _selection.Select(span.Source, null);
            }
        }
    }

    private BitmapSource? _pendingImage;
    private string? _pendingSize;

    /// <summary>
    /// Raised when the panel needs a pass to capture the frame it will sample.
    /// </summary>
    /// <remarks>
    /// Turning the profile tool on cannot reach back to the image that produced
    /// what is already on screen - the graph reclaimed that buffer when the
    /// pass ended. Asking for a fresh pass is what stops the first line drawn
    /// from reading blank.
    /// </remarks>
    public event EventHandler? ProfileDataRequested;

    public override void Prepare(GraphScheduler scheduler)
    {
        // A pass that is cancelled between Prepare and Commit would otherwise
        // strand this buffer with nothing left holding it.
        _pendingProfileSource?.Dispose();
        _pendingProfileSource = null;

        var (image, shapes) = Read(scheduler);

        // The conversion is the costly part, and the result is frozen, so it
        // can be built here and handed to the UI thread as-is.
        _pendingImage = MatImageConverter.ToBitmapSource(image);
        _pendingSize = image is null ? null : $"{image.Width} x {image.Height} x {image.Channels}";

        // Managed geometry, so it can be handed straight over - there is no
        // native buffer here to go out from under the UI thread.
        _pendingShapes = shapes;
        _pendingSpans = SpansOf(scheduler, shapes);

        // Prepared here, where the graph's buffer is still alive. Keeping the
        // ImageData itself instead would be a use-after-free waiting to happen:
        // the scheduler disposes it as soon as the pass completes.
        if (_isProfilingEnabled && image is not null)
            _pendingProfileSource = LineProfileSource.From(image);

        if (Log.IsEnabled)
            Log.Write(
                $"panel {Log.Id(this)} '{Title}' prepare profiling={_isProfilingEnabled} " +
                $"image={image is not null} captured={_pendingProfileSource is not null}");
    }

    public override void Commit()
    {
        Image = _pendingImage;
        SizeText = _pendingSize;
        _spans = _pendingSpans;
        Shapes = _pendingShapes;

        // A pick past the end of a set that came back shorter is gone.
        foreach (var span in _spans)
        {
            if (_selection.For(span.Source) >= span.Count)
                _selection.Select(span.Source, null);
        }

        OnPropertyChanged(nameof(SelectedShapeIndex));

        ProfileSource = _pendingProfileSource;
        _pendingProfileSource = null;

        // Prepare runs on a worker while the switch is flipped on the UI
        // thread, so a pass already past Prepare commits nothing to sample
        // even though profiling is now on. Asking again costs one pass and
        // ends there: the next Prepare sees the switch and captures a frame.
        if (Log.IsEnabled)
            Log.Write($"panel {Log.Id(this)} '{Title}' commit source={Log.Id(_profileSource)}");

        if (_isProfilingEnabled && _profileSource is null && Image is not null)
            ProfileDataRequested?.Invoke(this, EventArgs.Empty);

        RefreshRegion();
    }

    /// <summary>Geometry wired into this viewer, drawn over the image.</summary>
    public ShapeSet? Shapes
    {
        get => _shapes;
        private set => SetProperty(ref _shapes, value);
    }

    /// <summary>
    /// A panel for every profile line drawn on this viewer.
    /// </summary>
    /// <remarks>
    /// Filled by the viewer control, which owns the lines, and mirrored into
    /// the docking layout by the shell. Each one is a window in its own right
    /// so profiles can be compared side by side, or floated onto a second
    /// screen - which a strip under the image could not do.
    /// </remarks>
    public ObservableCollection<ProfileLinePanelViewModel> ProfileLines { get; } = new();

    /// <summary>
    /// This panel's frame, prepared for repeated line sampling, or null when
    /// profiling is off.
    /// </summary>
    public LineProfileSource? ProfileSource
    {
        get => _profileSource;
        private set
        {
            if (ReferenceEquals(_profileSource, value))
                return;

            var previous = _profileSource;
            SetProperty(ref _profileSource, value);
            previous?.Dispose();
        }
    }

    /// <summary>
    /// Whether this panel keeps a copy of its frame so lines can be profiled
    /// on the image.
    /// </summary>
    /// <remarks>
    /// Off by default. The buffer is four bytes per pixel per channel - so
    /// three times that again for a colour frame - and lives for as long as
    /// the frame is on screen, which is not worth paying on every viewer when
    /// most of them will never be profiled.
    /// </remarks>
    public bool IsProfilingEnabled
    {
        get => _isProfilingEnabled;
        set
        {
            if (!SetProperty(ref _isProfilingEnabled, value))
                return;

            Log.Write(
                $"panel {Log.Id(this)} '{Title}' profiling={value} " +
                $"listeners={(ProfileDataRequested is null ? "none" : "yes")}");

            if (value)
                ProfileDataRequested?.Invoke(this, EventArgs.Empty);
            else
                ProfileSource = null;
        }
    }

    public override void Dispose()
    {
        foreach (var line in ProfileLines)
            line.Dispose();

        ProfileLines.Clear();

        ProfileSource = null;

        _pendingProfileSource?.Dispose();
        _pendingProfileSource = null;
    }

    public BitmapSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public string? SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    /// <summary>
    /// The selected node's region parameter, if it has one. Shared across every
    /// viewer so a region can be drawn on whichever panel shows the useful
    /// image, not only on one designated panel.
    /// </summary>
    public ParameterViewModel? RegionParameter
    {
        get => _regionParameter;
        set
        {
            if (SetProperty(ref _regionParameter, Accepts(value) ? value : null))
            {
                OnPropertyChanged(nameof(Roi));
                OnPropertyChanged(nameof(IsRoiEnabled));
                OnPropertyChanged(nameof(RoiAngle));
                OnPropertyChanged(nameof(RoiColour));
                OnPropertyChanged(nameof(RoiLabel));
            }
        }
    }

    public bool IsRoiEnabled => _regionParameter is not null;

    /// <summary>
    /// The colour the region's box is drawn in - its own, so which of a
    /// node's regions is being dragged can be seen.
    /// </summary>
    public System.Windows.Media.Color RoiColour
    {
        get
        {
            try
            {
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    _regionParameter?.RegionColour ?? "#F59E0B");
            }
            catch (FormatException)
            {
                return System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);
            }
        }
    }

    /// <summary>The region's name, written beside its box: "Search Region".</summary>
    public string? RoiLabel => _regionParameter?.DisplayName;

    /// <summary>
    /// The selected node's search band, if it has one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RegionParameter"/> rather than one
    /// "geometry parameter" of either kind: a node can have both - a Find
    /// Circle inside a region of interest - and the viewer draws and edits
    /// them with different gestures.
    /// </remarks>
    public ParameterViewModel? RingParameter
    {
        get => _ringParameter;
        set
        {
            if (SetProperty(ref _ringParameter, AcceptsRing(value) ? value : null))
            {
                OnPropertyChanged(nameof(Ring));
                OnPropertyChanged(nameof(IsRingEnabled));
            }
        }
    }

    public bool IsRingEnabled => _ringParameter is not null;

    /// <summary>
    /// The selected node's search path, if it has one - Find Curve's. A
    /// third kind beside the region and the band, for the same reason the
    /// band is a second: drawn with a gesture of its own.
    /// </summary>
    public ParameterViewModel? PathParameter
    {
        get => _pathParameter;
        set
        {
            if (SetProperty(ref _pathParameter, Accepts(value) ? value : null))
            {
                OnPropertyChanged(nameof(GuidePath));
                OnPropertyChanged(nameof(IsPathEnabled));
            }
        }
    }

    public bool IsPathEnabled => _pathParameter is not null;

    public PathBand? GuidePath
    {
        get => _pathParameter?.PathValue;
        set
        {
            if (_pathParameter is null || value is null || _pathParameter.PathValue == value)
                return;

            _pathParameter.PathValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The tool this panel's viewer has in hand.
    /// </summary>
    /// <remarks>
    /// Two-way, so the shell can arm one. A Draw button that opened the
    /// right picture and then left the operator to find the right tool on
    /// the strip would be doing half its job - and the half it skipped is
    /// the one somebody who has not used the application before cannot
    /// guess.
    /// </remarks>
    public ViewerTool ActiveTool
    {
        get => _activeTool;
        set => SetProperty(ref _activeTool, value);
    }

    public Ring Ring
    {
        get => _ringParameter?.RingValue ?? default;
        set
        {
            if (_ringParameter is null || _ringParameter.RingValue == value)
                return;

            _ringParameter.RingValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// How far the region is turned, in degrees. Zero for a plain
    /// rectangle, which has no angle to offer.
    /// </summary>
    public double RoiAngle
    {
        get => _regionParameter?.RegionAngle ?? 0;
        set
        {
            if (_regionParameter is null || Math.Abs(_regionParameter.RegionAngle - value) < 1e-9)
                return;

            _regionParameter.RegionAngle = value;
            OnPropertyChanged();
        }
    }

    public Rectangle Roi
    {
        get => _regionParameter?.RegionValue ?? Rectangle.Empty;
        set
        {
            if (_regionParameter is null || _regionParameter.RegionValue == value)
                return;

            _regionParameter.RegionValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a region may be edited on this panel.
    /// </summary>
    /// <remarks>
    /// A viewer shows whatever is wired into it and cannot know whose
    /// coordinates that is, so it takes any region - the behaviour this
    /// panel has always had. A preview knows exactly which port it is
    /// showing and refuses the ones that are not in its space.
    /// </remarks>
    /// <remarks>
    /// No, by default, for both kinds. A Viewer node is where results are
    /// looked at, and a region or a band drawn over them is clutter from a
    /// different job - it was appearing on every viewer in the recipe at
    /// once, simply because a node that owns one happened to be selected.
    /// Only the preview of the owning node's own input says yes: the one
    /// picture the geometry is measured in, and the one place editing it
    /// makes sense.
    ///
    /// This retires the broadcast half of ActiveRegionParameter, which the
    /// roadmap had down as a thing geometry parameters would eventually
    /// undo. The parameter is still pushed to the panels; they are simply
    /// the ones entitled to it now.
    /// </remarks>
    protected virtual bool Accepts(ParameterViewModel? parameter) => parameter is null;

    /// <summary>
    /// Whether a search band may be drawn and shown on this panel.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Accepts"/> only because a node can have
    /// both and they are answered about different parameters; the rule is
    /// the same.
    /// </remarks>
    protected virtual bool AcceptsRing(ParameterViewModel? parameter) => Accepts(parameter);

    /// <summary>Re-reads the region after it was changed elsewhere.</summary>
    public void RefreshRegion()
    {
        OnPropertyChanged(nameof(Roi));
        OnPropertyChanged(nameof(RoiAngle));
        OnPropertyChanged(nameof(Ring));
        OnPropertyChanged(nameof(GuidePath));
    }
}

/// <summary>
/// One dockable image panel, backed by a single <see cref="ViewerNode"/>.
/// </summary>
/// <remarks>
/// The observation points of a recipe: placed deliberately, saved with the
/// layout, and arranged for whoever runs it. A
/// <see cref="PreviewPanelViewModel"/> is the other kind - opened on a node
/// to configure or inspect it, and closed again.
/// </remarks>
public sealed class ViewerPanelViewModel : ImagePanelViewModel
{
    public ViewerPanelViewModel(ViewerNode node, ShapeSelectionService selection)
        : base(node, selection, string.Empty) => Source = node;

    public ViewerNode Source { get; }

    protected override string ContentIdPrefix => "viewer";

    protected override (ImageData? Image, ShapeSet? Shapes) Read(GraphScheduler scheduler) =>
        (scheduler.GetInput<ImageData>(Source.ImageIn), ReadShapes(scheduler));

    /// <summary>
    /// Each drawn set's place in the merged list, keyed by the port that
    /// produced it - so a blob picked here is the blob a table on Find
    /// Blobs shows, even with a fitted curve drawn over the same picture.
    /// </summary>
    protected override IReadOnlyList<ShapeSpan> SpansOf(GraphScheduler scheduler, ShapeSet? shapes)
    {
        var spans = new List<ShapeSpan>();
        var start = 0;

        foreach (var (socket, set) in Drawn(scheduler))
        {
            if (socket.Connection?.Source is { } source)
                spans.Add(new ShapeSpan(source, start, set.Count));

            start += set.Count;
        }

        return spans;
    }

    /// <summary>
    /// Every connected shapes socket, drawn as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concatenated in socket order, so the lower sockets draw on top - the
    /// same way the layers of anything else stack, and adjustable by moving
    /// a wire rather than by a setting.
    /// </para>
    /// <para>
    /// A set whose frame disagrees with the others is dropped rather than
    /// drawn. Shapes carry the size of the image they were found in, and two
    /// sets measured in different frames cannot both be right over one
    /// picture - drawing them anyway would put geometry in the wrong place
    /// and look like a bad fit rather than a wiring mistake.
    /// </para>
    /// </remarks>
    private ShapeSet? ReadShapes(GraphScheduler scheduler)
    {
        var drawn = Drawn(scheduler);

        if (drawn.Length == 0)
            return Source.ShapeInputs.Select(scheduler.GetInput<ShapeSet>).FirstOrDefault(s => s is not null);

        if (drawn.Length == 1)
            return drawn[0].Set;

        var frame = drawn.Select(each => each.Set).FirstOrDefault(set => set.FrameWidth > 0 && set.FrameHeight > 0);

        return new ShapeSet(
            drawn.SelectMany(each => each.Set.Shapes).ToArray(), frame?.FrameWidth ?? 0, frame?.FrameHeight ?? 0);
    }

    /// <summary>
    /// The sets that are drawn, in socket order, with the socket each came
    /// in on: those with shapes in them, and in the frame of the first
    /// that knows its frame.
    /// </summary>
    private (InputPort Socket, ShapeSet Set)[] Drawn(GraphScheduler scheduler)
    {
        var sets = Source.ShapeInputs
            .Select(socket => (Socket: socket, Set: scheduler.GetInput<ShapeSet>(socket)))
            .Where(each => each.Set is { Count: > 0 })
            .Select(each => (each.Socket, Set: each.Set!))
            .ToArray();

        if (sets.Length < 2)
            return sets;

        // The first set that knows its frame decides; the rest have to agree
        // with it or be left out.
        var frame = sets.FirstOrDefault(each => each.Set.FrameWidth > 0 && each.Set.FrameHeight > 0).Set;
        var width = frame?.FrameWidth ?? 0;
        var height = frame?.FrameHeight ?? 0;

        // Said out loud by the node rather than here: a dropped set is
        // otherwise just shapes that are not there, and the node's status is
        // where this application already reports that sort of thing.
        return sets.Where(each => each.Set.MatchesFrame(width, height)).ToArray();
    }
}
