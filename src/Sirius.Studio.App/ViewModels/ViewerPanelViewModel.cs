using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows.Media.Imaging;
using Sirius.Core.Graph;
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
    private ShapeSet? _shapes;
    private ShapeSet? _pendingShapes;
    private LineProfileSource? _profileSource;
    private LineProfileSource? _pendingProfileSource;
    private bool _isProfilingEnabled;
    private IReadOnlyList<MeasurementRecord> _measurements = [];
    private bool _writingMeasurements;

    protected ImagePanelViewModel(VisionNode node, ShapeSelectionService selection)
        : base(node)
    {
        _selection = selection;

        _selection.Changed += (_, port) =>
        {
            if (ReferenceEquals(port, ShapeProducer))
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
    /// other. Null when this panel shows no shapes.
    /// </summary>
    protected abstract OutputPort? ShapeProducer { get; }

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
    protected virtual string MeasurementView => string.Empty;

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
    public int SelectedShapeIndex
    {
        get => _selection.For(ShapeProducer) ?? -1;
        set => _selection.Select(ShapeProducer, value);
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
        Shapes = _pendingShapes;

        if (SelectedShapeIndex >= (Shapes?.Count ?? 0))
            _selection.Select(ShapeProducer, null);

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
            }
        }
    }

    public bool IsRoiEnabled => _regionParameter is not null;

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
    protected virtual bool Accepts(ParameterViewModel? parameter) => true;

    /// <summary>Re-reads the region after it was changed elsewhere.</summary>
    public void RefreshRegion()
    {
        OnPropertyChanged(nameof(Roi));
        OnPropertyChanged(nameof(RoiAngle));
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
        : base(node, selection) => Source = node;

    public ViewerNode Source { get; }

    protected override string ContentIdPrefix => "viewer";

    /// <summary>
    /// The port the drawn shapes came from, when they all came from one.
    /// </summary>
    /// <remarks>
    /// This key is what ties a blob on the image to a row in a table reading
    /// the same wire, and it does that by index. Merge two producers and the
    /// indices no longer mean the same thing on both sides - picking the
    /// fifth shape on the image would light up the fifth row of a table that
    /// does not contain it. So a merged viewer shares no selection at all,
    /// which is honest, rather than sharing a wrong one.
    /// </remarks>
    protected override OutputPort? ShapeProducer
    {
        get
        {
            var connected = Source.ShapeInputs
                .Where(port => port.Connection is not null)
                .ToArray();

            return connected.Length == 1 ? connected[0].Connection!.Source : null;
        }
    }

    protected override (ImageData? Image, ShapeSet? Shapes) Read(GraphScheduler scheduler) =>
        (scheduler.GetInput<ImageData>(Source.ImageIn), ReadShapes(scheduler));

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
        var sets = Source.ShapeInputs
            .Select(scheduler.GetInput<ShapeSet>)
            .Where(set => set is { Count: > 0 })
            .Cast<ShapeSet>()
            .ToArray();

        if (sets.Length == 0)
            return Source.ShapeInputs.Select(scheduler.GetInput<ShapeSet>).FirstOrDefault(s => s is not null);

        if (sets.Length == 1)
            return sets[0];

        // The first set that knows its frame decides; the rest have to agree
        // with it or be left out.
        var frame = sets.FirstOrDefault(set => set.FrameWidth > 0 && set.FrameHeight > 0);
        var width = frame?.FrameWidth ?? 0;
        var height = frame?.FrameHeight ?? 0;

        // Said out loud by the node rather than here: a dropped set is
        // otherwise just shapes that are not there, and the node's status is
        // where this application already reports that sort of thing.
        var kept = sets.Where(set => set.MatchesFrame(width, height));

        return new ShapeSet(kept.SelectMany(set => set.Shapes).ToArray(), width, height);
    }
}
