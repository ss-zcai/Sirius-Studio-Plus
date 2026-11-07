using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
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
public abstract class ImagePanelViewModel : NodePanelViewModel, IOriginalPicture
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

    /// <summary>
    /// The last frame's own pixels, when it is deeper than 8 bits - for
    /// saving it as it is. See <see cref="ReadOriginal"/>.
    /// </summary>
    /// <remarks>
    /// Two buffers, filled and swapped rather than allocated: a 12-bit camera
    /// running live would otherwise put a new frame-sized array on the heap
    /// for every refresh, to be saved perhaps once. An 8-bit frame needs
    /// neither - its display bitmap is already its own values.
    /// </remarks>
    private Emgu.CV.Mat? _deep;

    // The bits the deep frame says it uses, for showing it by them.
    private int _deepBits = MatImageConverter.FullRange;
    private int _pendingDeepBits = MatImageConverter.FullRange;

    private Emgu.CV.Mat? _pendingDeep;

    private bool _pendingIsDeep;

    private readonly object _deepLock = new();
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
            if (!_spans.Any(span => ShapeSelectionService.Same(span.Source, port)))
                return;

            // What was picked last is what this panel shows, wherever it was
            // picked - here, or in a table reading one of the same wires.
            if (_selection.For(port) is not null)
                _shownSource = port;

            RaiseSelectedShape();
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
            var (index, _, _) = Selected();
            return index;
        }
        set
        {
            var hit = _spans.FirstOrDefault(span => value >= span.Start && value < span.Start + span.Count);

            // The pick first, so the panel never reads as having nothing
            // selected while the others are put down.
            if (hit.Source is not null)
            {
                _shownSource = ShapeSelectionService.Origin(hit.Source);
                _selection.Select(hit.Source, value - hit.Start);
            }

            foreach (var span in _spans)
            {
                if (!ShapeSelectionService.Same(span.Source, hit.Source))
                    _selection.Select(span.Source, null);
            }
        }
    }

    /// <summary>
    /// The selected shape's row in its own set, counting from one - the
    /// number a table on that set shows beside it - or 0 for none.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SelectedShapeIndex"/> + 1: with a curve drawn over
    /// the blobs, the second blob is the third shape here but row 2 in the
    /// blobs' table, and the number on the image is there to find the row.
    /// </remarks>
    /// <remarks>
    /// Picked in a table on another wire of the same shapes - the finder's,
    /// with this viewer drawing what a filter kept - it is that table's row:
    /// the 3rd kept blob is labelled 7 when row 7 was clicked.
    /// </remarks>
    public int SelectedShapeNumber
    {
        get
        {
            var (_, local, source) = Selected();

            if (source is null)
                return 0;

            return _selection.PickedOn(source) is { } picked && _selection.For(picked) is { } row
                ? row + 1
                : local + 1;
        }
    }

    /// <summary>The selected shape's place here and in its own set, and its set's wire; -1, -1 and null for none.</summary>
    private (int Index, int Local, OutputPort? Source) Selected()
    {
        // The set picked from last, then any other with a pick in it.
        foreach (var span in _spans.OrderByDescending(span => ShapeSelectionService.Same(span.Source, _shownSource)))
        {
            if (_selection.For(span.Source) is { } local && local < span.Count)
                return (span.Start + local, local, span.Source);
        }

        return (-1, -1, null);
    }

    private void RaiseSelectedShape()
    {
        OnPropertyChanged(nameof(SelectedShapeNumber));
        OnPropertyChanged(nameof(SelectedShapeIndex));
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

        // Copied here, while the graph's buffer is alive, as the profile
        // source is below.
        lock (_deepLock)
        {
            _pendingIsDeep = image is not null && image.Mat.Depth != Emgu.CV.CvEnum.DepthType.Cv8U;

            if (_pendingIsDeep)
            {
                _pendingDeep ??= new Emgu.CV.Mat();
                image!.Mat.CopyTo(_pendingDeep);
                _pendingDeepBits = image.Bits;
            }
        }

        // The conversion is the costly part, and the result is frozen, so it
        // can be built here and handed to the UI thread as-is.
        _pendingImage = MatImageConverter.ToBitmapSource(image, DisplayBits);
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
        lock (_deepLock)
        {
            // The spare buffer is kept for the next deep frame.
            if (_pendingIsDeep)
            {
                (_deep, _pendingDeep) = (_pendingDeep, _deep);
                _deepBits = _pendingDeepBits;
            }
            else if (_deep is not null)
            {
                if (_pendingDeep is null)
                    _pendingDeep = _deep;
                else
                    _deep.Dispose();

                _deep = null;
            }
        }

        Image = _pendingImage;
        SizeText = _pendingSize;
        OnPropertyChanged(nameof(IsSixteenBit));
        _spans = _pendingSpans;
        Shapes = _pendingShapes;

        // A pick past the end of a set that came back shorter is gone.
        foreach (var span in _spans)
        {
            if (_selection.For(span.Source) >= span.Count)
                _selection.Select(span.Source, null);
        }

        RaiseSelectedShape();

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

    /// <summary>
    /// How deep the picture on show is, when deeper than the 8 bits it is
    /// displayed in; null when the display bitmap already is its own values.
    /// </summary>
    public Emgu.CV.CvEnum.DepthType? OriginalDepth
    {
        get
        {
            lock (_deepLock)
                return _deep?.Depth;
        }
    }

    /// <summary>
    /// What the last preview was set to, for the next one opened: a
    /// recipe's 12-bit images are 12-bit wherever they are looked at.
    /// </summary>
    private static int _lastDisplayBits = MatImageConverter.Auto;

    private int _displayBits = _lastDisplayBits;

    /// <summary>
    /// How a 16-bit picture is shown - the bits that carry it, or
    /// <see cref="MatImageConverter.Stretch"/>. See
    /// <see cref="MatImageConverter.ToBitmapSource(Emgu.CV.Mat?, int)"/>.
    /// </summary>
    /// <remarks>
    /// Redrawn at once from the frame on show, without running anything:
    /// how a picture looks is not something the recipe computes.
    /// </remarks>
    public int DisplayBits
    {
        get => ReadDisplayBits();
        set
        {
            if (value == ReadDisplayBits())
                return;

            WriteDisplayBits(value);
            OnPropertyChanged();

            lock (_deepLock)
            {
                if (_deep is { Depth: Emgu.CV.CvEnum.DepthType.Cv16U })
                    Image = MatImageConverter.ToBitmapSource(_deep, value, _deepBits);
            }
        }
    }

    /// <summary>Where the setting is kept: the panel, or the Viewer node's recipe.</summary>
    protected virtual int ReadDisplayBits() => _displayBits;

    protected virtual void WriteDisplayBits(int bits) => _displayBits = _lastDisplayBits = bits;

    /// <summary>Whether the picture on show is 16-bit, which is when how it is shown is a choice.</summary>
    public bool IsSixteenBit => OriginalDepth == Emgu.CV.CvEnum.DepthType.Cv16U;

    /// <summary>
    /// The picture's own value at a pixel - "I 4095" on a 12-bit frame,
    /// not the 8-bit level it is shown at - or null for an 8-bit picture.
    /// </summary>
    public string? Reading(int x, int y)
    {
        lock (_deepLock)
        {
            if (_deep is not { } mat || x < 0 || y < 0 || x >= mat.Width || y >= mat.Height)
                return null;

            var values = new double[mat.NumberOfChannels];
            var row = mat.DataPointer + (nint)((long)y * mat.Step);
            var size = mat.ElementSize / values.Length;

            for (var c = 0; c < values.Length; c++)
            {
                var at = ((x * values.Length) + c) * size;

                values[c] = mat.Depth switch
                {
                    Emgu.CV.CvEnum.DepthType.Cv16U => (ushort)Marshal.ReadInt16(row, at),
                    Emgu.CV.CvEnum.DepthType.Cv16S => Marshal.ReadInt16(row, at),
                    Emgu.CV.CvEnum.DepthType.Cv32S => Marshal.ReadInt32(row, at),
                    Emgu.CV.CvEnum.DepthType.Cv32F => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(row, at)),
                    Emgu.CV.CvEnum.DepthType.Cv64F => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(row, at)),
                    Emgu.CV.CvEnum.DepthType.Cv8S => (sbyte)Marshal.ReadByte(row, at),
                    _ => double.NaN,
                };
            }

            string Text(double value) => value.ToString(
                mat.Depth is Emgu.CV.CvEnum.DepthType.Cv32F or Emgu.CV.CvEnum.DepthType.Cv64F ? "0.###" : "0",
                System.Globalization.CultureInfo.CurrentCulture);

            // OpenCV's colour is BGR.
            return values.Length switch
            {
                1 => $"I {Text(values[0])}",
                3 => $"R {Text(values[2])}  G {Text(values[1])}  B {Text(values[0])}",
                4 => $"R {Text(values[2])}  G {Text(values[1])}  B {Text(values[0])}  A {Text(values[3])}",
                _ => string.Join(' ', values.Select(Text)),
            };
        }
    }

    /// <summary>Saves the picture on show with its own values - 16-bit, float.</summary>
    public void SaveOriginal(string path)
    {
        lock (_deepLock)
        {
            if (_deep is null)
                throw new InvalidOperationException("The picture on show is 8-bit; save the displayed one.");

            ImageFileWriter.Save(_deep, path);
        }
    }

    public override void Dispose()
    {
        lock (_deepLock)
        {
            _deep?.Dispose();
            _pendingDeep?.Dispose();
            (_deep, _pendingDeep) = (null, null);
        }

        // Parameters outlive the panels that show them.
        RegionParameter = null;
        RingParameter = null;
        PathParameter = null;

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
            var old = _regionParameter;

            if (SetProperty(ref _regionParameter, Accepts(value) ? value : null))
            {
                Follow(old, _regionParameter);
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
            var old = _ringParameter;

            if (SetProperty(ref _ringParameter, AcceptsRing(value) ? value : null))
            {
                Follow(old, _ringParameter);
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
            var old = _pathParameter;

            if (SetProperty(ref _pathParameter, Accepts(value) ? value : null))
            {
                Follow(old, _pathParameter);
                OnPropertyChanged(nameof(GuidePath));
                OnPropertyChanged(nameof(IsPathEnabled));
            }
        }
    }

    public bool IsPathEnabled => _pathParameter is not null;

    public PathBand? GuidePath
    {
        get => _pathParameter?.ShownPath;
        set
        {
            if (_pathParameter is null || value is null || _pathParameter.ShownPath == value)
                return;

            using (Edit(_pathParameter))
                _pathParameter.ShownPath = value;

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

    /// <summary>
    /// The band as the node last searched it - round a wired circle, say -
    /// rather than as it was drawn, when the two differ. See
    /// <see cref="ParameterViewModel.ShownRing"/>.
    /// </summary>
    public Ring Ring
    {
        get => _ringParameter?.ShownRing ?? default;
        set
        {
            if (_ringParameter is null || _ringParameter.ShownRing == value)
                return;

            using (Edit(_ringParameter))
                _ringParameter.ShownRing = value;

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// How far the region is turned, in degrees. Zero for a plain
    /// rectangle, which has no angle to offer.
    /// </summary>
    public double RoiAngle
    {
        get => _regionParameter?.ShownAngle ?? 0;
        set
        {
            if (_regionParameter is null || Math.Abs(_regionParameter.ShownAngle - value) < 1e-9)
                return;

            using (Edit(_regionParameter))
                _regionParameter.ShownAngle = value;

            OnPropertyChanged();
        }
    }

    public Rectangle Roi
    {
        get => _regionParameter?.ShownRegion ?? Rectangle.Empty;
        set
        {
            if (_regionParameter is null || _regionParameter.ShownRegion == value)
                return;

            using (Edit(_regionParameter))
                _regionParameter.ShownRegion = value;

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

    /// <summary>
    /// Opens one undo step for a drawing, when the shell has said how.
    /// </summary>
    /// <remarks>
    /// A drawing over a band placed round a wired circle sets two settings,
    /// Offset and Band Width. Recorded one by one they alternate, never
    /// merge, and a drag becomes a hundred steps to undo; batched, each
    /// frame of the drag is one edit and the frames merge as any drag's do.
    /// </remarks>
    public Func<string, IDisposable?>? BeginEdit { get; set; }

    private IDisposable? Edit(ParameterViewModel parameter) =>
        BeginEdit?.Invoke($"Change {parameter.DisplayName}");

    /// <summary>
    /// Follows a parameter's shown geometry, which a run can move - placing
    /// a band round a circle that has moved - without anything being edited.
    /// </summary>
    private void Follow(ParameterViewModel? old, ParameterViewModel? now)
    {
        if (old is not null)
            old.PropertyChanged -= OnGeometryChanged;

        if (now is not null)
            now.PropertyChanged += OnGeometryChanged;
    }

    private void OnGeometryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ParameterViewModel.ShownRing) when ReferenceEquals(sender, _ringParameter):
                OnPropertyChanged(nameof(Ring));
                break;

            case nameof(ParameterViewModel.ShownRegion) when ReferenceEquals(sender, _regionParameter):
                OnPropertyChanged(nameof(Roi));
                break;

            case nameof(ParameterViewModel.ShownAngle) when ReferenceEquals(sender, _regionParameter):
                OnPropertyChanged(nameof(RoiAngle));
                break;

            case nameof(ParameterViewModel.ShownPath) when ReferenceEquals(sender, _pathParameter):
                OnPropertyChanged(nameof(GuidePath));
                break;
        }
    }

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

    /// <summary>Kept on the node, so it is saved with the recipe.</summary>
    protected override int ReadDisplayBits() => Source.DisplayBits;

    protected override void WriteDisplayBits(int bits) => Source.DisplayBits = bits;

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
