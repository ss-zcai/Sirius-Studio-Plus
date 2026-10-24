using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Computes a per-channel intensity histogram.
/// </summary>
/// <remarks>
/// In the old app the histogram was a fixed panel wired to whatever the tool
/// rack produced last, computed inline in <c>MainForm.UpdateHistogram</c>. As a
/// node it can be attached anywhere - including several at once, to compare
/// before and after a filter.
/// </remarks>
[VisionNode(
    "Histogram",
    TypeKey = "analysis.histogram",
    Category = NodeCategory.Analysis,
    Icon = "ph_histogram",
    Description = "Per-channel intensity histogram for the histogram panel.")]
public sealed class HistogramNode : VisionNode
{
    private const int MaxChannels = 3;

    private int _binCount = 256;

    public HistogramNode()
    {
        ImageIn = AddInput<ImageData>("In");
        HistogramOut = AddOutput<HistogramData>("Histogram");
    }

    public InputPort ImageIn { get; }

    public OutputPort HistogramOut { get; }

    [Param("Bins", Min = 2, Max = 256, Editor = ParamEditor.Slider)]
    public int BinCount
    {
        get => _binCount;
        set => SetParam(ref _binCount, Math.Clamp(value, 2, 256));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;

        if (source.Depth != DepthType.Cv8U)
        {
            // CalcHist needs an explicit range per depth; only 8-bit is wired up
            // so far, which covers everything the old histogram panel handled.
            context.Warn($"Histogram currently supports 8-bit images only (input is {source.Depth}).");
            return;
        }

        var ranges = new[] { 0f, 256f };
        var channels = new List<float[]>(Math.Min(source.Channels, MaxChannels));

        using var planes = new VectorOfMat();
        var split = source.Mat.Split();
        try
        {
            planes.Push(split);

            using var hist = new Mat();
            for (var channel = 0; channel < Math.Min(planes.Size, MaxChannels); channel++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                CvInvoke.CalcHist(
                    planes,
                    new[] { channel },
                    null,
                    hist,
                    new[] { _binCount },
                    ranges,
                    accumulate: false);

                var bins = new float[_binCount];
                hist.CopyTo(bins);
                channels.Add(bins);
            }
        }
        finally
        {
            // Split() hands back Mats the caller owns; VectorOfMat.Push copies
            // the headers rather than taking ownership.
            foreach (var plane in split)
                plane.Dispose();
        }

        context.Set(HistogramOut, new HistogramData(channels, _binCount));
    }
}
