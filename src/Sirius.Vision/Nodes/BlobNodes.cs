using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How the image is reduced to foreground and background.</summary>
public enum BlobSegmentation
{
    /// <summary>
    /// Pick the threshold from the image itself, by Otsu's method.
    /// </summary>
    /// <remarks>
    /// The default, because a blob tool that cannot segment is a trap: fed a
    /// grey image it finds every non-zero pixel as one region and returns the
    /// whole frame, which looks like a result.
    /// </remarks>
    Automatic,

    /// <summary>Use the threshold given below.</summary>
    Fixed,

    /// <summary>
    /// The input is already binary; take every non-zero pixel as foreground.
    /// </summary>
    None,
}

/// <summary>Which blobs are foreground.</summary>
public enum BlobPolarity
{
    /// <summary>Bright regions on a dark background, the usual binary result.</summary>
    Light,

    /// <summary>Dark regions on a bright background.</summary>
    Dark,
}

/// <summary>
/// Finds connected regions in a binary image, measures them, and keeps those
/// that pass.
/// </summary>
/// <remarks>
/// <para>
/// Holes are subtracted from their enclosing blob: a washer measures as the
/// ring, not the disc. That is why contours are retrieved with the two-level
/// hierarchy rather than as a flat list.
/// </para>
/// <para>
/// Measures are computed in cost order and only when something asks for them -
/// the convex hull behind Solidity is not built unless a solidity filter or
/// sort is set. On an image with thousands of contours that is the difference
/// between usable and not.
/// </para>
/// <para>
/// Every limit follows the same convention: <b>-1 means no limit</b>.
/// </para>
/// <para>
/// Thresholds its own input by default, so a grey image can be fed straight
/// in. Set the mode to None when a Threshold node upstream has already done
/// it. A colour image still needs a Colour Convert first.
/// </para>
/// </remarks>
[VisionNode(
    "Find Blobs",
    TypeKey = "blob.filter",
    Category = NodeCategory.Blob,
    Icon = "ph_find_blobs",
    Description = "Finds regions, measures them, and keeps those that pass the filters.")]
public sealed class BlobFilterNode : VisionNode
{
    /// <summary>
    /// Guards against a near-noise image producing tens of thousands of
    /// contours, which would stall the pass rather than fail it.
    /// </summary>
    private const int ContourLimit = 200000;

    private BlobPolarity _polarity = BlobPolarity.Light;
    private BlobSegmentation _segmentation = BlobSegmentation.Automatic;
    private int _threshold = 128;

    private ValueRange _area = ValueRange.Unbounded;
    private ValueRange _perimeter = ValueRange.Unbounded;
    private ValueRange _circularity = ValueRange.Unbounded;
    private ValueRange _solidity = ValueRange.Unbounded;
    private ValueRange _aspect = ValueRange.Unbounded;
    private ValueRange _holes = ValueRange.Unbounded;
    private bool _rejectBorder;

    private ShapeMeasure _sortBy = ShapeMeasure.Area;
    private bool _descending = true;
    private int _maxCount = -1;

    private bool _measureAll = true;
    private bool _fill = true;
    private int _thickness = 1;

    public BlobFilterNode()
    {
        ImageIn = AddInput<ImageData>("In");
        ImageOut = AddOutput<ImageData>("Out");
        CountOut = AddOutput<int>("Count");
        ShapesOut = AddOutput<ShapeSet>("Shapes");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// A mask of the blobs that passed, for image-domain work downstream -
    /// masking another image, or measuring intensity inside the regions.
    /// </summary>
    /// <remarks>
    /// Not the way to look at results. The Shapes output drawn on a viewer is
    /// that, and it carries the measurements with it.
    /// </remarks>
    public OutputPort ImageOut { get; }

    /// <summary>How many blobs passed the filter.</summary>
    public OutputPort CountOut { get; }

    /// <summary>The blobs themselves, with their measurements.</summary>
    public OutputPort ShapesOut { get; }

    [Param("Polarity",
        Group = "Detection",
        Description = "Whether blobs are the bright or the dark regions.")]
    public BlobPolarity Polarity
    {
        get => _polarity;
        set => SetParam(ref _polarity, value);
    }

    [Param("Threshold Mode",
        Group = "Detection",
        Description = "How foreground is separated from background. Automatic picks the "
                      + "level from the image; None expects an already-binary input.")]
    public BlobSegmentation Segmentation
    {
        get => _segmentation;
        set => SetParam(ref _segmentation, value);
    }

    [Param("Threshold",
        Group = "Detection",
        Min = 0,
        Max = 255,
        Description = "Grey level separating foreground from background, when the mode is Fixed.")]
    public int Threshold
    {
        get => _threshold;
        set => SetParam(ref _threshold, Math.Clamp(value, 0, 255));
    }

    // ---- filters ---------------------------------------------------------

    [Param(
        "Area",
        Group = "Size",
        Min = 0,
        Max = 10_000_000,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Logarithmic = true,
        Connectable = true,
        Description = "Blob area in pixels. Leave an end blank for no limit.")]
    public ValueRange Area
    {
        get => _area;
        set => SetParam(ref _area, value);
    }

    [Param(
        "Perimeter",
        Group = "Size",
        Min = 0,
        Max = 100_000,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Logarithmic = true,
        Description = "Outline length in pixels. Leave an end blank for no limit.")]
    public ValueRange Perimeter
    {
        get => _perimeter;
        set => SetParam(ref _perimeter, value);
    }

    [Param(
        "Circularity",
        Group = "Shape",
        Min = 0,
        Max = 1,
        Step = 0.01,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Description = "1 is a perfect circle, lower is more ragged or elongated.")]
    public ValueRange Circularity
    {
        get => _circularity;
        set => SetParam(ref _circularity, value);
    }

    [Param(
        "Solidity",
        Group = "Shape",
        Min = 0,
        Max = 1,
        Step = 0.01,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Description = "Area against its convex hull. 1 has no concavities.")]
    public ValueRange Solidity
    {
        get => _solidity;
        set => SetParam(ref _solidity, value);
    }

    [Param(
        "Aspect",
        Group = "Shape",
        Min = 1,
        Max = 20,
        Step = 0.1,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Description = "Long side over short side of the enclosing rectangle. " +
                      "1 is square; type past 20 for a hairline.")]
    public ValueRange Aspect
    {
        get => _aspect;
        set => SetParam(ref _aspect, value);
    }

    [Param(
        "Holes",
        Group = "Shape",
        Min = 0,
        Max = 20,
        Editor = ParamEditor.Range,
        AllowsNoLimit = true,
        Description = "Enclosed holes in the blob. Leave an end blank for no limit.")]
    public ValueRange Holes
    {
        get => _holes;
        set => SetParam(ref _holes, value);
    }

    [Param("Reject Touching Border",
        Group = "Position",
        Editor = ParamEditor.CheckBox,
        Description = "Drops blobs that reach the edge of the image, which are cut off and "
                      + "cannot be measured.")]
    public bool RejectTouchingBorder
    {
        get => _rejectBorder;
        set => SetParam(ref _rejectBorder, value);
    }

    // ---- ordering and limit ---------------------------------------------

    [Param("Measure Everything",
        Group = "Output",
        Editor = ParamEditor.CheckBox,
        Description = "Take every measurement, not only the ones being filtered or sorted on. "
                      + "Turn off on an image with thousands of blobs: the convex hull behind "
                      + "Solidity is the expensive one.")]
    public bool MeasureAll
    {
        get => _measureAll;
        set => SetParam(ref _measureAll, value);
    }

    [Param("Sort By",
        Group = "Output",
        Description = "Which measure orders the results. A maximum count is meaningless "
                      + "without one.")]
    public ShapeMeasure SortBy
    {
        get => _sortBy;
        set => SetParam(ref _sortBy, value);
    }

    [Param("Descending", Group = "Output", Description = "Largest first.")]
    public bool Descending
    {
        get => _descending;
        set => SetParam(ref _descending, value);
    }

    [Param("Max Count",
        Group = "Output",
        Description = "Keep at most this many, after sorting. -1 for no limit.")]
    public int MaxCount
    {
        get => _maxCount;
        set => SetParam(ref _maxCount, value);
    }

    // ---- drawing ---------------------------------------------------------

    [Param("Fill",
        Group = "Drawing",
        Description = "Fill the kept blobs rather than outline them. Filled is the useful "
                      + "form for a mask; an outline a pixel wide disappears when zoomed out.")]
    public bool Fill
    {
        get => _fill;
        set => SetParam(ref _fill, value);
    }

    [Param("Outline Thickness", Group = "Drawing", Min = 1, Max = 20)]
    public int Thickness
    {
        get => _thickness;
        set => SetParam(ref _thickness, Math.Max(1, value));
    }

    // ---- which measures are actually needed ------------------------------

    // Which measures to take. Filtering and sorting force one on; so does
    // asking for everything, which is the default - a filter range cannot be
    // chosen for a value that was never reported.
    //
    // This does not undo the staging. Cheap rejection still happens first, so
    // a blob dropped on area costs nothing further; it is the survivors that
    // get measured in full.
    private bool NeedsPerimeter =>
        _measureAll || !_perimeter.IsUnbounded || NeedsCircularity ||
        _sortBy is ShapeMeasure.Perimeter;

    private bool NeedsCircularity =>
        _measureAll || !_circularity.IsUnbounded ||
        _sortBy is ShapeMeasure.Circularity;

    private bool NeedsSolidity =>
        _measureAll || !_solidity.IsUnbounded ||
        _sortBy is ShapeMeasure.Solidity;

    private bool NeedsRotatedRect =>
        _measureAll || !_aspect.IsUnbounded ||
        _sortBy is ShapeMeasure.AspectRatio or ShapeMeasure.Width or ShapeMeasure.Length;

    private static bool Set(double limit) => limit >= 0;

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;

        if (source.Channels != 1)
        {
            throw new InvalidOperationException(
                "Blob analysis needs a single-channel binary image. " +
                "Put a Threshold or Colour Range ahead of it.");
        }

        using var binary = new Mat();
        Segment(source, binary);

        var blobs = Measure(binary, source, context);

        blobs = Order(blobs);

        var found = blobs.Count;
        if (_maxCount >= 0 && blobs.Count > _maxCount)
            blobs = blobs.Take(_maxCount).ToList();

        if (found > blobs.Count)
            context.Warn($"Found {found:N0} blobs, kept {blobs.Count:N0} after the maximum count.");
        else if (blobs.Count == 0)
            context.Warn("No blobs matched the filters.");

        context.Set(ImageOut, ImageData.Adopt(Draw(source, blobs)));
        context.Set(CountOut, blobs.Count);
        context.Set(
            ShapesOut,
            new ShapeSet(blobs.Select(blob => blob.Shape).ToList(), source.Width, source.Height));
    }

    /// <summary>
    /// Reduces the input to a binary image, foreground white.
    /// </summary>
    /// <remarks>
    /// Thresholding belongs in the blob tool, as it does in Cognex VisionPro.
    /// Leaving it out means a grey image produces one region covering the
    /// whole frame - a wrong answer that looks like a right one, because an
    /// outline is drawn and a count is reported.
    /// </remarks>
    private void Segment(ImageData source, Mat destination)
    {
        if (_segmentation == BlobSegmentation.None)
        {
            // Already binary: every non-zero pixel is foreground, and dark
            // polarity is the same problem upside down.
            if (_polarity == BlobPolarity.Dark)
                CvInvoke.BitwiseNot(source.Mat, destination);
            else
                source.Mat.CopyTo(destination);

            return;
        }

        var direction = _polarity == BlobPolarity.Light
            ? ThresholdType.Binary
            : ThresholdType.BinaryInv;

        if (_segmentation == BlobSegmentation.Fixed)
        {
            CvInvoke.Threshold(source.Mat, destination, _threshold, 255, direction);
            return;
        }

        if (source.Depth != DepthType.Cv8U)
        {
            throw new InvalidOperationException(
                "An automatic threshold needs an 8-bit image. Use a fixed threshold, or "
                + "Normalise the image first.");
        }

        // Otsu ignores the level it is given and picks its own.
        CvInvoke.Threshold(source.Mat, destination, 0, 255, direction | ThresholdType.Otsu);
    }

    /// <summary>
    /// Walks the contours, measuring each only as far as the filters require.
    /// </summary>
    private List<Blob> Measure(Mat binary, ImageData source, EvalContext context)
    {
        var kept = new List<Blob>();

        using var contours = new VectorOfVectorOfPoint();
        using var hierarchy = new Mat();

        // Ccomp gives two levels: outer boundaries and the holes inside
        // them, which is what makes hole subtraction possible.
        CvInvoke.FindContours(
            binary,
            contours,
            hierarchy,
            RetrType.Ccomp,
            ChainApproxMethod.ChainApproxSimple);

        if (contours.Size > ContourLimit)
        {
            throw new InvalidOperationException(
                $"The image produced {contours.Size:N0} contours, past the limit of " +
                $"{ContourLimit:N0}. Threshold or filter the image further before blob analysis.");
        }

        var relations = hierarchy.GetData();

        for (var i = 0; i < contours.Size; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Skip holes; they are accounted for by their parent.
            if (GetRelation(relations, i, Parent) != -1)
                continue;

            var contour = contours[i];

            // 1. Area, with holes subtracted. Free, and rejects most of a
            //    noisy image before anything expensive is touched.
            var area = CvInvoke.ContourArea(contour);
            if (area <= 0)
                continue;

            var holes = 0;
            for (var hole = GetRelation(relations, i, FirstChild);
                 hole != -1;
                 hole = GetRelation(relations, hole, Next))
            {
                area -= CvInvoke.ContourArea(contours[hole]);
                holes++;
            }

            if (!_area.Contains(area))
                continue;

            if (!_holes.Contains(holes))
                continue;

            // 2. Bounding box: cheap, and settles the border question.
            var box = CvInvoke.BoundingRectangle(contour);
            var touchesBorder =
                box.Left <= 0 || box.Top <= 0 ||
                box.Right >= source.Width || box.Bottom >= source.Height;

            if (_rejectBorder && touchesBorder)
                continue;

            var metrics = new Dictionary<string, double>
            {
                [ShapeMetric.Area] = area,
                [ShapeMetric.BoundsX] = box.X,
                [ShapeMetric.BoundsY] = box.Y,
                [ShapeMetric.BoundsWidth] = box.Width,
                [ShapeMetric.BoundsHeight] = box.Height,
                [ShapeMetric.Extent] = box.Width * box.Height > 0
                    ? area / (box.Width * (double)box.Height)
                    : 0,
                [ShapeMetric.HoleCount] = holes,
                [ShapeMetric.TouchesBorder] = touchesBorder ? 1 : 0,
            };

            // 3. Perimeter, and circularity from it.
            if (NeedsPerimeter)
            {
                var perimeter = CvInvoke.ArcLength(contour, true);
                metrics[ShapeMetric.Perimeter] = perimeter;

                if (!_perimeter.Contains(perimeter))
                    continue;

                if (NeedsCircularity)
                {
                    var circularity = perimeter > 0
                        ? 4 * Math.PI * area / (perimeter * perimeter)
                        : 0;

                    metrics[ShapeMetric.Circularity] = circularity;

                    if (!_circularity.Contains(circularity))
                        continue;
                }
            }

            // 4. Minimum-area rectangle, for an aspect ratio that does not
            //    depend on how the part happens to be rotated.
            if (NeedsRotatedRect)
            {
                var rect = CvInvoke.MinAreaRect(contour);
                var shortSide = Math.Min(rect.Size.Width, rect.Size.Height);
                var longSide = Math.Max(rect.Size.Width, rect.Size.Height);
                var aspect = shortSide > 0 ? longSide / shortSide : 0;

                metrics[ShapeMetric.Width] = shortSide;
                metrics[ShapeMetric.Length] = longSide;
                metrics[ShapeMetric.Angle] = rect.Angle;
                metrics[ShapeMetric.AspectRatio] = aspect;

                if (!_aspect.Contains(aspect))
                    continue;
            }

            // 5. Convex hull last: the only genuinely expensive one.
            if (NeedsSolidity)
            {
                using var hull = new VectorOfPoint();
                CvInvoke.ConvexHull(contour, hull);

                var hullArea = CvInvoke.ContourArea(hull);
                var solidity = hullArea > 0 ? area / hullArea : 0;

                metrics[ShapeMetric.Solidity] = solidity;

                if (!_solidity.Contains(solidity))
                    continue;
            }

            var centroid = Centroid(contour, box);
            metrics[ShapeMetric.CentroidX] = centroid.X;
            metrics[ShapeMetric.CentroidY] = centroid.Y;

            var outline = new Point2[contour.Size];
            for (var p = 0; p < contour.Size; p++)
                outline[p] = new Point2(contour[p].X, contour[p].Y);

            kept.Add(new Blob(
                i,
                new PolygonShape(outline) { Metrics = metrics },
                metrics,
                centroid));
        }

        return kept;
    }

    /// <summary>
    /// Centre of mass, falling back to the bounding box for a degenerate
    /// contour whose moments are all zero.
    /// </summary>
    private static Point2 Centroid(VectorOfPoint contour, System.Drawing.Rectangle box)
    {
        using var moments = CvInvoke.Moments(contour);

        if (Math.Abs(moments.M00) < 1e-9)
            return new Point2(box.X + (box.Width / 2.0), box.Y + (box.Height / 2.0));

        return new Point2(moments.M10 / moments.M00, moments.M01 / moments.M00);
    }

    /// <summary>
    /// Orders the blobs, so that a maximum count keeps a defined set.
    /// </summary>
    /// <remarks>
    /// Ties break on position, so two blobs of equal area do not swap places
    /// between runs - an inspection that matches blob <i>n</i> to feature
    /// <i>n</i> depends on the order being stable.
    /// </remarks>
    private List<Blob> Order(List<Blob> blobs)
    {
        if (_sortBy == ShapeMeasure.Position)
            return blobs.OrderBy(ReadingOrder).ToList();

        var key = ShapeMeasures.MetricFor(_sortBy) ?? ShapeMetric.Area;

        var ordered = _descending
            ? blobs.OrderByDescending(blob => blob.Metrics.GetValueOrDefault(key))
            : blobs.OrderBy(blob => blob.Metrics.GetValueOrDefault(key));

        return ordered.ThenBy(ReadingOrder).ToList();
    }

    /// <summary>
    /// Top to bottom, then left to right, with a tolerance so that blobs in
    /// the same row are not reordered by a pixel of vertical jitter.
    /// </summary>
    private static (long Row, double Column) ReadingOrder(Blob blob) =>
        ((long)(blob.Centroid.Y / 16), blob.Centroid.X);

    private Mat Draw(ImageData source, List<Blob> blobs)
    {
        var destination = new Mat(source.Mat.Rows, source.Mat.Cols, DepthType.Cv8U, 1);

        try
        {
            destination.SetTo(new MCvScalar(0));

            foreach (var blob in blobs)
            {
                using var one = new VectorOfVectorOfPoint();
                using var points = new VectorOfPoint(
                    ((PolygonShape)blob.Shape).Points
                        .Select(p => new System.Drawing.Point((int)p.X, (int)p.Y))
                        .ToArray());

                one.Push(points);
                CvInvoke.DrawContours(destination, one, 0, new MCvScalar(255), _fill ? -1 : _thickness);
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    // Hierarchy columns, as OpenCV orders them.
    private const int Next = 0;
    private const int FirstChild = 2;
    private const int Parent = 3;

    private static int GetRelation(Array relations, int index, int column) =>
        (int)relations.GetValue(0, index, column)!;

    /// <summary>One measured blob, carried from detection through to output.</summary>
    private sealed record Blob(
        int ContourIndex,
        Shape Shape,
        IReadOnlyDictionary<string, double> Metrics,
        Point2 Centroid);
}
