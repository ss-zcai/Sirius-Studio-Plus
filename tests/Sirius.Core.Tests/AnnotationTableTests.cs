using System.Globalization;
using System.IO;
using Sirius.Core.Annotations;
using Sirius.Core.Shapes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A picture's annotations read off as tables, and many pictures' written
/// as one CSV (F20b) - what Sirius IV's Annotations panel and Export show.
/// </summary>
public sealed class AnnotationTableTests
{
    private static AnnotationFile Annotated(long serial = 42, bool passed = false, params KeyValuePair<string, object?>[] results) => new()
    {
        Image = $"0{serial}.png",
        Width = 2448,
        Height = 2048,
        Bits = 12,
        Station = "Top",
        Recipe = "Top.sgraph",
        Serial = serial,
        Time = new DateTime(2026, 11, 21, 14, 23, 5, 123),
        Passed = passed,
        Grade = passed ? null : "Reject",
        Results = results.Length > 0 ? results : [new("Diameter", 12.4834), new("Sealed", true), new("Code", "LOT 4471")],
        Part = [new("PalletId", 12L)],
        Layers =
        [
            new AnnotationLayer("Main View",
            [
                new CircleShape(new Point2(100, 80), 10) { Label = "cap", Metrics = new Dictionary<string, double> { [ShapeMetric.Area] = 314.159, [ShapeMetric.Circularity] = 0.9981 } },
                new OrientedRectShape(new Point2(50, 40), 30, 10, 30),
            ]),
            new AnnotationLayer("Holes", [new PointShape(new Point2(7, 8)) { Metrics = new Dictionary<string, double> { [ShapeMetric.HoleCount] = 2 } }]),
        ],
    };

    [Fact]
    public void The_facts_are_the_picture_then_the_part_then_each_result()
    {
        var facts = AnnotationTable.Facts(Annotated());

        Assert.Equal(
            ["Station", "Recipe", "Inspection", "Time", "Verdict", "Size", "Part · PalletId", "Diameter", "Sealed", "Code"],
            facts.Select(fact => fact.Name));

        Assert.Equal("FAIL (Reject)", facts.Single(fact => fact.Name == "Verdict").Value);
        Assert.Equal("2448 × 2048, 12-bit", facts.Single(fact => fact.Name == "Size").Value);
        Assert.Equal(12.483.ToString(CultureInfo.CurrentCulture), facts.Single(fact => fact.Name == "Diameter").Value);
        Assert.Equal("Yes", facts.Single(fact => fact.Name == "Sealed").Value);
        Assert.Equal("12", facts.Single(fact => fact.Name == "Part · PalletId").Value);
    }

    [Fact]
    public void The_shapes_are_a_row_each_with_their_layer_kind_place_size_and_measurements()
    {
        var table = AnnotationTable.Shapes(Annotated().Layers);

        // Its own columns, then every measurement any of them carries, in reading order.
        Assert.Equal(["#", "Layer", "Kind", "Label", "X", "Y", "W", "H", "Area", "Circularity", "Holes"], table.Columns);
        Assert.Equal(3, table.Rows.Count);

        // A circle: its centre, its diameter; to a tenth of a pixel; ratios to three places.
        Assert.Equal(["1", "Main View", "Circle", "cap", "100", "80", "20", "20", Fmt(314.16), Fmt3(0.998), ""], table.Rows[0]);

        // A turned box: its own sides, not the box it fits in.
        Assert.Equal(["2", "Main View", "Box", "", "50", "40", "30", "10", "", "", ""], table.Rows[1]);

        // A point: where it is, and no size.
        Assert.Equal(["3", "Holes", "Point", "", "7", "8", "", "", "", "", "2"], table.Rows[2]);

        // Only the layers given - those switched on.
        Assert.Single(AnnotationTable.Shapes(Annotated().Layers.Skip(1)).Rows);

        static string Fmt(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        static string Fmt3(double value) => value.ToString("0.000", CultureInfo.CurrentCulture);
    }

    [Fact]
    public void A_csv_row_per_picture_has_every_result_met_with_a_full_stop_in_any_language()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            var pictures = new[]
            {
                new AnnotationPicture(@"2026-11-21\0042_FAIL.png", Annotated()),

                // The recipe changed: a new result, and one gone.
                new AnnotationPicture(@"2026-11-21\0043_PASS.png", Annotated(43, true, new("Diameter", 12.5), new("Gap, mm", 0.25))),

                // No annotation file: the file and the verdict its name says.
                new AnnotationPicture(@"2026-11-21\0044_FAIL.png", null, "FAIL"),
            };

            var text = new StringWriter();
            Assert.Equal(3, AnnotationCsv.WritePictures(text, pictures));

            var lines = text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("File,Station,Recipe,Inspection,Time,Verdict,Grade,Node Errors,Part: PalletId,Diameter,Sealed,Code,\"Gap, mm\"", lines[0]);
            Assert.Equal(@"2026-11-21\0042_FAIL.png,Top,Top.sgraph,42,2026-11-21 14:23:05.123,FAIL,Reject,0,12,12.4834,TRUE,LOT 4471,", lines[1]);
            Assert.Equal(@"2026-11-21\0043_PASS.png,Top,Top.sgraph,43,2026-11-21 14:23:05.123,PASS,,0,12,12.5,,,0.25", lines[2]);
            Assert.Equal(@"2026-11-21\0044_FAIL.png,,,,,FAIL,,,,,,,", lines[3]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_csv_row_per_shape_numbers_them_on_their_picture_across_its_layers()
    {
        var pictures = new[]
        {
            new AnnotationPicture("a.png", Annotated()),
            new AnnotationPicture("b.png", null, "PASS"),
            new AnnotationPicture("c.png", Annotated(7, true)),
        };

        var text = new StringWriter();
        Assert.Equal(6, AnnotationCsv.WriteShapes(text, pictures));

        var lines = text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("File,Station,Inspection,Verdict,Shape,Layer,Kind,Label,X,Y,W,H,Area,Circularity,Holes", lines[0]);
        Assert.Equal("a.png,Top,42,FAIL,1,Main View,Circle,cap,100,80,20,20,314.159,0.9981,", lines[1]);
        Assert.Equal("a.png,Top,42,FAIL,3,Holes,Point,,7,8,,,,,2", lines[3]);

        // No annotation file, no rows; the next picture counts from 1 again.
        Assert.StartsWith("c.png,Top,7,PASS,1,", lines[4], StringComparison.Ordinal);
        Assert.Equal(7, lines.Length);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(null, "")]
    public void A_field_is_quoted_only_when_it_must_be(string? value, string written) =>
        Assert.Equal(written, AnnotationCsv.Field(value));
}
