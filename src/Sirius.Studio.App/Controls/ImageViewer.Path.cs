using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// Drawing and editing a search path - Find Curve's band along an edge that
/// is neither a line nor a circle.
/// </summary>
/// <remarks>
/// <para>
/// Placed corner by corner with clicks, as anybody would trace a shape: a
/// double-click or Enter ends an open path, a click back on the first
/// corner closes it, Backspace takes the last corner back and Escape
/// abandons it. The band's width is kept from the path being replaced, so
/// redrawing the line does not undo a width chosen for it.
/// </para>
/// <para>
/// Once placed it is edited where it lies, with no tool picked: a corner is
/// dragged by its square, the whole path by the band, and the band's width
/// by its edge - the same grips as the ring's, where the shape's own
/// outline is its handle.
/// </para>
/// </remarks>
public partial class ImageViewer
{
    /// <summary>
    /// The search path on show, bound to whichever node owns it. Two-way:
    /// drawn here, kept by the node.
    /// </summary>
    public static readonly DependencyProperty GuidePathProperty = DependencyProperty.Register(
        nameof(GuidePath),
        typeof(PathBand),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnGuidePathChanged));

    /// <summary>Whether a path may be drawn and is worth showing.</summary>
    public static readonly DependencyProperty IsPathEnabledProperty = DependencyProperty.Register(
        nameof(IsPathEnabled),
        typeof(bool),
        typeof(ImageViewer),
        new FrameworkPropertyMetadata(false, OnGuidePathChanged));

    public PathBand? GuidePath
    {
        get => (PathBand?)GetValue(GuidePathProperty);
        set => SetValue(GuidePathProperty, value);
    }

    public bool IsPathEnabled
    {
        get => (bool)GetValue(IsPathEnabledProperty);
        set => SetValue(IsPathEnabledProperty, value);
    }

    /// <summary>What part of the path a point is over.</summary>
    internal enum PathPart
    {
        None,
        Corner,
        Edge,
        Inside,
    }

    /// <summary>Corners placed so far for a path being drawn.</summary>
    private readonly List<Point> _pathClicks = new();

    /// <summary>Where the pointer is while a path is half-placed.</summary>
    private Point? _pathPreview;

    /// <summary>The path as it was when the current drag began.</summary>
    private PathBand? _dragStartPath;

    /// <summary>Which corner is being dragged.</summary>
    private int _pathCorner = -1;

    private static void OnGuidePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ImageViewer)d;

        viewer.UpdatePathVisual();
        viewer.SyncPathTool();
    }

    /// <summary>
    /// Advances the path gesture with a click: a new corner, or the end of
    /// the path on a double-click or on the first corner again.
    /// </summary>
    private void AddPathClick(Point image, int clickCount)
    {
        // The double-click's first press already placed this corner.
        if (clickCount >= 2)
        {
            FinishPath(closed: false);
            return;
        }

        if (_pathClicks.Count >= 3 && Distance(image, _pathClicks[0]) <= Math.Max(HandleTolerance * ScreenPixel, 3))
        {
            FinishPath(closed: true);
            return;
        }

        _pathClicks.Add(image);
        UpdatePathVisual();
        UpdateHint();
    }

    /// <summary>Ends the gesture, writing the path if it has two corners apart.</summary>
    private void FinishPath(bool closed)
    {
        var corners = _pathClicks
            .Select(point => new Point2(point.X, point.Y))
            .Where((point, i) => i == 0 || point.DistanceTo(new Point2(_pathClicks[i - 1].X, _pathClicks[i - 1].Y)) > 1e-6)
            .ToArray();

        _pathClicks.Clear();
        _pathPreview = null;

        if (corners.Length >= 2)
            GuidePath = new PathBand(corners, closed, GuidePath?.Width ?? PathBand.Default.Width);

        UpdatePathVisual();
        UpdateHint();
    }

    /// <summary>Takes back the last corner placed.</summary>
    private void UndoPathClick()
    {
        _pathClicks.RemoveAt(_pathClicks.Count - 1);
        UpdatePathVisual();
        UpdateHint();
    }

    /// <summary>Abandons a half-placed path.</summary>
    private void ResetPath()
    {
        if (_pathClicks.Count == 0 && _pathPreview is null)
            return;

        _pathClicks.Clear();
        _pathPreview = null;
        UpdatePathVisual();
        UpdateHint();
    }

    /// <summary>Keeps the half-placed path following the pointer.</summary>
    private void TrackPathPreview(Point image)
    {
        if (ActiveTool != ViewerTool.Path || _pathClicks.Count == 0)
            return;

        _pathPreview = image;
        UpdatePathVisual();
    }

    private string PathHint() => _pathClicks.Count switch
    {
        0 => "Click the first corner of the path along the edge",
        1 => "Click the next corner",
        2 => "Click the next corner, or double-click to finish",
        _ => "Click the next corner, double-click to finish, or click the first corner to close it",
    };

    /// <summary>Offers the path tool only where there is a path to write, as the band tool does.</summary>
    private void SyncPathTool()
    {
        if (!_templateReady)
            return;

        PathToolButton.Visibility = IsPathEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (IsPathEnabled || ActiveTool != ViewerTool.Path)
            return;

        ResetPath();

        foreach (ToggleButton button in ToolButtons())
            button.IsChecked = (button.Tag as string) == nameof(ViewerTool.None);

        ActiveTool = ViewerTool.None;
    }

    /// <summary>Which part of the path a screen point grabs.</summary>
    private PathPart HitTestPath(Point screen) => HitTestPathAt(ScreenToImage(screen), out _);

    /// <summary>
    /// The same test in image coordinates. Corners win over the edge, and
    /// the edge over the inside, so a narrow band can still be resized.
    /// </summary>
    internal PathPart HitTestPathAt(Point image, out int corner)
    {
        corner = -1;

        if (!IsPathEnabled || _pathClicks.Count > 0 || GuidePath is not { IsUsable: true } path)
            return PathPart.None;

        var tolerance = Math.Max(HandleTolerance * ScreenPixel, 3);

        for (var i = 0; i < path.Points.Count; i++)
        {
            if (Distance(image, new Point(path.Points[i].X, path.Points[i].Y)) <= tolerance)
            {
                corner = i;
                return PathPart.Corner;
            }
        }

        var distance = DistanceToPath(path, image);

        if (Math.Abs(distance - path.HalfWidth) <= tolerance)
            return PathPart.Edge;

        return distance < path.HalfWidth ? PathPart.Inside : PathPart.None;
    }

    /// <summary>Starts dragging whatever part of the path was grabbed.</summary>
    private void BeginPathDrag(Point image)
    {
        _dragStartPath = GuidePath;

        _dragMode = HitTestPathAt(image, out _pathCorner) switch
        {
            PathPart.Corner => DragMode.MovePathCorner,
            PathPart.Edge => DragMode.ResizePath,
            _ => DragMode.MovePath,
        };
    }

    /// <summary>Applies a drag to the path.</summary>
    private void DragPath(DragMode mode, Point image)
    {
        if (_dragStartPath is not { } start)
            return;

        switch (mode)
        {
            case DragMode.MovePath:
                GuidePath = start.Moved(image.X - _dragStartImage.X, image.Y - _dragStartImage.Y);
                break;

            case DragMode.MovePathCorner when _pathCorner >= 0 && _pathCorner < start.Points.Count:
            {
                var corners = start.Points.ToArray();
                var moved = ClampToImage(image);
                corners[_pathCorner] = new Point2(moved.X, moved.Y);
                GuidePath = start.WithPoints(corners);
                break;
            }

            // The edge is half the width out from the path, wherever along
            // it the edge was grabbed.
            case DragMode.ResizePath:
                GuidePath = start.WithWidth(Math.Round(2 * DistanceToPath(start, image), 1));
                break;
        }
    }

    /// <summary>The cursor over a path, or null when the pointer is not on one.</summary>
    private Cursor? PathCursor(Point screen) => HitTestPath(screen) switch
    {
        PathPart.Corner or PathPart.Inside => Cursors.SizeAll,
        PathPart.Edge => Cursors.Cross,
        _ => null,
    };

    /// <summary>
    /// Draws the path - the one being placed if there is one, otherwise the
    /// one the node holds: the band as a broad translucent stroke along it,
    /// the line itself, a square on each corner and an arrow showing which
    /// way it runs, which is what decides the side the calipers start from.
    /// </summary>
    private void UpdatePathVisual()
    {
        if (!_templateReady)
            return;

        var placing = _pathClicks.Count > 0;
        var corners = placing
            ? _pathClicks.Concat(_pathPreview is { } pointer ? new[] { pointer } : Array.Empty<Point>()).ToList()
            : GuidePath?.Points.Select(point => new Point(point.X, point.Y)).ToList() ?? [];

        var closed = !placing && GuidePath is { Closed: true };
        var visible = IsPathEnabled && corners.Count >= (placing ? 1 : 2);

        PathBandShape.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PathLineShape.Visibility = PathBandShape.Visibility;
        PathHandleShape.Visibility = PathBandShape.Visibility;

        if (!visible)
            return;

        var line = new StreamGeometry();

        using (var context = line.Open())
        {
            context.BeginFigure(corners[0], isFilled: false, isClosed: closed);
            context.PolyLineTo(corners.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
        }

        line.Freeze();

        PathBandShape.Data = line;
        PathBandShape.StrokeThickness = GuidePath?.Width ?? PathBand.Default.Width;

        var handles = new GeometryGroup();
        var half = 3.5 * ScreenPixel;

        foreach (var corner in corners)
            handles.Children.Add(new RectangleGeometry(new Rect(corner.X - half, corner.Y - half, 2 * half, 2 * half)));

        // An arrow halfway along the first side, pointing the way the path
        // was drawn.
        if (corners.Count >= 2 && Distance(corners[0], corners[1]) > 1e-6)
        {
            var (a, b) = (corners[0], corners[1]);
            var length = Distance(a, b);
            var (ux, uy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
            var tip = new Point((a.X + b.X) / 2 + (ux * 6 * ScreenPixel), (a.Y + b.Y) / 2 + (uy * 6 * ScreenPixel));
            var size = 7 * ScreenPixel;

            var arrow = new StreamGeometry();

            using (var context = arrow.Open())
            {
                context.BeginFigure(tip, isFilled: true, isClosed: true);
                context.LineTo(new Point(tip.X - (ux * size) - (uy * size * 0.6), tip.Y - (uy * size) + (ux * size * 0.6)), true, false);
                context.LineTo(new Point(tip.X - (ux * size) + (uy * size * 0.6), tip.Y - (uy * size) - (ux * size * 0.6)), true, false);
            }

            handles.Children.Add(arrow);
        }

        PathLineShape.Data = line;
        PathLineShape.StrokeThickness = ScreenPixel;
        PathHandleShape.Data = handles;
        PathHandleShape.StrokeThickness = ScreenPixel;
    }

    /// <summary>The shortest distance from a point to a path, closing side included.</summary>
    internal static double DistanceToPath(PathBand path, Point point)
    {
        var best = double.MaxValue;
        var count = path.Points.Count;
        var sides = path.Closed ? count : count - 1;

        for (var i = 0; i < sides; i++)
        {
            var a = path.Points[i];
            var b = path.Points[(i + 1) % count];
            best = Math.Min(best, DistanceToSegment(point, new Point(a.X, a.Y), new Point(b.X, b.Y)));
        }

        return best;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var squared = (dx * dx) + (dy * dy);

        var t = squared < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / squared, 0, 1);

        return Distance(p, new Point(a.X + (t * dx), a.Y + (t * dy)));
    }
}
