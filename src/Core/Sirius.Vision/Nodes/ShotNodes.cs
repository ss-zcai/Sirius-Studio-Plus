using System.Globalization;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

// Several shots per part (backlog F26, runtime-design 14.2, decision 93):
// the PLC triggers each picture of a part and says which it is; the recipe
// runs on each as it comes, and a Collect node keeps what each gave until
// the set's last is in. What follows a Collect runs once per part, and
// waits - not fails - before then.

/// <summary>Which picture of the part this is: its number, the part's item, and whether it is the last.</summary>
[VisionNode(
    "Shot",
    TypeKey = "source.shot",
    Category = NodeCategory.Source,
    Icon = "ph_shot",
    Description = "Which picture of the part this inspection is: its number in the part's set, the part's item, " +
                  "and whether it is the last. A recipe judging a part from several pictures - under different " +
                  "lights, from two cameras - switches on it; Collect nodes keep each picture's results until " +
                  "the last. On the line the PLC says which shot; at the desk, Shots per Part.")]
public sealed class ShotNode : VisionNode, IInspectionNode, IShotSource
{
    private int _shotsPerPart = 1;

    public ShotNode()
    {
        ShotOut = AddOutput<int>("Shot");
        ItemOut = AddOutput<string>("Item");
        LastOut = AddOutput<bool>("Last");
    }

    /// <summary>The picture's number in the part's set, from 1.</summary>
    public OutputPort ShotOut { get; }

    /// <summary>The part's item ID; empty at the desk with one shot a part.</summary>
    public OutputPort ItemOut { get; }

    /// <summary>The set is complete with this picture.</summary>
    public OutputPort LastOut { get; }

    [Param(
        "Shots per Part",
        Min = 1,
        Max = 16,
        Description = "At the desk - Run Folder, Run Continuously - how many pictures in a row make a part: a folder " +
                      "of sets in order, shot 1 to N of each part. On the line the station's Shots per Part and the " +
                      "PLC say instead.")]
    public int ShotsPerPart
    {
        get => _shotsPerPart;
        set => SetParam(ref _shotsPerPart, Math.Clamp(value, 1, 16));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var shot = context.Shot ?? ShotInfo.Single();

        context.Set(ShotOut, shot.Number);
        context.Set(ItemOut, shot.Item);
        context.Set(LastOut, shot.IsLast);
    }
}

/// <summary>
/// Keeps what each shot of a part gave, by shot number, until the set's
/// last: then gives them all, and what follows runs once for the part.
/// </summary>
/// <remarks>
/// A new item starts a new set, throwing away an unfinished one; a shot
/// given twice keeps the second. Edited at the desk after a set, the last
/// shot runs again with the shots before it still kept.
/// </remarks>
public abstract class CollectNode : VisionNode, IInspectionNode
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int, object> _kept = [];
    private string? _item;

    /// <summary>The shots kept for the set under way, by number.</summary>
    public IReadOnlyList<int> KeptShots
    {
        get
        {
            lock (_gate)
                return _kept.Keys.ToArray();
        }
    }

    protected sealed override void OnEvaluate(EvalContext context)
    {
        var shot = context.Shot ?? ShotInfo.Single();
        var value = Take(context);

        lock (_gate)
        {
            if (!string.Equals(_item, shot.Item, StringComparison.Ordinal))
            {
                Clear();
                _item = shot.Item;
            }

            if (_kept.Remove(shot.Number, out var before))
                Release(before);

            if (value is not null)
                _kept[shot.Number] = value;

            if (!shot.IsLast)
            {
                context.Wait(string.Create(CultureInfo.CurrentCulture, $"Shot {shot.Number} kept; waiting for the set's last."));
                return;
            }

            var missing = Enumerable.Range(1, shot.Number).Where(number => !_kept.ContainsKey(number)).ToArray();

            if (missing.Length > 0)
            {
                context.Warn(missing.Length == 1
                    ? $"Shot {missing[0]} of {shot.Number} is missing: judged on the rest."
                    : $"Shots {string.Join(", ", missing)} of {shot.Number} are missing: judged on the rest.");
            }

            if (_kept.Count == 0)
                throw new InvalidOperationException("No shot of the set gave anything to collect.");

            Produce(context, _kept.Select(pair => (pair.Key, pair.Value)).ToArray());
        }
    }

    /// <summary>This shot's value, copied if it must outlive the inspection; null when it gave none.</summary>
    protected abstract object? Take(EvalContext context);

    /// <summary>What the complete set gives, from each shot's value in shot order.</summary>
    protected abstract void Produce(EvalContext context, IReadOnlyList<(int Shot, object Value)> shots);

    /// <summary>Lets go of a value no longer kept: a picture's pixels.</summary>
    protected virtual void Release(object value)
    {
    }

    private void Clear()
    {
        foreach (var value in _kept.Values)
            Release(value);

        _kept.Clear();
    }
}

/// <summary>
/// A part's pictures, one per shot, combined: their average - colour
/// averaged over several lights - the brightest and the darkest of each
/// pixel.
/// </summary>
[VisionNode(
    "Collect Images",
    TypeKey = "combine.collect-images",
    Category = NodeCategory.Combine,
    Icon = "ph_collect_images",
    Description = "Keeps each shot's image until the part's last, then gives their average, and the brightest and " +
                  "darkest of each pixel: one picture of the part from several lights. What follows runs once for " +
                  "the part. The images must be the same size and kind.")]
public sealed class CollectImagesNode : CollectNode
{
    public CollectImagesNode()
    {
        ImageIn = AddInput<ImageData>("Image");
        AverageOut = AddOutput<ImageData>("Average");
        BrightestOut = AddOutput<ImageData>("Brightest");
        DarkestOut = AddOutput<ImageData>("Darkest");
        CountOut = AddOutput<int>("Count");
    }

    public InputPort ImageIn { get; }

    public OutputPort AverageOut { get; }

    public OutputPort BrightestOut { get; }

    public OutputPort DarkestOut { get; }

    /// <summary>How many shots' images were combined.</summary>
    public OutputPort CountOut { get; }

    public override InputPort? PrimaryInput => ImageIn;

    public override OutputPort? PrimaryOutput => AverageOut;

    // Copied: the station's picture is reused by its next shot.
    protected override object? Take(EvalContext context) => context.Get<ImageData>(ImageIn)?.Clone();

    protected override void Release(object value) => (value as ImageData)?.Dispose();

    protected override void Produce(EvalContext context, IReadOnlyList<(int Shot, object Value)> shots)
    {
        var images = shots.Select(shot => (ImageData)shot.Value).ToArray();
        var first = images[0];

        foreach (var (shot, value) in shots.Skip(1))
        {
            var image = (ImageData)value;

            if (image.Width != first.Width || image.Height != first.Height || image.Channels != first.Channels || image.Mat.Depth != first.Mat.Depth)
            {
                throw new InvalidOperationException(
                    $"Shot {shot}'s image is {image.Width}x{image.Height}, {image.Channels} channel(s), against shot " +
                    $"{shots[0].Shot}'s {first.Width}x{first.Height}, {first.Channels}: images collected must be alike.");
            }
        }

        using var sum = new Mat();
        first.Mat.ConvertTo(sum, DepthType.Cv64F);

        var brightest = first.Mat.Clone();
        var darkest = first.Mat.Clone();

        foreach (var image in images.Skip(1))
        {
            using var wide = new Mat();
            image.Mat.ConvertTo(wide, DepthType.Cv64F);
            CvInvoke.Add(sum, wide, sum);
            CvInvoke.Max(brightest, image.Mat, brightest);
            CvInvoke.Min(darkest, image.Mat, darkest);
        }

        var average = new Mat();
        sum.ConvertTo(average, first.Mat.Depth, 1.0 / images.Length);

        context.Set(AverageOut, ImageData.From(first, average));
        context.Set(BrightestOut, ImageData.From(first, brightest));
        context.Set(DarkestOut, ImageData.From(first, darkest));
        context.Set(CountOut, images.Length);
    }
}

/// <summary>
/// A part's verdict from each shot's: passed when every shot passed. The
/// part's check, so a check per shot wired in is not counted on its own.
/// </summary>
[VisionNode(
    "Collect Verdicts",
    TypeKey = "combine.collect-verdicts",
    Category = NodeCategory.Combine,
    Icon = "ph_collect_verdicts",
    Description = "Keeps each shot's pass or fail until the part's last, then passes when every shot passed - the " +
                  "part's verdict. Gives how many shots failed, and which.")]
public sealed class CollectVerdictsNode : CollectNode, IJudgingNode
{
    private bool _passed = true;

    public CollectVerdictsNode()
    {
        PassedOut = AddOutput<bool>("Passed");
        FailedCountOut = AddOutput<int>("Failed Count");
        FailedShotsOut = AddOutput<string>("Failed Shots");
    }

    /// <summary>Every shot passed: the part's verdict.</summary>
    public OutputPort PassedOut { get; }

    public OutputPort FailedCountOut { get; }

    /// <summary>The shots that failed, by number: "2, 4"; empty when none.</summary>
    public OutputPort FailedShotsOut { get; }

    OutputPort IJudgingNode.VerdictPort => PassedOut;

    [Param("Shot Passed", Connectable = true, Description = "This shot's pass or fail. Wire a check in.")]
    public bool ShotPassed
    {
        get => _passed;
        set => SetParam(ref _passed, value);
    }

    protected override object? Take(EvalContext context) => context.Param(nameof(ShotPassed), _passed);

    protected override void Produce(EvalContext context, IReadOnlyList<(int Shot, object Value)> shots)
    {
        var failed = shots.Where(shot => shot.Value is false).Select(shot => shot.Shot).ToArray();

        context.Set(PassedOut, failed.Length == 0);
        context.Set(FailedCountOut, failed.Length);
        context.Set(FailedShotsOut, string.Join(", ", failed.Select(shot => shot.ToString(CultureInfo.InvariantCulture))));
    }
}

/// <summary>A number from each shot, summed up for the part: total, mean, least, most.</summary>
[VisionNode(
    "Collect Numbers",
    TypeKey = "combine.collect-numbers",
    Category = NodeCategory.Combine,
    Icon = "ph_collect_numbers",
    Description = "Keeps a number from each shot until the part's last - a defect count, an area, a width - then " +
                  "gives their total, mean, least and most, for a check on the whole part.")]
public sealed class CollectNumbersNode : CollectNode
{
    private double _value;

    public CollectNumbersNode()
    {
        SumOut = AddOutput<double>("Sum");
        MeanOut = AddOutput<double>("Mean");
        MinimumOut = AddOutput<double>("Minimum");
        MaximumOut = AddOutput<double>("Maximum");
        CountOut = AddOutput<int>("Count");
    }

    public OutputPort SumOut { get; }

    public OutputPort MeanOut { get; }

    public OutputPort MinimumOut { get; }

    public OutputPort MaximumOut { get; }

    public OutputPort CountOut { get; }

    [Param("Value", Connectable = true, Description = "This shot's number. Wire a measurement in.")]
    public double Value
    {
        get => _value;
        set => SetParam(ref _value, value);
    }

    protected override object? Take(EvalContext context) => context.Param(nameof(Value), _value);

    protected override void Produce(EvalContext context, IReadOnlyList<(int Shot, object Value)> shots)
    {
        var numbers = shots.Select(shot => (double)shot.Value).ToArray();

        context.Set(SumOut, numbers.Sum());
        context.Set(MeanOut, numbers.Average());
        context.Set(MinimumOut, numbers.Min());
        context.Set(MaximumOut, numbers.Max());
        context.Set(CountOut, numbers.Length);
    }
}
