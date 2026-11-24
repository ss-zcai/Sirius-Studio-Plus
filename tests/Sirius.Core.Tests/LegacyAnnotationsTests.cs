using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.IV;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The earlier image viewer's annotation files - a picture's
/// <c>name.tiff.json</c> - read, never written, by Sirius IV.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LegacyAnnotationsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyAnnotationsTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    /// <summary>As the earlier system writes them: JSON inside JSON, and Windows paths unescaped in info.</summary>
    private const string Inspected = """
        {
          "requestId": "260511155932312022",
          "fileName": "2-260511155932312022-Inspected-[2002].tiff",
          "algo": "Jianfan_work1_alg",
          "algoVersion": "0.0.16",
          "itemId": "26051115593231202",
          "args": [ "130" ],
          "meta": "{\"BatchNum\":\"2605110011\",\"PalletId\":3,\"PositionX\":1,\"PositionY\":2,\"MultiSeq\":2,\"MultiLast\":true,\"GroupId\":\"26051115593231202\",\"Requests\":[\"260511155932312021\",\"260511155932312022\"],\"QRCode\":\"260511155921052\",\"Station\":\"EndCapStation\",\"PixelFormat\":\"Mono8\"}",
          "output": {
            "result": 200,
            "defectCode": "2002",
            "info": "{\"features\": \"E:\\images\\alg\\feature_save\\a.features\", \"processing_time\": 921.6}",
            "annotations": "[{\"Type\": \"Circle\", \"Colour\": [255, 0, 0], \"LineWidth\": 2, \"Centre\": [2602, 2566], \"Radius\": 800, \"Name\": \"inspect area\"}, {\"Type\": \"Circle\", \"Colour\": [255, 0, 0], \"LineWidth\": 2, \"Centre\": [2602, 2566], \"Radius\": 1300, \"Name\": \"inspect area\"}, {\"Type\": \"RotatedRectangle\", \"Colour\": [255, 255, 0], \"LineWidth\": 2, \"Centre\": [3437, 3175], \"Width\": 60, \"Height\": 170, \"Rotation\": -53.9, \"Name\": \"mark\"}, {\"Type\": \"RotatedRectangle\", \"Colour\": [0, 0, 255], \"LineWidth\": 2, \"Centre\": [1938, 3146], \"Width\": 392, \"Height\": 214, \"Rotation\": 48, \"Name\": \"defect\"}]",
            "processingTime": 1063,
            "message": "Scratch"
          },
          "extra": {}
        }
        """;

    private const string Accepted = """
        {
          "requestId": "260511155932312021",
          "fileName": "2-260511155932312021-Accepted-[202].tiff",
          "algo": "Jianfan_work1_alg",
          "algoVersion": "0.0.16",
          "meta": "{\"MultiSeq\":1,\"MultiLast\":false,\"GroupId\":\"26051115593231202\",\"Requests\":[],\"PixelFormat\":\"Mono8\"}",
          "output": { "result": 202, "defectCode": "202", "info": "{\"processing_time\": 31.2}", "annotations": "[]", "processingTime": 179, "message": "Image received" },
          "extra": {}
        }
        """;

    private string Picture(string name, string? json)
    {
        var path = Path.Combine(_folder, name);

        using (var mat = new Mat(40, 50, DepthType.Cv8U, 1))
        {
            mat.SetTo(new MCvScalar(90));
            CvInvoke.Imwrite(path, mat);
        }

        if (json is not null)
            File.WriteAllText(LegacyAnnotations.PathFor(path), json);

        return path;
    }

    [Fact]
    public void Its_shapes_come_as_layers_by_name_in_their_own_colours_and_angles()
    {
        var file = LegacyAnnotations.FromJson((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Inspected)!);

        // A layer for each name, in the order first drawn; not written on the picture.
        Assert.Equal(["inspect area", "mark", "defect"], file.Layers.Select(layer => layer.Name));
        Assert.All(file.Layers, layer => Assert.All(layer.Shapes, shape => Assert.Null(shape.Label)));

        var circles = file.Layers[0].Shapes.Cast<CircleShape>().ToArray();
        Assert.Equal([800d, 1300d], circles.Select(circle => circle.Radius));
        Assert.Equal(new Point2(2602, 2566), circles[0].Centre);

        // A rotated rectangle turns clockwise, as ours do.
        var mark = Assert.IsType<OrientedRectShape>(Assert.Single(file.Layers[1].Shapes));
        Assert.Equal((new Point2(3437, 3175), 60d, 170d, -53.9), (mark.Centre, mark.Width, mark.Height, mark.AngleDegrees));

        // [b, g, r]: the defect red, the inspected area blue, the marks cyan.
        Assert.Equal(0xFFFF0000u, file.Layers[2].Shapes[0].Colour);
        Assert.Equal(0xFF0000FFu, circles[0].Colour);
        Assert.Equal(0xFF00FFFFu, mark.Colour);
    }

    [Fact]
    public void Its_meta_is_the_part_and_its_output_the_results()
    {
        var file = LegacyAnnotations.FromJson((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Inspected)!);
        var part = file.Part.ToDictionary(pair => pair.Key, pair => pair.Value);
        var results = file.Results.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal(3L, part["PalletId"]);
        Assert.Equal("26051115593231202", part["GroupId"]);
        Assert.Equal("260511155932312021, 260511155932312022", part["Requests"]);
        Assert.Equal(true, part["MultiLast"]);

        Assert.Equal(200L, results["Result"]);
        Assert.Equal("2002", results["Defect Code"]);
        Assert.Equal("Scratch", results["Message"]);
        Assert.Equal(1063L, results["Processing Time (ms)"]);

        // info, though its paths are not escaped.
        Assert.Equal(921.6, results["processing_time"]);
        Assert.Equal(@"E:\images\alg\feature_save\a.features", results["features"]);

        // 200 with a defect code other than 1: failed, the code and message its grade.
        Assert.False(file.Passed);
        Assert.Equal("FAIL", file.VerdictWord);
        Assert.Equal("2002 Scratch", file.Grade);
        Assert.Equal(("EndCapStation", 8, 260511155932312022L), (file.Station, file.Bits, file.Serial));
        Assert.Equal(new DateTime(2026, 5, 11, 15, 59, 32, 312), file.Time);
        Assert.Equal("Jianfan_work1_alg 0.0.16", file.Recipe);

        // The first shot: no shapes, received, not judged.
        var accepted = LegacyAnnotations.FromJson((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Accepted)!);
        Assert.Empty(accepted.Layers);
        Assert.Equal(("NONE", "202 Image received"), (accepted.VerdictWord, accepted.Grade));
    }

    /// <summary>As the CLT lens examples have them: ellipses, a layer name of its own, a defect's numbers, a pass.</summary>
    private const string Lens = """
        {
          "requestId": "101261112_101031996010803011",
          "fileName": "x_P01_C03_P1_1_Pass.tiff",
          "algo": "DDIForAstigmatismLenses",
          "algoVersion": "0.0.3",
          "meta": "{\"GroupID\":\"101261112_10103199601030301\",\"MultiLast\":true,\"DefectSettings\":\"[{\\\"Code\\\": 803}]\",\"PixelFormat\":\"Mono8\"}",
          "output": {
            "result": 200, "defectCode": "1", "message": "Pass", "processingTime": 526,
            "info": "{\"suggestedParameters\":\"{}\",\"processing_time\":526.4}",
            "annotations": "[{\"Type\": \"Ellipse\", \"Colour\": [0, 128, 255], \"LineWidth\": 1, \"Centre\": [1315.8, 1057.6], \"Width\": 608.4, \"Height\": 614.6, \"Rotation\": 88.1, \"Name\": \"Optical Zone\", \"Tag\": \"Dividing line\", \"LayerName\": \"Optical Zone\"}, {\"Type\": \"RotatedRectangle\", \"Colour\": [0, 0, 128], \"LineWidth\": 2, \"Centre\": [1286.6, 1320.9], \"Width\": 37.1, \"Height\": 16.3, \"Rotation\": -51.9, \"Name\": \"Foreign Material\", \"Tag\": \"opticalZone\", \"LayerName\": \"Defects\", \"ExtInfo\": {\"ParameterPath\": \"opticalZone.bright.blockForeignMaterial\", \"Descriptions\": {\"瑕疵名\": \"Foreign Material\", \"当前调参应用检出长度\": 20, \"瑕疵长度\": 8.53, \"瑕疵宽度\": 1.58, \"瑕疵置信度\": 1}}}]"
          },
          "extra": { "dataVersion": "2.4" }
        }
        """;

    [Fact]
    public void A_lens_reads_its_ellipses_layers_defect_sizes_and_pass()
    {
        var file = LegacyAnnotations.FromJson((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Lens)!);

        // 200 and defect code 1: passed, with no grade to say.
        Assert.Equal(("PASS", (string?)null), (file.VerdictWord, file.Grade));

        // The layer its own when it names one.
        Assert.Equal(["Optical Zone", "Defects"], file.Layers.Select(layer => layer.Name));

        var zone = Assert.IsType<EllipseShape>(Assert.Single(file.Layers[0].Shapes));
        Assert.Equal((new Point2(1315.8, 1057.6), 608.4, 614.6, 88.1), (zone.Centre, zone.MajorAxis, zone.MinorAxis, zone.Angle));
        Assert.Equal(0xFFFF8000u, zone.Colour);

        // The defect's numbers, in English; its name, text, left out.
        var defect = Assert.Single(file.Layers[1].Shapes);
        Assert.Equal(8.53, defect.Metrics["Length"]);
        Assert.Equal(1.58, defect.Metrics["Width"]);
        Assert.Equal(1, defect.Metrics["Confidence"]);
        Assert.Equal(20, defect.Metrics["Length Limit"]);
        Assert.Equal(4, defect.Metrics.Count);

        var results = file.Results.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("2.4", results["dataVersion"]);
        Assert.Equal("101261112_10103199601030301", file.Part.Single(pair => pair.Key == "GroupID").Value);
    }

    [Theory]
    [InlineData(200, "1", true, 0)]
    [InlineData(200, "803", false, 0)]
    [InlineData(202, "", null, 0)]
    [InlineData(500, "", null, 1)]
    public void Which_results_pass(int result, string code, bool? passed, int errors) =>
        Assert.Equal((passed, errors), LegacyAnnotations.VerdictOf(result, code));

    [Fact]
    public void Sirius_iv_lists_each_picture_with_its_verdict()
    {
        var fail = Picture("2-1-Inspected-[2002].tiff", Inspected);
        var pass = Picture("x_P01_C03_P1_1_Pass.tiff", Lens);
        var received = Picture("2-0-Accepted-[202].tiff", Accepted);

        Assert.Equal("FAIL", LegacyAnnotations.VerdictWordFor(fail));
        Assert.Equal("PASS", LegacyAnnotations.VerdictWordFor(pass));
        Assert.Equal("NONE", LegacyAnnotations.VerdictWordFor(received));
        Assert.Null(LegacyAnnotations.VerdictWordFor(Picture("bare.tiff", null)));

        Assert.True(new ImageFileItem(fail, _folder).Failed);
        Assert.True(new ImageFileItem(pass, _folder).Passed);
    }

    [Fact]
    public void A_sann_wins_and_a_file_that_is_neither_is_said()
    {
        var picture = Picture("2-1-Inspected-[2002].tiff", Inspected);

        Assert.True(AnnotationFile.ExistsFor(picture));
        Assert.Equal(3, AnnotationFile.ReadFor(picture, out _)!.Layers.Count);

        new AnnotationFile { Image = Path.GetFileName(picture), Station = "Ours" }.Save(AnnotationFile.PathFor(picture));
        Assert.Equal("Ours", AnnotationFile.ReadFor(picture, out _)!.Station);

        var other = Picture("other.tiff", """{ "something": "else" }""");
        Assert.Null(AnnotationFile.ReadFor(other, out var problem));
        Assert.Contains("earlier image viewer", problem, StringComparison.Ordinal);

        Assert.Null(AnnotationFile.ReadFor(Picture("bare.tiff", null), out problem));
        Assert.Null(problem);
    }

    /// <summary>
    /// In Sirius IV: its shapes on the picture; measured and saved, a .sann
    /// beside it starting from what it says - the earlier file untouched;
    /// copied to a folder, it goes too.
    /// </summary>
    [Fact]
    public void Sirius_iv_shows_it_saves_beside_it_and_carries_it()
    {
        var picture = Picture("2-260511155932312022-Inspected-[2002].tiff", Inspected);
        var legacy = LegacyAnnotations.PathFor(picture);
        var before = File.ReadAllText(legacy);

        WpfTestHost.OnStaThread(() =>
        {
            var model = new BrowserViewModel(null);
            WpfTestHost.Wait(model.OpenAsync(_folder));
            var item = Assert.Single(model.Files);

            var loaded = TileViewModel.Load(item);
            Assert.Equal(["inspect area", "mark", "defect"], loaded.Annotations!.Layers.Select(layer => layer.Name));
            Assert.Null(loaded.Problem);

            // Measured and saved: the .sann carries the earlier shapes and the measurement.
            model.Choose([item]);
            model.Measured(item, [new MeasurementRecord("PointToPoint", [new Point2(1, 1), new Point2(20, 1)], "#F59E0B", null)], [], 50, 40);
            Assert.True(model.Save());

            var saved = AnnotationFile.TryLoad(AnnotationFile.PathFor(picture), out _)!;
            Assert.Single(saved.Measurements);
            Assert.Equal(3, saved.Layers.Count);
            Assert.Equal("2002 Scratch", saved.Grade);
            Assert.Equal(before, File.ReadAllText(legacy));

            // Copied to a folder: the picture, its .sann and the earlier file.
            var target = Path.Combine(_folder, "out");
            var plan = BrowserViewModel.TransferPlan([item], target);
            Assert.Equal(3, plan[0].Files.Count);
            Assert.Contains(plan[0].Files, file => file.To.EndsWith(".tiff.json", StringComparison.Ordinal));
        });
    }
}
