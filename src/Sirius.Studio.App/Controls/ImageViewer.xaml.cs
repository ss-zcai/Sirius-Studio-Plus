using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Shapes;
using Sirius.Core.Nodes;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Diagnostics;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using DrawingPointF = System.Drawing.PointF;
using GraphShape = Sirius.Core.Shapes.Shape;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// Image viewer with zoom, pan, pixel readout, an interactive region of
/// interest, and measurement annotations.
/// </summary>
/// <remarks>
/// <para>
/// The ROI is the reason this control exists. In the old app a tool's ROI was a
/// <c>Rectangle</c> typed into a property grid, while the shapes drawn on the
/// <c>DiagramControl</c> were unrelated annotations - there was no path from a
/// drawn shape back to the tool that would use it. Here <see cref="Roi"/> binds
/// two-way onto the selected node's region parameter, so dragging on the image
/// re-runs the graph.
/// </para>
/// <para>
/// Measurements are presentation-only: they report what is on screen and are
/// not part of the graph, so they neither trigger evaluation nor persist. They
/// do accumulate - several readings can sit on one image, each selectable,
/// recolourable and individually removable.
/// </para>
/// </remarks>
public partial class ImageViewer : UserControl
{
    /// <summary>Grab distance for an ROI edge, in screen pixels.</summary>
    private const double EdgeTolerance = 6.0;

    /// <summary>Pick distance for a measurement, in screen pixels.</summary>
    private const double PickTolerance = 7.0;

    /// <summary>Grab distance for a handle on the selected measurement.</summary>
    private const double HandleTolerance = 8.0;

    /// <summary>
    /// How far a caliper's reference lines reach either side of the span, as a
    /// fraction of it.
    /// </summary>
    private const double ReferenceArmRatio = 0.22;

    /// <summary>
    /// How far the mouse must move before a press counts as a drag rather than
    /// a click, in screen pixels.
    /// </summary>
    private const double DragThreshold = 4.0;

    private const double MinZoom = 0.02;
    private const double MaxZoom = 64.0;
    private const double ZoomStep = 1.25;

    /// <summary>
    /// The colour a new measurement takes, read from the shared palette so it
    /// is one of the offered swatches rather than a value of its own.
    /// </summary>
    private static readonly Color DefaultColour =
        (Color)ColorConverter.ConvertFromString(StandardColours.All[0].Hex)!;

    private enum DragMode
    {
        None,
        Pan,
        DrawRoi,
        MoveRoi,
        ResizeRoi,
        RotateRoi,

        /// <summary>Dragging out the far end of a measurement.</summary>
        Measure,

        /// <summary>Dragging one point of a placed measurement.</summary>
        MovePoint,

        /// <summary>Dragging a placed measurement bodily.</summary>
        MoveMeasurement,

        /// <summary>Sliding the whole search band.</summary>
        MoveRing,

        /// <summary>Dragging the band's inner edge.</summary>
        ResizeRingInner,

        /// <summary>Dragging the band's outer edge.</summary>
        ResizeRingOuter,
    }

    /// <summary>What part of a search band a point is over.</summary>
    private enum RingPart
    {
        None,
        Inside,
        Inner,
        Outer,
    }

    [Flags]
    internal enum Edge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        Inside = 16,

        /// <summary>The knob above the region, which turns it.</summary>
        Rotate = 32,
    }

    private DragMode _dragMode;
    private Edge _activeEdge;

    /// <summary>The angle the region had when a turn started.</summary>
    private double _dragStartAngle;

    /// <summary>Where the pointer was, relative to the centre, when a turn started.</summary>
    private double _dragStartBearing;
    private Point _dragStartScreen;
    private Point _dragStartImage;
    private DrawingRectangle _dragStartRoi;
    private Size _lastImageSize = Size.Empty;

    /// <summary>Measurements placed on the image, oldest first.</summary>
    private readonly List<Measurement> _measurements = new();

    private Measurement? _selected;

    /// <summary>Which point of the selected measurement is being dragged.</summary>
    private int _editIndex = -1;

    /// <summary>Where the selected measurement sat when a move began.</summary>
    private List<Point> _editStart = new();

    /// <summary>Points confirmed so far for the measurement being placed.</summary>
    private readonly List<Point> _pending = new();

    /// <summary>Which gesture of the active tool's sequence is in progress.</summary>
    private int _gestureIndex;

    /// <summary>
    /// Set when a drag gesture was begun by a press that never moved: the near
    /// point is placed and the far end is awaited as a second click.
    /// </summary>
    private bool _dragAnchored;

    private Point _livePoint;
    private bool _hasLivePoint;

    /// <summary>Colour for new measurements, and for the selected one.</summary>
    private Color _colour = DefaultColour;

    /// <summary>The panel belonging to each profile line on this viewer.</summary>
    private readonly Dictionary<Measurement, ProfileLinePanelViewModel> _profilePanels = new();

    /// <summary>Numbers profile lines. Never reused, so deleting one does not renumber the rest.</summary>
    private int _profileCount;

    /// <summary>
    /// Set once the whole template exists.
    /// </summary>
    /// <remarks>
    /// A ComboBox with a pre-selected item raises SelectionChanged while the
    /// template is still being built, so a handler can run with only part of
    /// the tree in place. Guarding on one named element is not enough - the
    /// element that exists is no guide to the one that does not - so there is
    /// a single flag for "everything is here".
    /// </remarks>
    private bool _templateReady;

    public ImageViewer()
    {
        InitializeComponent();

        // Attached to the canvas, not the control: the toolbar and status bar
        // are children now, and must not pan the image or place points.
        Surface.MouseWheel += OnMouseWheel;
        Surface.MouseDown += OnMouseDown;
        Surface.MouseMove += OnMouseMove;
        Surface.MouseUp += OnMouseUp;
        Surface.MouseLeave += OnMouseLeave;
        Surface.SizeChanged += OnSurfaceSizeChanged;
        KeyDown += OnKeyDown;

        _templateReady = true;

        SyncColourPicker(_colour);
        UpdateHint();
    }

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource),
        typeof(BitmapSource),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty RoiProperty = DependencyProperty.Register(
        nameof(Roi),
        typeof(DrawingRectangle),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            DrawingRectangle.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnRoiChanged));

    /// <summary>
    /// How far the region is turned, in degrees, positive clockwise.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Roi"/>, which stays the upright rectangle
    /// the region would be at zero. Keeping the two apart means every
    /// existing edge and move calculation is unchanged - they work in the
    /// region's own frame, and only the hit test has to get the pointer
    /// into it.
    /// </remarks>
    public static readonly DependencyProperty RoiAngleProperty = DependencyProperty.Register(
        nameof(RoiAngle),
        typeof(double),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnRoiChanged));

    public static readonly DependencyProperty IsRoiEnabledProperty = DependencyProperty.Register(
        nameof(IsRoiEnabled),
        typeof(bool),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(false, OnRoiChanged));

    public static readonly DependencyProperty ActiveToolProperty = DependencyProperty.Register(
        nameof(ActiveTool),
        typeof(ViewerTool),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(ViewerTool.None, OnActiveToolChanged));

    public static readonly DependencyProperty ProfileSourceProperty = DependencyProperty.Register(
        nameof(ProfileSource),
        typeof(LineProfileSource),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(null, OnProfileSourceChanged));

    public static readonly DependencyProperty IsProfilingEnabledProperty = DependencyProperty.Register(
        nameof(IsProfilingEnabled),
        typeof(bool),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>
    /// The measurements drawn here, in a form a recipe can keep.
    /// </summary>
    /// <remarks>
    /// Two-way, because ownership alternates: the control holds them while it
    /// is open and the node holds them the rest of the time. They used to live
    /// only in the control, so an afternoon of measuring disappeared on
    /// File - New without so much as a prompt - nothing in the recipe had
    /// changed, because the measurements were never in it.
    /// </remarks>
    public static readonly DependencyProperty MeasurementsProperty = DependencyProperty.Register(
        nameof(Measurements),
        typeof(IReadOnlyList<MeasurementRecord>),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnMeasurementsChanged));

    /// <summary>
    /// The dimensions of the frame on show, as "2448 x 2048 x 3".
    /// </summary>
    /// <remarks>
    /// Per viewer rather than per window. The shell used to put this in the
    /// main status bar, taking it from whichever viewer panel happened to
    /// be first - so with two viewers open it showed one of them and gave
    /// no clue which, and after a Crop or an ROI the number on screen
    /// belonged to a different picture than the one being looked at.
    /// </remarks>
    public static readonly DependencyProperty ImageSizeTextProperty = DependencyProperty.Register(
        nameof(ImageSizeText),
        typeof(string),
        typeof(ImageViewer),
        new PropertyMetadata(null));

    /// <summary>
    /// The annular search band on show, bound to whichever node owns it.
    /// </summary>
    /// <remarks>
    /// Two-way, like the region of interest: the operator draws it here
    /// and the node that reads it is the one that keeps it.
    /// </remarks>
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(
        nameof(Ring),
        typeof(Ring),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            default(Ring),
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnRingChanged));

    /// <summary>Whether a band may be drawn and is worth showing.</summary>
    public static readonly DependencyProperty IsRingEnabledProperty = DependencyProperty.Register(
        nameof(IsRingEnabled),
        typeof(bool),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(false, OnRingChanged));

    public static readonly DependencyProperty ShapesProperty = DependencyProperty.Register(
        nameof(Shapes),
        typeof(ShapeSet),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(null, OnShapesChanged));

    public static readonly DependencyProperty SelectedShapeIndexProperty = DependencyProperty.Register(
        nameof(SelectedShapeIndex),
        typeof(int),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            -1,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedShapeChanged));

    public static readonly DependencyProperty ProfileLinesProperty = DependencyProperty.Register(
        nameof(ProfileLines),
        typeof(ObservableCollection<ProfileLinePanelViewModel>),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(null));

    public BitmapSource? ImageSource
    {
        get => (BitmapSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    /// <summary>The region of interest, in image pixel coordinates.</summary>
    public DrawingRectangle Roi
    {
        get => (DrawingRectangle)GetValue(RoiProperty);
        set => SetValue(RoiProperty, value);
    }

    /// <summary>
    /// Whether ROI editing is active. Driven by whether the selected node
    /// actually has a region parameter.
    /// </summary>
    public double RoiAngle
    {
        get => (double)GetValue(RoiAngleProperty);
        set => SetValue(RoiAngleProperty, value);
    }

    public Ring Ring
    {
        get => (Ring)GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    public bool IsRingEnabled
    {
        get => (bool)GetValue(IsRingEnabledProperty);
        set => SetValue(IsRingEnabledProperty, value);
    }

    public bool IsRoiEnabled
    {
        get => (bool)GetValue(IsRoiEnabledProperty);
        set => SetValue(IsRoiEnabledProperty, value);
    }

    public ViewerTool ActiveTool
    {
        get => (ViewerTool)GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    /// <summary>
    /// This panel's frame, prepared for line sampling.
    /// </summary>
    /// <remarks>
    /// Owned by the panel view-model, which disposes it when the frame is
    /// replaced. The viewer only reads through it, and must never dispose it -
    /// the same instance may still be in use when a new one arrives.
    /// </remarks>
    public LineProfileSource? ProfileSource
    {
        get => (LineProfileSource?)GetValue(ProfileSourceProperty);
        set => SetValue(ProfileSourceProperty, value);
    }

    /// <summary>
    /// Asks the panel to keep a sampling copy of each frame. Set from here,
    /// because only the viewer knows whether a profile is wanted.
    /// </summary>
    public bool IsProfilingEnabled
    {
        get => (bool)GetValue(IsProfilingEnabledProperty);
        set => SetValue(IsProfilingEnabledProperty, value);
    }

    /// <summary>
    /// Geometry produced by the graph and drawn over the image.
    /// </summary>
    /// <remarks>
    /// Results, not annotations in the editable sense: redrawn from scratch on
    /// every evaluation, and not selectable, because dragging one would imply
    /// it could change what the graph found.
    /// </remarks>
    public ShapeSet? Shapes
    {
        get => (ShapeSet?)GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    /// <summary>
    /// Which graph shape is selected, counting from zero, or -1 for none.
    /// </summary>
    /// <remarks>
    /// Shared with any table showing the same shapes, so clicking a row here
    /// and clicking a blob there are the same act.
    /// </remarks>
    public int SelectedShapeIndex
    {
        get => (int)GetValue(SelectedShapeIndexProperty);
        set => SetValue(SelectedShapeIndexProperty, value);
    }

    /// <summary>
    /// A panel for each profile line, shared with the panel view-model so the
    /// shell can dock them.
    /// </summary>
    public ObservableCollection<ProfileLinePanelViewModel>? ProfileLines
    {
        get => (ObservableCollection<ProfileLinePanelViewModel>?)GetValue(ProfileLinesProperty);
        set => SetValue(ProfileLinesProperty, value);
    }

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ImageViewer)d;
        var image = e.NewValue as BitmapSource;

        viewer.DisplayImage.Source = image;
        viewer.EmptyHint.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;

        if (image is null)
        {
            viewer._lastImageSize = Size.Empty;
            viewer.UpdateReadout(null);
            return;
        }

        viewer.DisplayImage.Width = image.PixelWidth;
        viewer.DisplayImage.Height = image.PixelHeight;

        // Re-fit only when the frame size actually changed. Re-fitting on every
        // evaluation would fight the operator's zoom while they tune a slider.
        var size = new Size(image.PixelWidth, image.PixelHeight);
        if (viewer._lastImageSize != size)
        {
            // Measurements are in image coordinates, so a change of frame size
            // means they now point at features that have moved. Dropping them
            // beats leaving stale readings on screen.
            var hadImage = viewer._lastImageSize != Size.Empty;
            viewer._lastImageSize = size;

            if (hadImage)
                viewer.ClearMeasurements();

            viewer.FitToWindow();
        }

        viewer.UpdateRoiVisual();
    }

    private static void OnRoiChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImageViewer)d).UpdateRoiVisual();

    /// <summary>
    /// A new frame arrived, so any profile on screen is now stale - the line
    /// has not moved, but what lies under it has.
    /// </summary>
    private static void OnProfileSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImageViewer)d).UpdateProfile();

    public string? ImageSizeText
    {
        get => (string?)GetValue(ImageSizeTextProperty);
        set => SetValue(ImageSizeTextProperty, value);
    }

    public IReadOnlyList<MeasurementRecord>? Measurements
    {
        get => (IReadOnlyList<MeasurementRecord>?)GetValue(MeasurementsProperty);
        set => SetValue(MeasurementsProperty, value);
    }

    private static void OnRingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ImageViewer)d;

        viewer.UpdateRingVisual();
        viewer.SyncRingTool();
    }

    private static void OnShapesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImageViewer)d).RedrawShapes();

    /// <summary>
    /// Takes a saved set in - unless this control is what just wrote it.
    /// </summary>
    private static void OnMeasurementsChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ImageViewer)d;

        if (viewer._publishing)
            return;

        viewer.Restore(e.NewValue as IReadOnlyList<MeasurementRecord>);
    }

    private static void OnSelectedShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImageViewer)d).RedrawShapes();

    private static void OnActiveToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ImageViewer)d;

        // Switching tools abandons the half-placed measurement but keeps the
        // ones already on the image.
        viewer.ResetCapture();
        viewer.RedrawMeasurements();
        viewer.UpdateHint();
        viewer.UpdateCursorForTool();

        // Picking the profile tool is what has to ask for the frame. The graph
        // reclaims its buffers when a pass ends, so until a pass runs with
        // profiling on there is nothing to sample and the first line drawn
        // reads blank. This is also what releases the frame, and closes the
        // profile windows, once the tool is put away and no line is left.
        viewer.UpdateProfile();
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastImageSize != Size.Empty && SceneTransform.Matrix.IsIdentity)
            FitToWindow();
    }

    // ---- view transform -------------------------------------------------

    private double Scale => SceneTransform.Matrix.M11;

    /// <summary>One screen pixel, expressed in image units at the current zoom.</summary>
    private double ScreenPixel => 1 / Math.Max(Scale, 1e-6);

    private Point ScreenToImage(Point screen)
    {
        var matrix = SceneTransform.Matrix;
        if (!matrix.HasInverse)
            return screen;

        matrix.Invert();
        return matrix.Transform(screen);
    }

    public void FitToWindow()
    {
        if (_lastImageSize == Size.Empty || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
            return;

        const double padding = 16;
        var scale = Math.Min(
            (Surface.ActualWidth - padding) / _lastImageSize.Width,
            (Surface.ActualHeight - padding) / _lastImageSize.Height);

        scale = Math.Clamp(scale, MinZoom, MaxZoom);

        var matrix = new Matrix();
        matrix.Scale(scale, scale);
        matrix.Translate(
            (Surface.ActualWidth - (_lastImageSize.Width * scale)) / 2,
            (Surface.ActualHeight - (_lastImageSize.Height * scale)) / 2);

        SceneTransform.Matrix = matrix;
        OnTransformChanged();
    }

    private void SetActualSize()
    {
        if (_lastImageSize == Size.Empty)
            return;

        var matrix = new Matrix();
        matrix.Translate(
            (Surface.ActualWidth - _lastImageSize.Width) / 2,
            (Surface.ActualHeight - _lastImageSize.Height) / 2);

        SceneTransform.Matrix = matrix;
        OnTransformChanged();
    }

    /// <summary>Zooms about a point in screen space, keeping it fixed.</summary>
    private void ZoomAt(Point screen, double factor)
    {
        var target = Math.Clamp(Scale * factor, MinZoom, MaxZoom);
        factor = target / Scale;

        if (Math.Abs(factor - 1) < 1e-6)
            return;

        var anchor = ScreenToImage(screen);
        var matrix = SceneTransform.Matrix;
        matrix.ScaleAtPrepend(factor, factor, anchor.X, anchor.Y);
        SceneTransform.Matrix = matrix;

        OnTransformChanged();
    }

    private Point SurfaceCentre() => new(Surface.ActualWidth / 2, Surface.ActualHeight / 2);

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => SetActualSize();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomAt(SurfaceCentre(), ZoomStep);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomAt(SurfaceCentre(), 1 / ZoomStep);

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ImageSource is null)
            return;

        ZoomAt(e.GetPosition(Surface), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void OnTransformChanged()
    {
        ZoomText.Text = $"{Scale * 100:0}%";
        UpdateRoiVisual();
        RedrawMeasurements();
        RedrawShapes();
    }

    // ---- graph geometry --------------------------------------------------

    /// <summary>Colour for shapes whose producer did not choose one.</summary>
    private static readonly Color DefaultShapeColour = Color.FromRgb(0xA7, 0x8B, 0xFA);

    /// <summary>
    /// Draws the shapes the graph produced.
    /// </summary>
    /// <remarks>
    /// Everything of one colour goes into a single geometry and a single
    /// <see cref="Path"/>. A blob finder can return thousands of outlines, and
    /// one element each would cost more in layout than the whole evaluation
    /// did.
    /// </remarks>
    private void RedrawShapes()
    {
        if (!_templateReady)
            return;

        ShapeLayer.Children.Clear();

        if (Shapes is not { Count: > 0 } set)
            return;

        // Shapes carry the size of the image they were found in. A different
        // size means they would be drawn somewhere they were never measured,
        // so they are dimmed and the status bar says why.
        var matches = ImageSource is not { } image || set.MatchesFrame(image.PixelWidth, image.PixelHeight);

        var width = 1.4 * ScreenPixel;
        var selected = SelectedShapeIndex;

        foreach (var group in set.Shapes.GroupBy(shape => (shape.Colour, shape.StrokeWidth)))
        {
            var colour = group.Key.Colour is { } packed
                ? Color.FromArgb(
                    (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
                : DefaultShapeColour;

            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            var geometry = new StreamGeometry();

            using (var context = geometry.Open())
            {
                foreach (var shape in group)
                    AddShape(context, shape);
            }

            geometry.Freeze();

            // A producer may ask for a heavier line - to make a failing
            // feature read louder than a passing one. It is a weight in image
            // units, so the zoom still scales it.
            var weight = group.First().StrokeWidth is { } asked
                ? asked * ScreenPixel
                : width;

            ShapeLayer.Children.Add(new Path
            {
                Data = geometry,
                Stroke = brush,
                StrokeThickness = weight,
                Opacity = matches ? 1.0 : 0.4,
                IsHitTestVisible = false,
            });
        }

        // The selected one is drawn again on top, brighter and heavier, so it
        // stands out from a field of identical blobs.
        if (selected >= 0 && selected < set.Count)
            DrawSelectedShape(set.Shapes[selected], width);

        // Labels are data on the shape, put there by a Shape Style node. The
        // viewer does not invent them: numbering every shape here used to
        // override an upstream "no labels", which is the wrong way round - the
        // graph decides what a result says, the viewer draws it.
        //
        // The exception is the selected shape, which is numbered even when
        // unlabelled. That number is not about the shape, it is the row it
        // corresponds to in a table, and without it a selection in a field of
        // identical blobs says nothing.
        for (var i = 0; i < set.Count; i++)
        {
            var shape = set.Shapes[i];

            if (shape.Label is { Length: > 0 } label)
                DrawShapeLabel(label, shape, LabelBrush(shape));
            else if (i == selected)
                DrawShapeLabel((i + 1).ToString(CultureInfo.CurrentCulture), shape, Brushes.White);
        }

        ReportShapeFrame(set, matches);
    }

    private static void AddShape(StreamGeometryContext context, GraphShape shape)
    {
        switch (shape)
        {
            case PolygonShape { Points.Count: > 1 } polygon:
            {
                var points = polygon.Points;
                context.BeginFigure(new Point(points[0].X, points[0].Y), false, polygon.Closed);

                for (var i = 1; i < points.Count; i++)
                    context.LineTo(new Point(points[i].X, points[i].Y), true, false);

                break;
            }

            case LineShape line:
                context.BeginFigure(new Point(line.Start.X, line.Start.Y), false, false);
                context.LineTo(new Point(line.End.X, line.End.Y), true, false);
                break;

            case OrientedRectShape oriented:
            {
                var corners = oriented.Corners();
                context.BeginFigure(new Point(corners[0].X, corners[0].Y), false, true);

                for (var i = 1; i < corners.Length; i++)
                    context.LineTo(new Point(corners[i].X, corners[i].Y), true, false);

                break;
            }

            case RectShape rect:
                context.BeginFigure(new Point(rect.Rect.X, rect.Rect.Y), false, true);
                context.LineTo(new Point(rect.Rect.Right, rect.Rect.Y), true, false);
                context.LineTo(new Point(rect.Rect.Right, rect.Rect.Bottom), true, false);
                context.LineTo(new Point(rect.Rect.X, rect.Rect.Bottom), true, false);
                break;

            case CircleShape circle:
            {
                // Two arcs, because a StreamGeometry has no ellipse primitive.
                var left = new Point(circle.Centre.X - circle.Radius, circle.Centre.Y);
                var right = new Point(circle.Centre.X + circle.Radius, circle.Centre.Y);
                var size = new Size(circle.Radius, circle.Radius);

                context.BeginFigure(left, false, true);
                context.ArcTo(right, size, 0, false, SweepDirection.Clockwise, true, false);
                context.ArcTo(left, size, 0, false, SweepDirection.Clockwise, true, false);
                break;
            }

            case EllipseShape ellipse:
            {
                // Two arcs again, turned by the ellipse's angle. The arc
                // primitive takes a rotation, so unlike the circle this one
                // cannot be drawn with the axes swapped and no angle.
                var a = ellipse.MajorAxis / 2;
                var b = ellipse.MinorAxis / 2;
                var radians = ellipse.Angle * Math.PI / 180.0;

                var dx = a * Math.Cos(radians);
                var dy = a * Math.Sin(radians);

                var left = new Point(ellipse.Centre.X - dx, ellipse.Centre.Y - dy);
                var right = new Point(ellipse.Centre.X + dx, ellipse.Centre.Y + dy);
                var size = new Size(a, b);

                context.BeginFigure(left, false, true);
                context.ArcTo(right, size, ellipse.Angle, false, SweepDirection.Clockwise, true, false);
                context.ArcTo(left, size, ellipse.Angle, false, SweepDirection.Clockwise, true, false);
                break;
            }

            case PointShape point:
            {
                // A cross, since a zero-length figure draws nothing.
                const double arm = 4;
                context.BeginFigure(new Point(point.At.X - arm, point.At.Y), false, false);
                context.LineTo(new Point(point.At.X + arm, point.At.Y), true, false);
                context.BeginFigure(new Point(point.At.X, point.At.Y - arm), false, false);
                context.LineTo(new Point(point.At.X, point.At.Y + arm), true, false);
                break;
            }
        }
    }

    /// <summary>
    /// A label is written in the colour of the shape it belongs to, so a
    /// styled set reads as one thing.
    /// </summary>
    private static Brush LabelBrush(GraphShape shape)
    {
        if (shape.Colour is not { } packed)
            return Brushes.White;

        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed));

        brush.Freeze();
        return brush;
    }

    /// <summary>Redraws one shape picked out from the rest.</summary>
    private void DrawSelectedShape(GraphShape shape, double width)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
            AddShape(context, shape);

        geometry.Freeze();

        ShapeLayer.Children.Add(new Path
        {
            Data = geometry,
            Stroke = Brushes.White,
            StrokeThickness = width * 2.4,
            IsHitTestVisible = false,
        });
    }

    private void DrawShapeLabel(string text, GraphShape shape, Brush brush)
    {
        var pixel = ScreenPixel;
        var bounds = shape.Bounds;

        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            IsHitTestVisible = false,
            RenderTransform = new ScaleTransform(pixel, pixel),
            RenderTransformOrigin = new Point(0, 0),
        };

        Canvas.SetLeft(label, bounds.X + bounds.Width + (4 * pixel));
        Canvas.SetTop(label, bounds.Y);
        ShapeLayer.Children.Add(label);
    }

    private void ReportShapeFrame(ShapeSet set, bool matches)
    {
        if (matches)
            return;

        HintText.Text =
            $"Shapes were found on a {set.FrameWidth} x {set.FrameHeight} image; " +
            "this one is a different size, so they are drawn where they were measured, not where they are.";
    }

    // ---- toolbar ---------------------------------------------------------

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string name)
            return;

        // Radio behaviour: the tools share one mode, so selecting one releases
        // the rest. A ToggleButton group would need them in the same parent
        // with a GroupName, which the separators break up.
        foreach (var button in ToolButtons())
            button.IsChecked = ReferenceEquals(button, clicked);

        ActiveTool = Enum.TryParse<ViewerTool>(name, out var tool) ? tool : ViewerTool.None;
    }

    /// <summary>
    /// The buttons that choose a tool, which behave as one radio group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identified by carrying a tool name in their Tag. The toolbar holds
    /// other toggles - the overlay switches - and releasing those every time
    /// a tool was picked would turn the overlay off by surprise.
    /// </para>
    /// <para>
    /// Read from the toolbar's items rather than from a button's parent. A
    /// ToolBar moves what does not fit into its overflow menu, so at any
    /// moment the tools are split across two panels - and which half a given
    /// button is in depends on how wide the viewer happens to be.
    /// </para>
    /// </remarks>
    internal IEnumerable<ToggleButton> ToolButtons() =>
        Tools.Items
            .OfType<ToggleButton>()
            .Where(button => button.Tag is string name && Enum.IsDefined(typeof(ViewerTool), name));

    private void OnColourChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once while the template is applied, before the canvas exists.
        if (!_templateReady)
            return;

        if (ColourPicker.SelectedItem is not ColourOption option)
            return;

        if (ColorConverter.ConvertFromString(option.Hex) is not Color colour)
            return;

        _colour = colour;

        // Recolouring the selection is the point of the control: you pick out a
        // measurement that has disappeared into the image and change it.
        if (_selected is { } measurement)
        {
            measurement.Colour = colour;
            Publish();
        }

        RedrawMeasurements();
        UpdateReading();
    }

    /// <summary>Whether results from the graph are drawn.</summary>
    private bool ShowShapes => ShowShapesButton.IsChecked == true;

    /// <summary>Whether hand-drawn measurements are drawn.</summary>
    private bool ShowMeasurements => ShowMeasurementsButton.IsChecked == true;

    private void OnToggleShapesClick(object sender, RoutedEventArgs e)
    {
        ShapeLayer.Visibility = ShowShapes ? Visibility.Visible : Visibility.Collapsed;

        // What cannot be seen cannot be picked, so a hidden selection is put
        // down rather than left highlighted under a hidden layer.
        if (!ShowShapes)
            SelectedShapeIndex = -1;
    }

    private void OnToggleMeasurementsClick(object sender, RoutedEventArgs e)
    {
        MeasureLayer.Visibility = ShowMeasurements ? Visibility.Visible : Visibility.Collapsed;

        if (!ShowMeasurements)
        {
            ResetCapture();
            Select(null);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearMeasurements();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete or Key.Back when _selected is not null:
                DeleteSelected();
                e.Handled = true;
                break;

            case Key.Escape when _ringClicks.Count > 0:
                ResetRing();
                e.Handled = true;
                break;

            case Key.Escape when _pending.Count > 0:
                ResetCapture();
                RedrawMeasurements();
                UpdateReading();
                UpdateHint();
                e.Handled = true;
                break;

            case Key.Escape:
                Select(null);
                SelectedShapeIndex = -1;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Removes every measurement from the image.</summary>
    public void ClearMeasurements()
    {
        _measurements.Clear();
        ResetCapture();
        Select(null);
        Publish();
    }

    private void DeleteSelected()
    {
        if (_selected is not { } measurement)
            return;

        _measurements.Remove(measurement);
        Select(null);
        Publish();
    }

    // ---- measurement capture ---------------------------------------------

    /// <summary>
    /// Advances the active tool's gesture sequence with a press.
    /// </summary>
    /// <returns>
    /// True when the press began a drag, so the caller should capture the mouse
    /// and wait for the release.
    /// </returns>
    private bool BeginMeasureStep(Point image)
    {
        var gestures = Measurement.GesturesFor(ActiveTool);
        if (gestures.Length == 0)
            return false;

        if (_gestureIndex >= gestures.Length)
            ResetCapture();

        if (gestures[_gestureIndex] == GestureKind.Click)
        {
            _pending.Add(image);
            _gestureIndex++;
            FinishStep();
            return false;
        }

        if (_dragAnchored)
        {
            // The press that started this line never moved, so this one gives
            // the far end.
            _pending.Add(image);
            _dragAnchored = false;
            _gestureIndex++;
            FinishStep();
            return false;
        }

        _pending.Add(image);
        _livePoint = image;
        _hasLivePoint = true;
        FinishStep();
        return true;
    }

    private void EndMeasureDrag(Point screen)
    {
        if ((screen - _dragStartScreen).Length < DragThreshold)
        {
            // Barely moved. Rather than commit a zero-length line, keep the
            // near point and let the far end arrive as a second click: a slip
            // of the hand should not produce a measurement of nothing.
            _dragAnchored = true;
            FinishStep();
            return;
        }

        _pending.Add(ClampToImage(ScreenToImage(screen)));
        _gestureIndex++;
        FinishStep();
    }

    /// <summary>Commits once enough points are in, then refreshes the overlay.</summary>
    private void FinishStep()
    {
        if (!_dragAnchored && _pending.Count >= Measurement.PointsRequired(ActiveTool))
        {
            var measurement = new Measurement(ActiveTool, _pending, _colour);

            // Named on creation, because the name is what its window is called
            // and what is drawn beside it on the image.
            if (measurement.Tool == ViewerTool.LineProfile)
                measurement.Name = $"Profile {++_profileCount}";

            _measurements.Add(measurement);
            ResetCapture();
            Select(measurement);
            Publish();
            return;
        }

        RedrawMeasurements();
        UpdateReading();
        UpdateHint();
    }

    /// <summary>True while this control is writing its own property.</summary>
    /// <remarks>
    /// Without it, publishing would come straight back in as a change and
    /// rebuild every measurement from its own record mid-drag.
    /// </remarks>
    private bool _publishing;

    /// <summary>
    /// Replaces what is drawn with a saved set.
    /// </summary>
    private void Restore(IReadOnlyList<MeasurementRecord>? saved)
    {
        _measurements.Clear();
        _selected = null;
        _profileCount = 0;

        foreach (var record in saved ?? [])
        {
            // A tool this build does not have is skipped rather than
            // guessed at: a measurement drawn by a newer one would have
            // points this one cannot interpret.
            if (!Enum.TryParse<ViewerTool>(record.Tool, out var tool) || tool == ViewerTool.None)
                continue;

            if (record.Points.Count < Measurement.PointsRequired(tool))
                continue;

            var colour = ColorConverter.ConvertFromString(record.Colour) is Color parsed
                ? parsed
                : _colour;

            var measurement = new Measurement(
                tool,
                record.Points.Select(point => new Point(point.X, point.Y)),
                colour)
            {
                Name = record.Name ?? string.Empty,
            };

            // Numbering carries on past what was restored, so the next line
            // drawn cannot take a name one of these already has.
            if (tool == ViewerTool.LineProfile)
                _profileCount++;

            _measurements.Add(measurement);
        }

        if (!_templateReady)
            return;

        RedrawMeasurements();
        UpdateReading();
        UpdateProfile();
    }

    /// <summary>
    /// Writes what is drawn back out, so whoever owns the recipe keeps it -
    /// and notices that it changed.
    /// </summary>
    private void Publish()
    {
        var records = _measurements
            .Select(measurement => new MeasurementRecord(
                measurement.Tool.ToString(),
                measurement.Points.Select(point => new Point2(point.X, point.Y)).ToArray(),
                measurement.Colour.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(measurement.Name) ? null : measurement.Name))
            .ToArray();

        _publishing = true;
        try
        {
            Measurements = records;
        }
        finally
        {
            _publishing = false;
        }
    }

    /// <summary>
    /// Clicks collected so far while placing a band: the centre, then a
    /// point on the edge.
    /// </summary>
    private readonly List<Point> _ringClicks = new();

    /// <summary>Where the pointer is while a band is half-placed.</summary>
    private Point? _ringPreview;

    /// <summary>How many clicks the band gesture takes.</summary>
    public const int RingClicks = 3;

    /// <summary>
    /// Advances the band gesture with a click, and finishes it on the third.
    /// </summary>
    /// <remarks>
    /// Centre, then a point on the edge, then a point giving the width
    /// either side of that edge - which is how somebody describes a search
    /// band out loud, and means the second click can be put straight onto
    /// the feature being looked for rather than onto an inner radius that
    /// has to be guessed below it.
    /// </remarks>
    private void AddRingClick(Point image)
    {
        _ringClicks.Add(image);

        if (_ringClicks.Count < RingClicks)
        {
            UpdateRingVisual();
            UpdateHint();
            return;
        }

        var centre = _ringClicks[0];
        var mean = Distance(centre, _ringClicks[1]);
        var half = Math.Abs(Distance(centre, _ringClicks[2]) - mean);

        _ringClicks.Clear();
        _ringPreview = null;

        // Written through the property so the two-way binding carries it to
        // the node; the visual follows from the property changing, not the
        // other way round.
        Ring = Core.Nodes.Ring.FromMean(centre.X, centre.Y, mean, half);

        UpdateHint();
    }

    /// <summary>The band as it would be if the gesture ended here.</summary>
    private Ring? PendingRing()
    {
        if (_ringClicks.Count == 0 || _ringPreview is not { } pointer)
            return null;

        var centre = _ringClicks[0];

        if (_ringClicks.Count == 1)
        {
            // Only a centre so far, so the pointer is the edge and the band
            // is shown hairline - wide enough to see, narrow enough not to
            // suggest a width has been chosen.
            var radius = Distance(centre, pointer);
            return Core.Nodes.Ring.FromMean(centre.X, centre.Y, radius, 0.5);
        }

        var mean = Distance(centre, _ringClicks[1]);
        var half = Math.Abs(Distance(centre, pointer) - mean);

        return Core.Nodes.Ring.FromMean(centre.X, centre.Y, mean, half);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Offers the band tool only where there is a band to write.
    /// </summary>
    /// <remarks>
    /// And takes it away again when there is not, putting the pointer
    /// back - a tool left selected after the node it belonged to was
    /// deselected would swallow clicks meant for something else.
    /// </remarks>
    private void SyncRingTool()
    {
        if (!_templateReady)
            return;

        RingToolButton.Visibility = IsRingEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (IsRingEnabled || ActiveTool != ViewerTool.Ring)
            return;

        ResetRing();

        foreach (var button in ToolButtons())
            button.IsChecked = (button.Tag as string) == nameof(ViewerTool.None);

        ActiveTool = ViewerTool.None;
    }

    /// <summary>
    /// Which part of the band a screen point grabs.
    /// </summary>
    /// <remarks>
    /// The circles are their own handles. Separate knobs would have to be
    /// placed somewhere on a shape that is the same all the way round, and
    /// wherever they went they would be in the way of the feature being
    /// inspected - the thing the band is drawn around.
    ///
    /// The edges win over the inside, so a narrow band can still be
    /// resized rather than only moved.
    /// </remarks>
    private RingPart HitTestRing(Point screen) => HitTestRingAt(ScreenToImage(screen));

    /// <summary>
    /// The same test in image coordinates, which is where the geometry
    /// lives and where it can be checked without a laid-out control.
    /// </summary>
    private RingPart HitTestRingAt(Point image)
    {
        if (!IsRingEnabled || _ringClicks.Count > 0)
            return RingPart.None;

        var ring = Ring;

        if (ring.OuterRadius <= 0)
            return RingPart.None;

        var distance = Distance(new Point(ring.CentreX, ring.CentreY), image);
        var tolerance = Math.Max(HandleTolerance * ScreenPixel, 3);

        if (Math.Abs(distance - ring.OuterRadius) <= tolerance)
            return RingPart.Outer;

        if (Math.Abs(distance - ring.InnerRadius) <= tolerance)
            return RingPart.Inner;

        // Anywhere within the band itself moves it. Not the hole: that is
        // where the part being measured is, and a click there is far more
        // likely meant for the image than for the band.
        return distance > ring.InnerRadius && distance < ring.OuterRadius
            ? RingPart.Inside
            : RingPart.None;
    }

    /// <summary>The band as it was when the current drag began.</summary>
    private Ring _dragStartRing;

    /// <summary>Applies a drag to the band.</summary>
    /// <remarks>
    /// Resizing moves one edge and leaves the other, which is what
    /// dragging an edge means. Ring's constructor reorders if they cross,
    /// so dragging the inner edge past the outer one swaps them rather
    /// than inverting the band.
    /// </remarks>
    private void DragRing(DragMode mode, Point image)
    {
        var centre = new Point(_dragStartRing.CentreX, _dragStartRing.CentreY);

        switch (mode)
        {
            case DragMode.MoveRing:
                Ring = _dragStartRing.WithCentre(
                    _dragStartRing.CentreX + (image.X - _dragStartImage.X),
                    _dragStartRing.CentreY + (image.Y - _dragStartImage.Y));
                break;

            case DragMode.ResizeRingInner:
                Ring = new Ring(
                    _dragStartRing.CentreX,
                    _dragStartRing.CentreY,
                    Distance(centre, image),
                    _dragStartRing.OuterRadius);
                break;

            case DragMode.ResizeRingOuter:
                Ring = new Ring(
                    _dragStartRing.CentreX,
                    _dragStartRing.CentreY,
                    _dragStartRing.InnerRadius,
                    Distance(centre, image));
                break;
        }
    }

    /// <summary>Abandons a half-placed band.</summary>
    private void ResetRing()
    {
        if (_ringClicks.Count == 0 && _ringPreview is null)
            return;

        _ringClicks.Clear();
        _ringPreview = null;
        UpdateRingVisual();
        UpdateHint();
    }

    /// <summary>
    /// Draws the band - the one being placed if there is one, otherwise the
    /// one the node holds.
    /// </summary>
    private void UpdateRingVisual()
    {
        if (!_templateReady)
            return;

        var ring = PendingRing() ?? Ring;
        var visible = IsRingEnabled && ring.OuterRadius > 0;

        RingShape.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
            return;

        var outer = new EllipseGeometry(
            new Point(ring.CentreX, ring.CentreY), ring.OuterRadius, ring.OuterRadius);

        var inner = new EllipseGeometry(
            new Point(ring.CentreX, ring.CentreY), ring.InnerRadius, ring.InnerRadius);

        // EvenOdd, so the hole is a hole: a GeometryGroup filled Nonzero
        // would shade the whole disc and hide the part being inspected.
        RingShape.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { outer, inner },
        };

        RingShape.StrokeThickness = ScreenPixel;
    }

    private void ResetCapture()
    {
        _pending.Clear();
        _gestureIndex = 0;
        _dragAnchored = false;
        _hasLivePoint = false;
    }

    private void Select(Measurement? measurement)
    {
        foreach (var existing in _measurements)
            existing.IsSelected = ReferenceEquals(existing, measurement);

        _selected = measurement;

        // Follow the selection with the colour picker, so changing colour acts
        // on what was just clicked rather than silently on something else.
        if (measurement is not null)
        {
            _colour = measurement.Colour;
            SyncColourPicker(measurement.Colour);
        }

        RedrawMeasurements();
        UpdateReading();
        UpdateHint();
    }

    private void SyncColourPicker(Color colour)
    {
        if (ColourPicker is null)
            return;

        foreach (var option in ColourPicker.Items.OfType<ColourOption>())
        {
            if (ColorConverter.ConvertFromString(option.Hex) is Color candidate &&
                candidate == colour)
            {
                ColourPicker.SelectedItem = option;
                return;
            }
        }
    }

    /// <summary>The points to draw for the measurement in progress, cursor included.</summary>
    private IReadOnlyList<Point> PendingWithLive()
    {
        if (!_hasLivePoint || _pending.Count == 0)
            return _pending;

        return new List<Point>(_pending) { _livePoint };
    }

    private void UpdateProfile()
    {
        if (!_templateReady)
            return;

        SyncProfilingEnabled();
        SyncProfilePanels();
        ResampleProfiles();
    }

    /// <summary>
    /// Gives every profile line a panel, and drops the panels whose line has
    /// gone.
    /// </summary>
    private void SyncProfilePanels()
    {
        if (ProfileLines is not { } panels)
            return;

        foreach (var (measurement, panel) in _profilePanels.ToArray())
        {
            if (_measurements.Contains(measurement))
                continue;

            panel.ResampleRequested -= OnResampleRequested;
            _profilePanels.Remove(measurement);
            panels.Remove(panel);
        }

        foreach (var measurement in _measurements)
        {
            if (measurement.Tool != ViewerTool.LineProfile || _profilePanels.ContainsKey(measurement))
                continue;

            // The identity is per line and never restored from a saved layout -
            // measurements do not outlive the frame they were drawn on, so
            // there would be nothing for a restored window to show.
            var panel = new ProfileLinePanelViewModel(
                $"profileline:{Guid.NewGuid():N}",
                measurement.Name);

            panel.ResampleRequested += OnResampleRequested;

            _profilePanels[measurement] = panel;
            panels.Add(panel);

            Log.Write($"viewer {Log.Id(this)} opened '{panel.Title}' {Log.Id(panel)}");
        }
    }

    private void OnResampleRequested(object? sender, EventArgs e) => ResampleProfiles();

    /// <summary>Pushes a fresh reading into every profile panel.</summary>
    private void ResampleProfiles()
    {
        if (Log.IsEnabled)
            Log.Write(
                $"viewer {Log.Id(this)} resample panels={_profilePanels.Count} " +
                $"source={Log.Id(ProfileSource)}");

        foreach (var (measurement, panel) in _profilePanels)
        {
            panel.Rename(measurement.Name);

            if (ProfileSource is not { } source || measurement.Points.Count < 2)
            {
                // Enabling the tool cannot reach back to the frame already on
                // screen; the panel has asked for a pass, so this is brief.
                panel.Data = null;
                continue;
            }

            panel.Data = source.Sample(
                new DrawingPointF((float)measurement.Points[0].X, (float)measurement.Points[0].Y),
                new DrawingPointF((float)measurement.Points[1].X, (float)measurement.Points[1].Y),
                thickness: panel.Thickness);
        }
    }

    /// <summary>The band width the panel for this line is set to.</summary>
    private int ThicknessFor(Measurement measurement) =>
        _profilePanels.TryGetValue(measurement, out var panel) ? panel.Thickness : 1;

    /// <summary>
    /// Keeps the panel's sampling buffer alive exactly while it is wanted.
    /// </summary>
    /// <remarks>
    /// It costs four bytes per pixel for as long as it is held, so it is turned
    /// off again once the tool is put away and the last profile line is gone.
    /// </remarks>
    private void SyncProfilingEnabled()
    {
        var wanted =
            ActiveTool == ViewerTool.LineProfile ||
            _measurements.Any(measurement => measurement.Tool == ViewerTool.LineProfile);

        // Only when it actually changes. Writing the property the value it
        // already holds replaces the two-way binding behind it with a plain
        // local value - WPF takes the shortcut for an unchanged value and the
        // binding does not survive it. A changing write goes through the
        // binding and reaches the panel as it should.
        //
        // This is not a micro-optimisation. Without the test, the first
        // profile source to arrive triggered this with nothing to change,
        // the binding was gone from that moment, and every later "profiling
        // is on" was written into a dead property: the panel never heard,
        // never asked for a frame, and the chart read "No profile" for ever.
        if (IsProfilingEnabled != wanted)
            IsProfilingEnabled = wanted;

        if (Log.IsEnabled)
            Log.Write(
                $"viewer {Log.Id(this)} profiling={wanted} readback={IsProfilingEnabled} " +
                $"tool={ActiveTool} lines={_measurements.Count(m => m.Tool == ViewerTool.LineProfile)} " +
                $"context={Log.Id(DataContext)}");
    }

    private void UpdateReading()
    {
        UpdateProfile();

        // While placing, the live reading wins: it is what the hand is doing.
        if (_pending.Count > 0)
        {
            var preview = new Measurement(ActiveTool, PendingWithLive(), _colour);
            SetReading(preview.Text, _colour);
            return;
        }

        if (_selected is { } measurement)
        {
            SetReading(measurement.Text, measurement.Colour);
            return;
        }

        SetReading(string.Empty, _colour);
    }

    private void SetReading(string text, Color colour)
    {
        MeasurementText.Text = text;

        // Tinting the readout to match the annotation ties the number in the
        // status bar to the line it came from when several are on screen.
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        MeasurementText.Foreground = brush;
    }

    private void UpdateHint()
    {
        if (ActiveTool == ViewerTool.None)
        {
            if (_selected is not null)
                HintText.Text = "Drag a handle to adjust, the body to move, Delete to remove";
            else if (_measurements.Count > 0)
                HintText.Text = "Click a measurement to select it";
            else
                HintText.Text = string.Empty;

            return;
        }

        if (ActiveTool == ViewerTool.Ring)
        {
            HintText.Text = _ringClicks.Count switch
            {
                0 => "Click the centre of the circle",
                1 => "Click on the edge you are looking for",
                _ => "Click to set how far either side to search",
            };

            return;
        }

        var name = Measurement.DisplayName(ActiveTool);

        HintText.Text = _dragAnchored
            ? $"{name}: click the far end"
            : $"{name}: {StepHint()}";
    }

    /// <summary>What to do next, in the terms the tool is operated in.</summary>
    private string StepHint()
    {
        var placed = _pending.Count;

        return ActiveTool switch
        {
            ViewerTool.PointToPoint =>
                "drag from one point to the other",

            ViewerTool.PointToLine => placed == 0
                ? "click the point"
                : "drag along the line",

            ViewerTool.LineToLineParallel =>
                "drag across the gap, from one line to the other",

            ViewerTool.LineToLineAngled => placed < 2
                ? "drag along the first line"
                : "drag along the second line",

            ViewerTool.Angle => placed switch
            {
                0 => "click the vertex",
                1 => "click along the first arm",
                _ => "click along the second arm",
            },

            ViewerTool.CircleCentre =>
                "drag from the centre out to the edge",

            ViewerTool.CircleThreePoint =>
                $"click {3 - placed} point{(3 - placed == 1 ? string.Empty : "s")} on the curve",

            _ => string.Empty,
        };
    }

    private void UpdateCursorForTool() =>
        Surface.Cursor = ActiveTool == ViewerTool.None ? Cursors.Arrow : Cursors.Cross;

    /// <summary>
    /// Which point of the selected measurement a screen point grabs, or -1.
    /// </summary>
    /// <remarks>
    /// Only the selected measurement offers handles. With every measurement
    /// grabbable at once, a crowded image would be impossible to click on.
    /// </remarks>
    private int HitTestHandle(Point screen)
    {
        if (_selected is not { } measurement)
            return -1;

        var image = ScreenToImage(screen);
        var tolerance = HandleTolerance * ScreenPixel;

        var best = -1;
        var bestDistance = double.MaxValue;

        for (var index = 0; index < measurement.Points.Count; index++)
        {
            var distance = Measurement.Distance(image, measurement.Points[index]);
            if (distance <= tolerance && distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void MoveSelectedPoint(Point image)
    {
        if (_selected is not { } measurement || _editIndex < 0)
            return;

        measurement.Points[_editIndex] = ClampToImage(image);

        RedrawMeasurements();
        UpdateReading();
    }

    /// <summary>
    /// Shifts the whole measurement, keeping its shape.
    /// </summary>
    /// <remarks>
    /// The translation is clamped rather than each point, so dragging against
    /// the edge of the image slides the measurement along it instead of
    /// collapsing it - the same reasoning as moving the region of interest.
    /// </remarks>
    private void MoveSelected(Point image)
    {
        if (_selected is not { } measurement || _editStart.Count == 0)
            return;

        var dx = image.X - _dragStartImage.X;
        var dy = image.Y - _dragStartImage.Y;

        dx = Math.Clamp(dx, -_editStart.Min(p => p.X), _lastImageSize.Width - _editStart.Max(p => p.X));
        dy = Math.Clamp(dy, -_editStart.Min(p => p.Y), _lastImageSize.Height - _editStart.Max(p => p.Y));

        for (var index = 0; index < measurement.Points.Count; index++)
            measurement.Points[index] = new Point(_editStart[index].X + dx, _editStart[index].Y + dy);

        RedrawMeasurements();
        UpdateReading();
    }

    /// <summary>
    /// Which graph shape lies under a screen point, or null.
    /// </summary>
    /// <remarks>
    /// By bounding box, smallest first, rather than by true outline. For found
    /// regions - which are mostly compact and rarely overlap - it picks what
    /// the eye picks, and it costs nothing on a set of thousands.
    /// </remarks>
    private int? PickShape(Point screen)
    {
        if (!ShowShapes || Shapes is not { Count: > 0 } set)
            return null;

        var image = ScreenToImage(screen);
        var tolerance = PickTolerance * ScreenPixel;
        var at = new Point2(image.X, image.Y);

        int? best = null;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < set.Count; i++)
        {
            var shape = set.Shapes[i];
            var bounds = shape.Bounds;

            // Cheap reject first: a point outside the box cannot be near
            // the outline, and this is the test that keeps a set of
            // thousands affordable.
            var near =
                image.X >= bounds.X - tolerance && image.X <= bounds.Right + tolerance &&
                image.Y >= bounds.Y - tolerance && image.Y <= bounds.Bottom + tolerance;

            if (!near)
                continue;

            // Then the outline itself. The box alone made a large shape
            // claim every click inside it - a fitted circle across the
            // whole part swallowed the part and everything drawn on it.
            var distance = ShapeHitTest.DistanceToOutline(shape, at);

            if (distance > tolerance || distance >= bestDistance)
                continue;

            best = i;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>Finds the measurement nearest a screen point, within reach.</summary>
    private Measurement? PickMeasurement(Point screen)
    {
        if (!ShowMeasurements)
            return null;

        var image = ScreenToImage(screen);
        var tolerance = PickTolerance * ScreenPixel;

        Measurement? best = null;
        var bestDistance = double.MaxValue;

        // Later measurements are drawn on top, so on a near-tie the one the eye
        // sees is the one that gets picked.
        foreach (var measurement in _measurements)
        {
            var distance = measurement.DistanceTo(image);
            if (distance <= tolerance && distance <= bestDistance)
            {
                best = measurement;
                bestDistance = distance;
            }
        }

        return best;
    }

    // ---- pointer interaction --------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ImageSource is null)
            return;

        Focus();
        _dragStartScreen = e.GetPosition(Surface);
        _dragStartImage = ScreenToImage(_dragStartScreen);
        _dragStartRoi = Roi;

        // Panning stays on the middle button (and Alt) so the image can still
        // be moved mid-measurement.
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            _dragMode = DragMode.Pan;
        }
        else if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        else if (ActiveTool == ViewerTool.Ring)
        {
            // Its own click handling rather than the measurement gesture
            // sequence: this writes a parameter instead of leaving an
            // annotation, so nothing should be added to _measurements and
            // nothing should be selectable afterwards.
            AddRingClick(ClampToImage(_dragStartImage));
            e.Handled = true;
            return;
        }
        else if (ActiveTool != ViewerTool.None && ShowMeasurements)
        {
            if (!BeginMeasureStep(ClampToImage(_dragStartImage)))
            {
                // A click-only step: there is no release to wait for.
                e.Handled = true;
                return;
            }

            _dragMode = DragMode.Measure;
        }
        else if (HitTestHandle(_dragStartScreen) is var handle and >= 0)
        {
            // A handle on the selected measurement wins over the region: it is
            // the more specific target, and it is only there once selected.
            _editIndex = handle;
            _dragMode = DragMode.MovePoint;
        }
        else if (HitTestRing(_dragStartScreen) is var part and not RingPart.None)
        {
            // Before the region, because a node with both has its band
            // drawn inside its region and the band is the smaller, more
            // specific target.
            _dragStartRing = Ring;

            _dragMode = part switch
            {
                RingPart.Inner => DragMode.ResizeRingInner,
                RingPart.Outer => DragMode.ResizeRingOuter,
                _ => DragMode.MoveRing,
            };
        }
        else if (IsRoiEnabled && HitTestRoi(_dragStartScreen) is var edge and not Edge.None)
        {
            _activeEdge = edge;

            if (edge == Edge.Rotate)
            {
                _dragMode = DragMode.RotateRoi;
                _dragStartAngle = RoiAngle;
                _dragStartBearing = BearingFromCentre(_dragStartImage);
            }
            else
            {
                _dragMode = edge == Edge.Inside ? DragMode.MoveRoi : DragMode.ResizeRoi;
            }
        }
        else if (PickShape(_dragStartScreen) is { } shapeIndex)
        {
            // Graph geometry is picked after the operator's own measurements,
            // which are editable and so have the stronger claim on a click.
            SelectedShapeIndex = shapeIndex;
            Select(null);
            e.Handled = true;
            return;
        }
        else if (PickMeasurement(_dragStartScreen) is { } hit)
        {
            // The first click only selects. Moving is reserved for a drag on
            // something already selected, so clicking to read a value cannot
            // nudge it off the feature it was taken from.
            if (!ReferenceEquals(hit, _selected))
            {
                Select(hit);
                e.Handled = true;
                return;
            }

            _editStart = new List<Point>(hit.Points);
            _dragMode = DragMode.MoveMeasurement;
        }
        else if (IsRoiEnabled)
        {
            // Clicking past everything puts down whatever was picked up, for
            // graph shapes as much as for measurements.
            Select(null);
            SelectedShapeIndex = -1;
            _dragMode = DragMode.DrawRoi;

            var origin = ClampToImage(_dragStartImage);
            Roi = new DrawingRectangle((int)origin.X, (int)origin.Y, 0, 0);
        }
        else
        {
            Select(null);
            SelectedShapeIndex = -1;
            _dragMode = DragMode.Pan;
        }

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (ImageSource is null)
            return;

        var screen = e.GetPosition(Surface);
        var image = ScreenToImage(screen);

        if (_dragMode == DragMode.None)
        {
            UpdateReadout(image);

            // The band is placed with clicks rather than a drag, so nothing
            // is "being dragged" while it is half-placed and this is the
            // only path its preview is updated from. Below the early return
            // it never ran, and the band appeared only once the third click
            // committed it.
            TrackRingPreview(image);

            // Between steps of a multi-part measurement the cursor stands in
            // for the point not yet placed, so the shape is visible before it
            // is committed.
            if (_pending.Count > 0)
            {
                _livePoint = ClampToImage(image);
                _hasLivePoint = true;
                RedrawMeasurements();
                UpdateReading();
            }
            else if (ActiveTool == ViewerTool.None)
            {
                UpdateCursor(screen);
            }

            return;
        }

        switch (_dragMode)
        {
            case DragMode.Pan:
            {
                var delta = screen - _dragStartScreen;
                var matrix = SceneTransform.Matrix;
                matrix.Translate(delta.X, delta.Y);
                SceneTransform.Matrix = matrix;
                _dragStartScreen = screen;
                OnTransformChanged();
                break;
            }

            case DragMode.Measure:
                _livePoint = ClampToImage(image);
                _hasLivePoint = true;
                RedrawMeasurements();
                UpdateReading();
                break;

            case DragMode.MovePoint:
                MoveSelectedPoint(image);
                break;

            case DragMode.MoveMeasurement:
                MoveSelected(image);
                break;

            case DragMode.MoveRing:
            case DragMode.ResizeRingInner:
            case DragMode.ResizeRingOuter:
                DragRing(_dragMode, image);
                break;

            case DragMode.DrawRoi:
                Roi = NormaliseToImage(_dragStartImage, image);
                break;

            case DragMode.MoveRoi:
            {
                var dx = (int)Math.Round(image.X - _dragStartImage.X);
                var dy = (int)Math.Round(image.Y - _dragStartImage.Y);

                // Clamp the translation rather than the result, so dragging
                // against an edge does not shrink the region.
                dx = Math.Clamp(dx, -_dragStartRoi.X, (int)_lastImageSize.Width - _dragStartRoi.Right);
                dy = Math.Clamp(dy, -_dragStartRoi.Y, (int)_lastImageSize.Height - _dragStartRoi.Bottom);

                Roi = _dragStartRoi with { X = _dragStartRoi.X + dx, Y = _dragStartRoi.Y + dy };
                break;
            }

            case DragMode.ResizeRoi:
            {
                if (RoiAngle == 0)
                {
                    Roi = ResizeRoi(_dragStartRoi, _activeEdge, image);
                    break;
                }

                // About the rectangle the drag began on, not the one being
                // rewritten as it goes: pivoting about a moving centre makes
                // the region wander away from the pointer.
                var start = new Rect(
                    _dragStartRoi.X, _dragStartRoi.Y, _dragStartRoi.Width, _dragStartRoi.Height);

                var turned = ResizeTurned(start, _activeEdge, RoiAngle, image);

                Roi = new DrawingRectangle(
                    (int)Math.Round(turned.X),
                    (int)Math.Round(turned.Y),
                    (int)Math.Round(turned.Width),
                    (int)Math.Round(turned.Height));

                break;
            }

            case DragMode.RotateRoi:
            {
                // The change in bearing since the grab, not the bearing
                // itself, so the knob stays under the cursor rather than
                // jumping to it when the drag begins.
                var turned = _dragStartAngle + (BearingFromCentre(image) - _dragStartBearing);

                // Whole degrees with Shift, which is most of what a part
                // lined up against a fixture actually needs.
                RoiAngle = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? Math.Round(turned / 15) * 15
                    : Math.Round(turned, 2);

                break;
            }
        }

        TrackRingPreview(image);
        UpdateReadout(image);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None)
            return;

        if (_dragMode == DragMode.Measure)
            EndMeasureDrag(e.GetPosition(Surface));

        // A click that never became a drag should not leave a zero-sized ROI.
        if (_dragMode == DragMode.DrawRoi && (Roi.Width < 2 || Roi.Height < 2))
            Roi = _dragStartRoi;

        if (_dragMode is DragMode.MovePoint or DragMode.MoveMeasurement)
            Publish();

        _dragMode = DragMode.None;
        _activeEdge = Edge.None;
        _editIndex = -1;
        Surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Keeps the half-placed band following the pointer.</summary>
    private void TrackRingPreview(Point image)
    {
        if (ActiveTool != ViewerTool.Ring || _ringClicks.Count == 0)
            return;

        _ringPreview = image;
        UpdateRingVisual();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragMode != DragMode.None)
            return;

        UpdateReadout(null);

        // Drop the stand-in point rather than leaving a line running to where
        // the cursor left the canvas.
        if (_hasLivePoint)
        {
            _hasLivePoint = false;
            RedrawMeasurements();
        }
    }

    // ---- measurement drawing --------------------------------------------

    private void RedrawMeasurements()
    {
        MeasureLayer.Children.Clear();

        foreach (var measurement in _measurements)
            Draw(measurement);

        if (_pending.Count > 0)
            Draw(new Measurement(ActiveTool, PendingWithLive(), _colour));
    }

    private void Draw(Measurement measurement)
    {
        var brush = new SolidColorBrush(measurement.Colour);
        brush.Freeze();

        // Widths are in image units, divided by the zoom so the overlay keeps
        // the same weight on screen however far in you are.
        var width = (measurement.IsSelected ? 2.6 : 1.5) * ScreenPixel;

        DrawGeometry(measurement, brush, width);

        foreach (var point in measurement.Points)
        {
            DrawMarker(point, brush, width);

            if (measurement.IsSelected)
                DrawHandle(point, brush);
        }

        // A profile line is labelled with its name, which is also the title of
        // its chart window; its length stays in the status bar. Everything else
        // is labelled with its reading, which is the whole point of it.
        var label = measurement.Tool == ViewerTool.LineProfile
            ? measurement.Name
            : measurement.Text;

        if (!string.IsNullOrEmpty(label))
            DrawLabel(label, measurement.LabelAnchor, brush, measurement.IsSelected);
    }

    /// <remarks>
    /// Draws whatever the points so far allow, so a measurement under the hand
    /// looks like the one it is about to become.
    /// </remarks>
    private void DrawGeometry(Measurement measurement, Brush brush, double width)
    {
        var points = measurement.Points;

        switch (measurement.Tool)
        {
            case ViewerTool.PointToPoint:
                if (points.Count >= 2)
                    DrawLine(points[0], points[1], brush, width);
                break;

            case ViewerTool.LineProfile:
                if (points.Count >= 2)
                {
                    DrawLine(points[0], points[1], brush, width);

                    // The band is the area actually sampled, so it is drawn
                    // rather than left to be imagined from a number.
                    var band = ThicknessFor(measurement);

                    if (band > 1)
                        DrawBand(points[0], points[1], band, brush, width);
                }
                break;

            case ViewerTool.PointToLine:
                if (points.Count >= 3)
                {
                    DrawLine(points[1], points[2], brush, width);

                    var foot = Measurement.FootOnLine(points[0], points[1], points[2]);
                    DrawExtension(points[1], points[2], foot, brush, width);
                    DrawDashed(points[0], foot, brush, width);
                }
                break;

            case ViewerTool.LineToLineParallel:
                if (points.Count >= 2)
                {
                    // The drag is the caliper; the reference lines show the two
                    // edges it was taken across. Dashing the span and leaving
                    // the references solid keeps them readable apart: the solid
                    // lines are the part, the dashed one is the reading.
                    var arm = Measurement.Distance(points[0], points[1]) * ReferenceArmRatio;

                    DrawDashed(points[0], points[1], brush, width);
                    DrawReference(points[0], points[1], arm, brush, width);
                    DrawReference(points[1], points[0], arm, brush, width);
                }
                break;

            case ViewerTool.LineToLineAngled:
                if (points.Count >= 2)
                    DrawLine(points[0], points[1], brush, width);

                if (points.Count >= 4)
                {
                    DrawLine(points[2], points[3], brush, width);

                    var (onFirst, onSecond) = Measurement.ClosestPointsBetween(
                        points[0], points[1], points[2], points[3]);

                    DrawDashed(onFirst, onSecond, brush, width);
                }
                break;

            case ViewerTool.Angle:
                if (points.Count >= 2)
                    DrawLine(points[0], points[1], brush, width);

                if (points.Count >= 3)
                {
                    DrawLine(points[0], points[2], brush, width);
                    DrawArc(points, brush, width);
                }
                break;

            case ViewerTool.CircleCentre:
                if (points.Count >= 2)
                {
                    DrawRing(points[0], Measurement.Distance(points[0], points[1]), brush, width);
                    DrawDashed(points[0], points[1], brush, width);
                }
                break;

            case ViewerTool.CircleThreePoint:
                if (points.Count >= 3 &&
                    Measurement.Circumcircle(points[0], points[1], points[2]) is { } circle)
                {
                    DrawRing(circle.Centre, circle.Radius, brush, width);
                    DrawMarker(circle.Centre, brush, width);
                }
                break;
        }
    }

    /// <summary>
    /// The edges of the averaged band, as two dashed lines either side.
    /// </summary>
    private void DrawBand(Point a, Point b, int thickness, Brush brush, double width)
    {
        var normal = Measurement.Normal(a, b);
        if (normal.Length < 1e-9)
            return;

        var half = (thickness - 1) / 2.0;

        foreach (var side in new[] { -half, half })
        {
            DrawDashed(
                new Point(a.X + (normal.X * side), a.Y + (normal.Y * side)),
                new Point(b.X + (normal.X * side), b.Y + (normal.Y * side)),
                brush,
                width * 0.7);
        }
    }

    private void DrawMarker(Point at, Brush brush, double width)
    {
        var arm = 5 * ScreenPixel;

        MeasureLayer.Children.Add(NewLine(
            new Point(at.X - arm, at.Y), new Point(at.X + arm, at.Y), brush, width));
        MeasureLayer.Children.Add(NewLine(
            new Point(at.X, at.Y - arm), new Point(at.X, at.Y + arm), brush, width));
    }

    /// <summary>A hollow square on each point of the selected measurement.</summary>
    private void DrawHandle(Point at, Brush brush)
    {
        var size = 7 * ScreenPixel;

        var handle = new Rectangle
        {
            Width = size,
            Height = size,
            Stroke = brush,
            StrokeThickness = ScreenPixel,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(handle, at.X - (size / 2));
        Canvas.SetTop(handle, at.Y - (size / 2));
        MeasureLayer.Children.Add(handle);
    }

    private void DrawLine(Point a, Point b, Brush brush, double width) =>
        MeasureLayer.Children.Add(NewLine(a, b, brush, width));

    private void DrawDashed(Point a, Point b, Brush brush, double width)
    {
        var dashed = NewLine(a, b, brush, width);
        dashed.StrokeDashArray = new DoubleCollection { 4, 3 };
        MeasureLayer.Children.Add(dashed);
    }

    /// <summary>
    /// A short line through <paramref name="at"/>, perpendicular to the span -
    /// the edge a caliper reading was taken against.
    /// </summary>
    /// <remarks>
    /// <paramref name="arm"/> is in image units and proportional to the span,
    /// not a fixed number of screen pixels. These lines stand for the two edges
    /// of the part, so they belong to the image and have to zoom with it; the
    /// crosshairs and handles are chrome, and those stay a constant size.
    /// </remarks>
    private void DrawReference(Point at, Point other, double arm, Brush brush, double width)
    {
        var normal = Measurement.Normal(at, other);
        if (normal.Length < 1e-9 || arm <= 0)
            return;

        DrawLine(
            new Point(at.X - (normal.X * arm), at.Y - (normal.Y * arm)),
            new Point(at.X + (normal.X * arm), at.Y + (normal.Y * arm)),
            brush,
            width);
    }

    /// <summary>
    /// Extends a line dashed to meet a perpendicular whose foot falls outside
    /// the drawn span, so the reading visibly lands on the line it measures to.
    /// </summary>
    private void DrawExtension(Point a, Point b, Point foot, Brush brush, double width)
    {
        var line = b - a;
        if (line.LengthSquared < 1e-18)
            return;

        var t = (((foot.X - a.X) * line.X) + ((foot.Y - a.Y) * line.Y)) / line.LengthSquared;

        if (t < 0)
            DrawDashed(a, foot, brush, width);
        else if (t > 1)
            DrawDashed(b, foot, brush, width);
    }

    private void DrawRing(Point centre, double radius, Brush brush, double width)
    {
        if (radius <= 0)
            return;

        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = brush,
            StrokeThickness = width,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ring, centre.X - radius);
        Canvas.SetTop(ring, centre.Y - radius);
        MeasureLayer.Children.Add(ring);
    }

    /// <summary>The arc between the two arms, which is what the number refers to.</summary>
    private void DrawArc(IReadOnlyList<Point> points, Brush brush, double width)
    {
        var vertex = points[0];
        var radius = Math.Min(
            Measurement.Distance(vertex, points[1]),
            Measurement.Distance(vertex, points[2])) * 0.35;

        if (radius < 1e-6)
            return;

        var start = AlongRay(vertex, points[1], radius);
        var end = AlongRay(vertex, points[2], radius);

        // The sign of the cross product says which way round the short arc goes.
        var cross = ((points[1].X - vertex.X) * (points[2].Y - vertex.Y)) -
                    ((points[1].Y - vertex.Y) * (points[2].X - vertex.X));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0,
            false,
            cross > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
            true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        MeasureLayer.Children.Add(new Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = width,
            IsHitTestVisible = false,
        });
    }

    private static Point AlongRay(Point from, Point towards, double distance)
    {
        var direction = towards - from;
        if (direction.Length < 1e-9)
            return from;

        direction.Normalize();
        return new Point(from.X + (direction.X * distance), from.Y + (direction.Y * distance));
    }

    /// <summary>
    /// The reading, drawn beside the geometry it belongs to.
    /// </summary>
    /// <remarks>
    /// Counter-scaled by the zoom so the text stays legible rather than growing
    /// with the image, which is why it is placed by transform rather than by
    /// font size.
    /// </remarks>
    private void DrawLabel(string text, Point anchor, Brush brush, bool selected)
    {
        var pixel = ScreenPixel;

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x17, 0x18, 0x1A)),
            BorderBrush = brush,
            BorderThickness = new Thickness(selected ? 1.4 : 0.8),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            RenderTransform = new ScaleTransform(pixel, pixel),
            RenderTransformOrigin = new Point(0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
            },
        };

        // Offset in screen pixels, converted to image units: the label sits
        // clear of the line without drifting as the zoom changes.
        Canvas.SetLeft(label, anchor.X + (8 * pixel));
        Canvas.SetTop(label, anchor.Y - (20 * pixel));
        MeasureLayer.Children.Add(label);
    }

    private static Line NewLine(Point a, Point b, Brush brush, double width) => new()
    {
        X1 = a.X,
        Y1 = a.Y,
        X2 = b.X,
        Y2 = b.Y,
        Stroke = brush,
        StrokeThickness = width,
        IsHitTestVisible = false,
    };

    // ---- ROI geometry ----------------------------------------------------

    private Point ClampToImage(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, _lastImageSize.Width)),
        Math.Clamp(p.Y, 0, Math.Max(0, _lastImageSize.Height)));

    private DrawingRectangle NormaliseToImage(Point a, Point b)
    {
        a = ClampToImage(a);
        b = ClampToImage(b);

        var x = (int)Math.Round(Math.Min(a.X, b.X));
        var y = (int)Math.Round(Math.Min(a.Y, b.Y));
        var w = (int)Math.Round(Math.Abs(a.X - b.X));
        var h = (int)Math.Round(Math.Abs(a.Y - b.Y));

        return new DrawingRectangle(x, y, w, h);
    }

    private DrawingRectangle ResizeRoi(DrawingRectangle start, Edge edge, Point image)
    {
        var clamped = ClampToImage(image);

        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        if (edge.HasFlag(Edge.Left))
            left = (int)Math.Round(clamped.X);
        if (edge.HasFlag(Edge.Right))
            right = (int)Math.Round(clamped.X);
        if (edge.HasFlag(Edge.Top))
            top = (int)Math.Round(clamped.Y);
        if (edge.HasFlag(Edge.Bottom))
            bottom = (int)Math.Round(clamped.Y);

        // Dragging an edge past its opposite flips the rectangle rather than
        // producing a negative extent.
        return DrawingRectangle.FromLTRB(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Max(left, right),
            Math.Max(top, bottom));
    }

    /// <summary>
    /// Classifies a screen point against the ROI. Tolerance is converted from
    /// screen pixels into image units so the grab zone stays the same physical
    /// size at any zoom.
    /// </summary>
    private Edge HitTestRoi(Point screen)
    {
        var roi = Roi;
        if (roi.Width <= 0 || roi.Height <= 0)
            return Edge.None;

        var tolerance = EdgeTolerance * ScreenPixel;

        // Turned into the region's own frame first, so everything below is
        // the same arithmetic it always was - the region is upright in here
        // however far it has been rotated out there.
        var image = ToRoiFrame(ScreenToImage(screen));

        if (IsOnRotateKnob(image, roi, tolerance))
            return Edge.Rotate;

        var nearLeft = Math.Abs(image.X - roi.Left) <= tolerance;
        var nearRight = Math.Abs(image.X - roi.Right) <= tolerance;
        var nearTop = Math.Abs(image.Y - roi.Top) <= tolerance;
        var nearBottom = Math.Abs(image.Y - roi.Bottom) <= tolerance;

        var withinX = image.X >= roi.Left - tolerance && image.X <= roi.Right + tolerance;
        var withinY = image.Y >= roi.Top - tolerance && image.Y <= roi.Bottom + tolerance;

        var edge = Edge.None;
        if (nearLeft && withinY)
            edge |= Edge.Left;
        if (nearRight && withinY)
            edge |= Edge.Right;
        if (nearTop && withinX)
            edge |= Edge.Top;
        if (nearBottom && withinX)
            edge |= Edge.Bottom;

        if (edge != Edge.None)
            return edge;

        var inside = image.X > roi.Left && image.X < roi.Right &&
                     image.Y > roi.Top && image.Y < roi.Bottom;

        return inside ? Edge.Inside : Edge.None;
    }

    /// <summary>The direction from the region's centre to a point, in degrees.</summary>
    private double BearingFromCentre(Point image)
    {
        var roi = Roi;
        var centre = new Point(roi.X + (roi.Width / 2.0), roi.Y + (roi.Height / 2.0));

        return Math.Atan2(image.Y - centre.Y, image.X - centre.X) * 180.0 / Math.PI;
    }

    private void UpdateCursor(Point screen)
    {
        if (HitTestHandle(screen) >= 0)
        {
            Surface.Cursor = Cursors.SizeAll;
            return;
        }

        // The band's own circles are its handles, so the cursor is the only
        // thing that says they can be grabbed.
        switch (HitTestRing(screen))
        {
            case RingPart.Inner:
            case RingPart.Outer:
                Surface.Cursor = Cursors.SizeNWSE;
                return;

            case RingPart.Inside:
                Surface.Cursor = Cursors.SizeAll;
                return;
        }

        if (!IsRoiEnabled)
        {
            // With no region to edit, the only thing under the cursor worth
            // reacting to is a measurement.
            Surface.Cursor = PickMeasurement(screen) is null ? Cursors.Arrow : Cursors.Hand;
            return;
        }

        Surface.Cursor = HitTestRoi(screen) switch
        {
            Edge.Rotate => Cursors.Hand,
            Edge.Left or Edge.Right => Cursors.SizeWE,
            Edge.Top or Edge.Bottom => Cursors.SizeNS,
            Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
            Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
            Edge.Inside => Cursors.SizeAll,
            _ => PickMeasurement(screen) is null ? Cursors.Cross : Cursors.Hand,
        };
    }

    /// <summary>How far above the region the turning knob sits, in image units.</summary>
    private double RotateKnobReach => 22 * ScreenPixel;

    /// <summary>
    /// A point in image coordinates, expressed in the region's own frame.
    /// </summary>
    private Point ToRoiFrame(Point image) => Turn(image, CentreOf(Roi), -RoiAngle);

    /// <summary>Rotates <paramref name="point"/> about <paramref name="centre"/>.</summary>
    private static Point Turn(Point point, Point centre, double degrees)
    {
        if (degrees == 0)
            return point;

        var radians = degrees * Math.PI / 180.0;
        var dx = point.X - centre.X;
        var dy = point.Y - centre.Y;

        return new Point(
            centre.X + (dx * Math.Cos(radians)) - (dy * Math.Sin(radians)),
            centre.Y + (dx * Math.Sin(radians)) + (dy * Math.Cos(radians)));
    }

    private static Point CentreOf(DrawingRectangle rect) =>
        new(rect.X + (rect.Width / 2.0), rect.Y + (rect.Height / 2.0));

    private static Point CentreOf(Rect rect) =>
        new(rect.X + (rect.Width / 2.0), rect.Y + (rect.Height / 2.0));

    /// <summary>
    /// Resizes a turned region by dragging one of its edges or corners.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The edge being dragged is an edge of the region, not of the screen,
    /// so the pointer has to be read in the region's own frame. That part
    /// alone is not enough: resizing moves the centre, and the centre is
    /// what the region turns about - so a displacement worked out in the
    /// turned frame has to be turned back before it is applied. Without
    /// that the edge nobody touched slides away as the opposite one is
    /// dragged, which is what dragging a corner of a rotated region looked
    /// like.
    /// </para>
    /// <para>
    /// Static and taking everything it needs, because this is the part
    /// worth testing: that the far edge stays exactly where it was, at any
    /// angle.
    /// </para>
    /// </remarks>
    internal static Rect ResizeTurned(Rect start, Edge edge, double degrees, Point pointer)
    {
        var centre = CentreOf(start);
        var local = Turn(pointer, centre, -degrees);

        var left = edge.HasFlag(Edge.Left) ? local.X : start.Left;
        var right = edge.HasFlag(Edge.Right) ? local.X : start.Right;
        var top = edge.HasFlag(Edge.Top) ? local.Y : start.Top;
        var bottom = edge.HasFlag(Edge.Bottom) ? local.Y : start.Bottom;

        // Dragging an edge past its opposite flips the region rather than
        // producing a negative extent.
        var resized = new Rect(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top));

        // The new centre was worked out in the turned frame; put it back.
        var moved = Turn(CentreOf(resized), centre, degrees);

        return new Rect(
            moved.X - (resized.Width / 2),
            moved.Y - (resized.Height / 2),
            resized.Width,
            resized.Height);
    }

    /// <summary>Whether a point in the region's frame is on the turning knob.</summary>
    private bool IsOnRotateKnob(Point inFrame, DrawingRectangle roi, double tolerance)
    {
        var knob = new Point(roi.X + (roi.Width / 2.0), roi.Y - RotateKnobReach);

        return (inFrame - knob).Length <= Math.Max(tolerance, 6 * ScreenPixel);
    }

    private void UpdateRoiVisual()
    {
        var roi = Roi;
        var visible = IsRoiEnabled && roi.Width > 0 && roi.Height > 0;

        RoiShape.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HandleLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
            return;

        Canvas.SetLeft(RoiShape, roi.X);
        Canvas.SetTop(RoiShape, roi.Y);
        RoiShape.Width = roi.Width;
        RoiShape.Height = roi.Height;

        // Keep the outline one screen pixel wide regardless of zoom.
        RoiShape.StrokeThickness = ScreenPixel;

        // Turned about its own centre, so the rectangle held in Roi stays the
        // upright one and nothing else has to know about the angle.
        RoiShape.RenderTransformOrigin = new Point(0.5, 0.5);
        RoiShape.RenderTransform = RoiAngle == 0
            ? Transform.Identity
            : new RotateTransform(RoiAngle);

        DrawCornerMarks(roi);
        DrawRotateKnob(roi);
    }

    /// <summary>
    /// The knob that turns the region, on a stalk above its top edge.
    /// </summary>
    /// <remarks>
    /// Outside the outline rather than on a corner, because every corner is
    /// already a resize grip - a turn and a stretch started from the same
    /// pixel would be a coin toss.
    /// </remarks>
    private void DrawRotateKnob(DrawingRectangle roi)
    {
        var radius = 4 * ScreenPixel;
        var centre = new Point(roi.X + (roi.Width / 2.0), roi.Y + (roi.Height / 2.0));
        var knob = RotateByAngle(new Point(centre.X, roi.Y - RotateKnobReach), centre);
        var top = RotateByAngle(new Point(centre.X, roi.Y), centre);

        var stalk = new Line
        {
            X1 = top.X,
            Y1 = top.Y,
            X2 = knob.X,
            Y2 = knob.Y,
            Stroke = RoiShape.Stroke,
            StrokeThickness = ScreenPixel,
            IsHitTestVisible = false,
        };

        var grip = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = RoiShape.Stroke,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(grip, knob.X - radius);
        Canvas.SetTop(grip, knob.Y - radius);

        HandleLayer.Children.Add(stalk);
        HandleLayer.Children.Add(grip);
    }

    /// <summary>A point in the region's frame, put back into image coordinates.</summary>
    private Point RotateByAngle(Point inFrame, Point centre)
    {
        if (RoiAngle == 0)
            return inFrame;

        var radians = RoiAngle * Math.PI / 180.0;
        var dx = inFrame.X - centre.X;
        var dy = inFrame.Y - centre.Y;

        return new Point(
            centre.X + (dx * Math.Cos(radians)) - (dy * Math.Sin(radians)),
            centre.Y + (dx * Math.Sin(radians)) + (dy * Math.Cos(radians)));
    }

    /// <summary>
    /// Small corner marks for affordance. Sized in image units from the current
    /// scale so they read as a constant size on screen.
    /// </summary>
    private void DrawCornerMarks(DrawingRectangle roi)
    {
        HandleLayer.Children.Clear();

        var size = 6 * ScreenPixel;
        var corners = new[]
        {
            new Point(roi.Left, roi.Top),
            new Point(roi.Right, roi.Top),
            new Point(roi.Left, roi.Bottom),
            new Point(roi.Right, roi.Bottom),
        };

        var centre = new Point(roi.X + (roi.Width / 2.0), roi.Y + (roi.Height / 2.0));

        foreach (var upright in corners)
        {
            // Turned with the region, or the grips would sit away from the
            // corners they resize as soon as it is rotated.
            var corner = RotateByAngle(upright, centre);

            var mark = new Rectangle
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                StrokeThickness = ScreenPixel,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(mark, corner.X - (size / 2));
            Canvas.SetTop(mark, corner.Y - (size / 2));
            HandleLayer.Children.Add(mark);
        }
    }

    // ---- readout ---------------------------------------------------------

    private void UpdateReadout(Point? image)
    {
        if (ImageSource is not { } source || image is not { } point)
        {
            PositionText.Text = string.Empty;
            PixelText.Text = string.Empty;
            return;
        }

        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);

        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight)
        {
            PositionText.Text = string.Empty;
            PixelText.Text = string.Empty;
            return;
        }

        PositionText.Text = $"X {x}  Y {y}";
        PixelText.Text = SamplePixel(source, x, y) ?? string.Empty;
    }

    /// <summary>Reads one pixel out of the displayed bitmap.</summary>
    private static string? SamplePixel(BitmapSource source, int x, int y)
    {
        try
        {
            var bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8;
            var buffer = new byte[bytesPerPixel];
            source.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, bytesPerPixel, 0);

            if (source.Format == PixelFormats.Gray8)
                return $"I {buffer[0]}";

            if (source.Format == PixelFormats.Bgr24)
                return $"R {buffer[2]}  G {buffer[1]}  B {buffer[0]}";

            if (source.Format == PixelFormats.Bgra32)
                return $"R {buffer[2]}  G {buffer[1]}  B {buffer[0]}  A {buffer[3]}";

            return string.Join(' ', buffer);
        }
        catch (Exception)
        {
            // Sampling is a readout nicety; a format we cannot decode should
            // not interrupt panning.
            return null;
        }
    }
}
