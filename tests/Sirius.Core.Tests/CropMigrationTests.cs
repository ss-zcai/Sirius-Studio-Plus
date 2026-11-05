using Emgu.CV;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Crop is gone: Region of Interest at no angle, cutting the bounding box,
/// is the same cut. A recipe that used one opens with a Region of Interest
/// in its place, set up to cut exactly what the Crop cut.
/// </summary>
public class CropMigrationTests
{
    public CropMigrationTests() => TestCatalog.Ensure();

    private const string Recipe = """
    {
      "version": 2,
      "name": "crops",
      "nodes": [
        {
          "id": "a", "type": "basic.crop", "name": "Crop 1", "enabled": true, "x": 0, "y": 0,
          "params": { "Roi": { "x": 10, "y": 20, "w": 50, "h": 30 }, "FollowReferenceAngle": true }
        },
        {
          "id": "b", "type": "basic.crop", "name": "Crop 2", "enabled": true, "x": 300, "y": 0,
          "params": { "Roi": { "x": 5, "y": 5, "w": 20, "h": 10 } }
        }
      ],
      "connections": [
        { "fromNode": "a", "fromPort": "Out", "toNode": "b", "toPort": "In" }
      ]
    }
    """;

    [Fact]
    public void A_crop_opens_as_a_region_of_interest_cutting_the_same_box()
    {
        var loaded = GraphSerializer.Deserialize(Recipe);

        Assert.Empty(loaded.Warnings);

        var first = Find(loaded.Graph, "a");
        var second = Find(loaded.Graph, "b");

        Assert.Equal("Crop 1", first.Name);
        Assert.Equal(new OrientedRect(35, 35, 50, 30, 0), first.Rect);
        Assert.Equal(RoiAlignment.BoundingBox, first.Alignment);

        // As saved: following the angle puts the region's centre where the
        // Crop's went when the reference turned.
        Assert.True(first.FollowReferenceAngle);

        Assert.True(second.ImageIn.IsConnected);
    }

    [Fact]
    public void The_migrated_cut_is_the_same_pixels()
    {
        var loaded = GraphSerializer.Deserialize(Recipe);
        var first = Find(loaded.Graph, "a");

        var source = new EdgeTestKit.PictureNode(Ramp);
        loaded.Graph.Add(source);
        loaded.Graph.TryConnect(source.Out, first.ImageIn);

        using var scheduler = EdgeTestKit.Run(loaded.Graph);

        Assert.Equal(NodeStatus.Ready, first.Status);

        var cut = scheduler.Get<ImageData>(first.ImageOut);
        using var ramp = Ramp();
        using var expected = new Mat(ramp.Mat, new System.Drawing.Rectangle(10, 20, 50, 30));

        Assert.Equal((50, 30), (cut.Width, cut.Height));
        Assert.Equal(0, CvInvoke.Norm(cut.Mat, expected, Emgu.CV.CvEnum.NormType.L1));
    }

    [Fact]
    public void A_crop_with_no_region_says_so_and_gets_a_default_one()
    {
        var loaded = GraphSerializer.Deserialize(Recipe.Replace(
            "\"x\": 10, \"y\": 20, \"w\": 50, \"h\": 30",
            "\"x\": 0, \"y\": 0, \"w\": 0, \"h\": 0"));

        var warning = Assert.Single(loaded.Warnings);
        Assert.Contains("Crop 1", warning);

        Assert.Equal(new RegionOfInterestNode().Rect, Find(loaded.Graph, "a").Rect);
    }

    private static RegionOfInterestNode Find(VisionGraph graph, string id) =>
        Assert.IsType<RegionOfInterestNode>(graph.Nodes.Single(node => node.Id == id));

    /// <summary>Every pixel different, so a cut one pixel out is caught.</summary>
    private static ImageData Ramp()
    {
        const int Width = 200, Height = 100;
        var data = new byte[Width * Height];

        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)((i * 7) + (i / Width));

        var mat = new Mat(Height, Width, Emgu.CV.CvEnum.DepthType.Cv8U, 1);
        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }
}
