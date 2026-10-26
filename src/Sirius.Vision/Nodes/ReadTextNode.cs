using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.OCR;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What a Read Text node expects to be looking at.</summary>
/// <remarks>
/// A curated few of Tesseract's page segmentation modes. The full list
/// includes orientation detection and column analysis, which are for
/// scanned documents; what is in front of a machine vision camera is a
/// line, a word, a character, or a few of them scattered about.
/// </remarks>
public enum TextLayout
{
    /// <summary>
    /// One line of text. The usual case for a printed label, and the
    /// right choice even when that line is a single word.
    /// </summary>
    /// <remarks>
    /// Tesseract has a single-word mode as well, and it is not offered
    /// here: against this engine it reads HELLO as "an" and 80108 as
    /// "on", at a tenth of the confidence a line gets on the same
    /// picture. A setting that is always worse is worse than no setting.
    /// </remarks>
    Line,

    /// <summary>A single character.</summary>
    Character,

    /// <summary>Several lines making up a block.</summary>
    Block,

    /// <summary>Text scattered about with no particular order.</summary>
    Scattered,
}

/// <summary>
/// Reads printed text inside a region.
/// </summary>
/// <remarks>
/// <para>
/// Tesseract, which is already compiled into the OpenCV build this
/// product uses - no new native dependency, only the language data, which
/// ships beside the executable.
/// </para>
/// <para>
/// Region-based like Read Code and Find Line, and for the same reasons: a
/// label sits in a known part of a fixtured image, searching only there is
/// faster, and whatever else is in frame cannot be read by mistake. The
/// crop is straightened before reading, so text on a part lying at an
/// angle is read as if it were square to the camera.
/// </para>
/// <para>
/// OCR on a machine is not OCR on a page. The useful settings here are
/// the ones that narrow the problem - how much text to expect, which
/// characters are possible, how small is too faint to trust - because a
/// part number of known shape read against a known alphabet is a far
/// easier question than "what does this say".
/// </para>
/// </remarks>
[VisionNode(
    "Read Text",
    TypeKey = "read.text",
    Category = NodeCategory.TextAndCodeReading,
    Icon = "ph_read_text",
    Description = "Reads printed text inside a region.")]
public sealed class ReadTextNode : VisionNode
{
    /// <summary>Green, as a found feature is drawn.</summary>
    private const uint Found = 0xFF22C55E;

    /// <summary>
    /// Tesseract wants capitals around thirty pixels tall; below that it
    /// starts guessing. A region drawn round a small label is magnified
    /// to meet it rather than the operator being told to zoom the camera.
    ///
    /// Twice that, because what is measured here is the region and what
    /// matters is the lettering inside it - a 40-pixel region around
    /// 14-pixel capitals read "SMALL." until it was doubled.
    /// </summary>
    private const int ComfortableHeight = 64;

    private OrientedRect _region = new(320, 240, 400, 120, 0);
    private TextLayout _layout = TextLayout.Line;
    private string _allowed = string.Empty;
    private double _minimumConfidence = 60;
    private bool _showText = true;

    /// <summary>
    /// One engine for every Read Text node, started once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starting one costs about 140 ms against 35 ms to read with it, so
    /// it has to outlive an evaluation - a recipe re-runs on every
    /// parameter change. Shared rather than one per node because a node
    /// has no disposal hook to release its own: the engine holds native
    /// memory and a 4 MB model, and a recipe with three readers in it
    /// would hold three of each until a finalizer got round to them.
    /// </para>
    /// <para>
    /// Guarded because the scheduler evaluates on worker threads and
    /// Tesseract cannot be used from two at once. Sharing makes the lock
    /// real rather than theoretical: two readers in one recipe now take
    /// turns. At 35 ms a read that is a better trade than two engines.
    /// </para>
    /// </remarks>
    private static Tesseract? _engine;

    private static readonly object EngineLock = new();

    public ReadTextNode()
    {
        ImageIn = AddInput<ImageData>("In");

        TextOut = AddOutput<string>("Text");
        ConfidenceOut = AddOutput<double>("Confidence");
        WordsOut = AddOutput<ShapeSet>("Words");
    }

    public InputPort ImageIn { get; }

    /// <summary>What was read, as one string.</summary>
    public OutputPort TextOut { get; }

    /// <summary>
    /// How sure it is, from 0 to 100, averaged over the words kept.
    /// </summary>
    /// <remarks>
    /// Wire it into a Limit Check set to At Least and a doubtful read
    /// fails the part rather than quietly becoming a wrong answer. Zero
    /// when nothing was read, so a missing label fails the same check.
    /// </remarks>
    public OutputPort ConfidenceOut { get; }

    /// <summary>A box per word, labelled with the word.</summary>
    public OutputPort WordsOut { get; }

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "Where to read, as x, y, w, h, angle. Draw it on the image.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Layout", Description = "How much text to expect in the region.")]
    public TextLayout Layout
    {
        get => _layout;
        set => SetParam(ref _layout, value);
    }

    /// <summary>
    /// Characters a reading may contain, as a validation rather than a
    /// correction.
    /// </summary>
    /// <remarks>
    /// Tesseract's own character whitelist is ignored by the LSTM engine
    /// - measured: the same picture reads identically with and without
    /// it - so setting it would be a parameter whose only job is to be
    /// wrong. It is applied here instead.
    ///
    /// A word containing anything outside the set is dropped, not
    /// stripped of the offending characters. A letter where only digits
    /// are possible means the read is wrong, and a wrong part number
    /// repaired into a plausible one is the worst thing this node could
    /// produce.
    /// </remarks>
    [Param("Allowed", Group = "Reading",
        Description = "Readings containing anything else are rejected, e.g. 0123456789. Blank accepts all.")]
    public string Allowed
    {
        get => _allowed;
        set => SetParam(ref _allowed, value ?? string.Empty);
    }

    [Param("Min Confidence", Group = "Reading", Min = 0, Max = 100, Step = 1,
        Editor = ParamEditor.Slider,
        Description = "Words the engine is less sure of than this are dropped.")]
    public double MinimumConfidence
    {
        get => _minimumConfidence;
        set => SetParam(ref _minimumConfidence, Math.Clamp(value, 0, 100));
    }

    [Param("Label", Group = "Reading", Description = "Draw each word beside its box.")]
    public bool ShowText
    {
        get => _showText;
        set => SetParam(ref _showText, value);
    }

    /// <summary>Where the language data is expected to be.</summary>
    public static string DataDirectory => Tesseract.DefaultTesseractDirectory;

    /// <summary>Whether the language data is installed beside the executable.</summary>
    public static bool IsLanguageDataPresent =>
        File.Exists(Path.Combine(DataDirectory, "eng.traineddata"));

    internal static PageSegMode ModeFor(TextLayout layout) => layout switch
    {
        TextLayout.Character => PageSegMode.SingleChar,
        TextLayout.Block => PageSegMode.SingleBlock,
        TextLayout.Scattered => PageSegMode.SparseText,
        _ => PageSegMode.SingleLine,
    };

    /// <summary>
    /// How much to magnify a region so the engine has something to read.
    /// </summary>
    /// <remarks>
    /// Whole numbers only, and never a reduction: resampling by 1.7 puts
    /// ringing on the strokes, and shrinking text has never helped anyone
    /// read it.
    /// </remarks>
    internal static int ScaleFor(int height) =>
        height >= ComfortableHeight
            ? 1
            : Math.Min(8, Math.Max(1, (int)Math.Ceiling((double)ComfortableHeight / Math.Max(1, height))));

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;

        if (!IsLanguageDataPresent)
        {
            context.Warn(
                $"No English language data in {DataDirectory}. It ships with the " +
                "application as tessdata/eng.traineddata; this build is missing it.");

            Publish(context, string.Empty, 0, ShapeSet.Empty);
            return;
        }

        var region = context.Param(nameof(Region), _region);

        using var straightened = Straighten(source, region);
        using var prepared = Prepare(straightened, out var scale);

        Tesseract.Word[] words;

        lock (EngineLock)
        {
            var engine = Engine();

            engine.PageSegMode = ModeFor(_layout);
            engine.SetImage(prepared);
            engine.Recognize();

            words = engine.GetWords();
        }

        var kept = words
            .Where(word => word.Confident >= _minimumConfidence)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Where(word => IsAllowed(word.Text, _allowed))
            .ToArray();

        if (kept.Length == 0)
        {
            Publish(context, string.Empty, 0, new ShapeSet([], source.Width, source.Height));
            return;
        }

        var text = string.Join(" ", kept.Select(word => word.Text.Trim()));
        var confidence = kept.Average(word => (double)word.Confident);

        Publish(context, text, confidence, Boxes(kept, region, prepared, scale, source));
    }

    /// <summary>
    /// Whether every character of a reading is one the recipe allows.
    /// </summary>
    /// <remarks>
    /// Whitespace is always allowed: it separates words rather than
    /// being part of one, and nobody writing an alphabet thinks to
    /// include a space in it.
    /// </remarks>
    internal static bool IsAllowed(string? text, string? allowed)
    {
        if (string.IsNullOrEmpty(allowed) || string.IsNullOrEmpty(text))
            return true;

        return text.All(character =>
            char.IsWhiteSpace(character) || allowed.Contains(character, StringComparison.Ordinal));
    }

    private void Publish(EvalContext context, string text, double confidence, ShapeSet words)
    {
        context.Set(TextOut, text);
        context.Set(ConfidenceOut, confidence);
        context.Set(WordsOut, words);
    }

    /// <summary>
    /// The shared engine, started on first use. Call under the lock.
    /// </summary>
    /// <remarks>
    /// LSTM only, because that is what the fast model contains: asking
    /// for the combined engine against it fails outright rather than
    /// falling back.
    /// </remarks>
    private static Tesseract Engine() =>
        _engine ??= new Tesseract(DataDirectory, "eng", OcrEngineMode.LstmOnly);

    /// <summary>
    /// Grey, magnified if the text is small, which is what the engine
    /// wants to see.
    /// </summary>
    private static Mat Prepare(Mat straightened, out int scale)
    {
        var grey = new Mat();

        try
        {
            if (straightened.NumberOfChannels > 1)
                CvInvoke.CvtColor(straightened, grey, ColorConversion.Bgr2Gray);
            else
                straightened.CopyTo(grey);

            if (grey.Depth != DepthType.Cv8U)
            {
                using var narrowed = new Mat();
                grey.ConvertTo(narrowed, DepthType.Cv8U, 1.0 / 257.0);
                narrowed.CopyTo(grey);
            }

            scale = ScaleFor(grey.Height);

            if (scale == 1)
                return grey;

            var enlarged = new Mat();
            CvInvoke.Resize(
                grey, enlarged,
                new System.Drawing.Size(grey.Width * scale, grey.Height * scale),
                0, 0, Inter.Cubic);

            grey.Dispose();

            return enlarged;
        }
        catch
        {
            grey.Dispose();
            throw;
        }
    }

    /// <summary>Rotates the region upright into an image of exactly its size.</summary>
    private static Mat Straighten(ImageData source, OrientedRect region)
    {
        var width = Math.Max(1, (int)Math.Round(region.Width));
        var height = Math.Max(1, (int)Math.Round(region.Height));

        using var affine = Transform(region, width, height);

        var destination = new Mat();

        try
        {
            CvInvoke.WarpAffine(
                source.Mat, destination, affine,
                new System.Drawing.Size(width, height),
                Inter.Linear, Warp.Default, BorderType.Replicate, default);

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private static Matrix<double> Transform(OrientedRect region, int width, int height)
    {
        var centre = new System.Drawing.PointF((float)region.CentreX, (float)region.CentreY);
        var affine = new Matrix<double>(2, 3);

        CvInvoke.GetRotationMatrix2D(centre, region.Angle, 1.0, affine);

        affine.Data[0, 2] += (width / 2.0) - region.CentreX;
        affine.Data[1, 2] += (height / 2.0) - region.CentreY;

        return affine;
    }

    /// <summary>
    /// Each word boxed where it is in the original picture.
    /// </summary>
    /// <remarks>
    /// Two transforms to undo: the magnification applied to make the text
    /// readable, and the rotation that squared the region up. Skipping
    /// either puts the boxes somewhere nobody is looking.
    /// </remarks>
    private ShapeSet Boxes(
        Tesseract.Word[] words, OrientedRect region, Mat prepared, int scale, ImageData source)
    {
        using var forward = Transform(region, prepared.Width / scale, prepared.Height / scale);
        using var inverse = new Matrix<double>(2, 3);
        CvInvoke.InvertAffineTransform(forward, inverse);

        var shapes = new List<Shape>(words.Length);

        foreach (var word in words)
        {
            var box = word.Region;

            var corners = new[]
            {
                (box.Left, box.Top), (box.Right, box.Top),
                (box.Right, box.Bottom), (box.Left, box.Bottom),
            }
            .Select(corner => Map(inverse, corner.Item1 / (double)scale, corner.Item2 / (double)scale))
            .ToArray();

            shapes.Add(new PolygonShape(corners)
            {
                Colour = Found,
                StrokeWidth = 2,
                Label = _showText ? word.Text.Trim() : null,
                Metrics = new Dictionary<string, double> { ["Confidence"] = word.Confident },
            });
        }

        return new ShapeSet(shapes, source.Width, source.Height);
    }

    private static Point2 Map(Matrix<double> inverse, double x, double y) => new(
        (inverse.Data[0, 0] * x) + (inverse.Data[0, 1] * y) + inverse.Data[0, 2],
        (inverse.Data[1, 0] * x) + (inverse.Data[1, 1] * y) + inverse.Data[1, 2]);
}
