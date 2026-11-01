using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;

namespace Sirius.Core.Tests;

/// <summary>
/// Pictures with edges exactly where a test says, and a graph to run a
/// tool on them - shared by the edge, curve and lens tests.
/// </summary>
internal static class EdgeTestKit
{
    /// <summary>
    /// A dark field, light wherever <paramref name="inside"/> is positive.
    /// </summary>
    /// <remarks>
    /// <paramref name="inside"/> is a signed distance in pixels - positive
    /// in the part - and the intensity ramps across one pixel where it
    /// crosses zero, so the 50% crossing an edge detector finds is exactly
    /// on the boundary rather than wherever a rasteriser put it.
    /// </remarks>
    public static ImageData Render(int width, int height, Func<double, double, double> inside, byte dark = 30, byte light = 220)
    {
        var data = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var coverage = Math.Clamp(0.5 + inside(x, y), 0, 1);
                data[(y * width) + x] = (byte)Math.Round(dark + ((light - dark) * coverage));
            }
        }

        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    /// <summary>Runs <paramref name="graph"/> once, outside any synchronisation context, and hands back its results.</summary>
    public static GraphScheduler Run(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;

        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        scheduler.RunAsync().GetAwaiter().GetResult();
        return scheduler;
    }

    /// <summary>A graph of a picture wired into <paramref name="tool"/>'s first input.</summary>
    public static (VisionGraph Graph, PictureNode Source) Wire(Func<ImageData> picture, VisionNode tool)
    {
        var graph = new VisionGraph();
        var source = new PictureNode(picture);
        graph.Add(source);
        graph.Add(tool);
        graph.TryConnect(source.Out, tool.Inputs[0]);
        return (graph, source);
    }

    public static T Get<T>(this GraphScheduler scheduler, OutputPort port) => (T)scheduler.GetOutput(port)!;

    public static ShapeSet Shapes(this GraphScheduler scheduler, OutputPort port) => scheduler.GetOutput<ShapeSet>(port)!;

    /// <summary>Hands out a fresh copy of a picture each run.</summary>
    public sealed class PictureNode : VisionNode
    {
        private readonly Func<ImageData> _picture;

        public PictureNode(Func<ImageData> picture)
        {
            _picture = picture;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _picture());
    }

    /// <summary>Publishes fixed shapes, for wiring into a tool.</summary>
    public sealed class ShapesNode : VisionNode
    {
        private readonly ShapeSet _shapes;

        public ShapesNode(params Shape[] shapes)
        {
            _shapes = new ShapeSet(shapes, 0, 0);
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _shapes);
    }

    /// <summary>Publishes a fixed calibration.</summary>
    public sealed class CalibrationValue : VisionNode
    {
        private readonly Calibration _calibration;

        public CalibrationValue(Calibration calibration)
        {
            _calibration = calibration;
            Out = AddOutput<Calibration>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _calibration);
    }
}
