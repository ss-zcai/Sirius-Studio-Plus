using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding a template: seeded noise, with a patch of it copied to known
/// places, so every position is known exactly.
/// </summary>
public class MatchTemplateTests
{
    public MatchTemplateTests() => TestCatalog.Ensure();

    private static Mat Noise(int seed, int width = 320, int height = 240)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height];
        random.NextBytes(pixels);

        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(pixels);

        // Softened, so a pixel's neighbours say something about it - as in
        // a real picture, and as matching to a fraction of a pixel needs.
        CvInvoke.GaussianBlur(mat, mat, new System.Drawing.Size(5, 5), 1.2);
        return mat;
    }

    /// <summary>A 40 x 30 patch of one noise picture.</summary>
    private static Mat Patch() => new Mat(Noise(7), new System.Drawing.Rectangle(100, 80, 40, 30)).Clone();

    /// <summary>A different noise picture with the patch pasted at each of the given corners.</summary>
    private static Mat WithPatches(params System.Drawing.Point[] corners)
    {
        var scene = Noise(11);
        using var patch = Patch();

        foreach (var corner in corners)
        {
            using var target = new Mat(scene, new System.Drawing.Rectangle(corner, patch.Size));
            patch.CopyTo(target);
        }

        return scene;
    }

    [Fact]
    public void The_template_is_found_where_it_was_put()
    {
        using var scene = WithPatches(new System.Drawing.Point(150, 60));
        using var patch = Patch();

        var match = Assert.Single(MatchTemplateNode.Find(scene, patch, 0.8, 5, 0.5));

        Assert.Equal(150, match.TopLeft.X, 1);
        Assert.Equal(60, match.TopLeft.Y, 1);
        Assert.InRange(match.Score, 0.99, 1);
    }

    [Fact]
    public void Several_copies_are_found_best_first_and_each_once()
    {
        using var scene = WithPatches(new(20, 20), new(200, 40), new(120, 170));
        using var patch = Patch();

        var matches = MatchTemplateNode.Find(scene, patch, 0.8, 10, 0.5);

        Assert.Equal(3, matches.Count);
        Assert.Equal(
            [(20, 20), (120, 170), (200, 40)],
            matches.Select(m => ((int)Math.Round(m.TopLeft.X), (int)Math.Round(m.TopLeft.Y))).Order());
        Assert.True(matches.Zip(matches.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));
    }

    /// <summary>The lights a little brighter than when the template was taken: still a match.</summary>
    [Fact]
    public void A_brighter_scene_still_matches()
    {
        using var scene = WithPatches(new System.Drawing.Point(150, 60));
        CvInvoke.Add(scene, new ScalarArray(new MCvScalar(30)), scene);
        using var patch = Patch();

        var match = Assert.Single(MatchTemplateNode.Find(scene, patch, 0.8, 1, 0.5));

        Assert.InRange(match.Score, 0.95, 1);
    }

    [Fact]
    public void A_template_of_one_grey_level_matches_nothing_rather_than_everything()
    {
        using var scene = Noise(11);
        using var flat = new Mat(30, 40, DepthType.Cv8U, 1);
        flat.SetTo(new MCvScalar(128));

        Assert.Empty(MatchTemplateNode.Find(scene, flat, 0.5, 5, 0.5));
    }

    [Fact]
    public void The_node_reports_the_match_where_the_template_is_in_the_whole_image()
    {
        var node = new MatchTemplateNode
        {
            SearchRegion = new System.Drawing.Rectangle(100, 30, 150, 120),
            MaximumCount = 5,
        };

        var graph = new VisionGraph();
        var scene = new PictureNode(() => WithPatches(new(20, 20), new(150, 60)));
        var template = new PictureNode(Patch);

        graph.Add(scene);
        graph.Add(template);
        graph.Add(node);
        graph.TryConnect(scene.Out, node.ImageIn);
        graph.TryConnect(template.Out, node.TemplateIn);

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

        using (scheduler)
        {
            scheduler.RunAsync().GetAwaiter().GetResult();

            // Only the copy inside the search region, in whole-image coordinates.
            Assert.Equal(1, scheduler.GetOutput(node.CountOut));

            var found = Assert.IsType<RectShape>(Assert.Single(scheduler.GetOutput<ShapeSet>(node.MatchesOut)!.Shapes));
            Assert.Equal(150, found.Rect.X, 1);
            Assert.Equal(60, found.Rect.Y, 1);

            var centre = (Point2)scheduler.GetOutput(node.PositionOut)!;
            Assert.Equal(170, centre.X, 1);
            Assert.Equal(75, centre.Y, 1);
        }
    }

    private sealed class PictureNode : VisionNode
    {
        private readonly Func<Mat> _picture;

        public PictureNode(Func<Mat> picture)
        {
            _picture = picture;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, ImageData.Adopt(_picture()));
    }
}
