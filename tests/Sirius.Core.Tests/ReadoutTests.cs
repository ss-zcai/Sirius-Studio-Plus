using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Seeing what a node is producing when it is not a picture.
/// </summary>
/// <remarks>
/// An image or a set of shapes opens in a viewer. A number, a string or
/// a scale had nowhere to be seen, so the only way to find out what a
/// Calibrate or a Read Text had made was to wire it into something that
/// drew it on an image.
/// </remarks>
public class ReadoutTests
{
    public ReadoutTests() => TestCatalog.Ensure();

    // ---- which ports are read out -----------------------------------------

    /// <summary>
    /// Images and shape sets are left out: they have a viewer, and a
    /// line of text saying "ImageData" would be no use to anybody.
    /// </summary>
    [Fact]
    public void A_picture_is_not_read_out()
    {
        var node = new ReadCodeNode();

        Assert.False(Readout.IsReadable(node.CodesOut));
        Assert.True(Readout.IsReadable(node.TextOut));
        Assert.True(Readout.IsReadable(node.CountOut));
    }

    /// <summary>
    /// Outputs only. An input's value belongs to whoever produced it,
    /// and is read out on that node.
    /// </summary>
    [Fact]
    public void An_input_is_not_read_out() =>
        Assert.False(Readout.IsReadable(new MeasureNode().CalibrationIn));

    /// <summary>
    /// The node that prompted this: a scale with nowhere to be seen.
    /// </summary>
    [Fact]
    public void A_calibration_is_read_out()
    {
        var node = new CalibrateNode();

        Assert.True(Readout.IsReadable(node.CalibrationOut));
        Assert.True(Readout.IsReadable(node.ScaleOut));
    }

    // ---- how a value is written -------------------------------------------

    [Fact]
    public void Nothing_run_yet_reads_as_a_dash() => Assert.Equal("-", Readout.Describe(null));

    /// <summary>
    /// A code that read nothing is not the same as a port nothing has
    /// run on, and the two would otherwise look identical.
    /// </summary>
    [Fact]
    public void An_empty_string_says_so() => Assert.Equal("(empty)", Readout.Describe(string.Empty));

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(100.0, "100")]
    [InlineData(-5.0, "-5")]
    [InlineData(1.5, "1.5")]
    [InlineData(0.0412, "0.0412")]
    [InlineData(0.00001, "0")]
    public void A_number_keeps_the_figures_that_matter(double value, string expected) =>
        Assert.Equal(expected, Readout.Describe(value));

    /// <summary>
    /// Invariant, matching what is written on the image and into a
    /// saved recipe: a readout saying "1,5" where the label said "1.5"
    /// would start an argument about which was right.
    /// </summary>
    [Fact]
    public void A_number_is_written_the_way_the_label_writes_it()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

        try
        {
            Assert.Equal("1.5", Readout.Describe(1.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void A_verdict_reads_as_a_word(bool value, string expected) =>
        Assert.Equal(expected, Readout.Describe(value));

    [Fact]
    public void An_integer_is_written_plainly() => Assert.Equal("3", Readout.Describe(3));

    [Fact]
    public void A_calibration_writes_itself() =>
        Assert.Equal("0.05 mm", Readout.Describe(new Calibration(0.05, "mm")));

    [Fact]
    public void A_matrix_says_its_shape_as_well_as_its_values() =>
        Assert.StartsWith("2x2", Readout.Describe(new NumberMatrix(2, 2, [1, 0, 0, 1])));

    /// <summary>
    /// A block of OCR or a long payload would otherwise push the
    /// panel's width around on every run.
    /// </summary>
    [Fact]
    public void Long_text_is_cut_off()
    {
        var value = Readout.Describe(new string('x', 200));

        Assert.True(value.Length <= 60, $"was {value.Length} characters");
        Assert.EndsWith("…", value);
    }

    [Fact]
    public void Several_lines_are_brought_onto_one() =>
        Assert.Equal("one two", Readout.Describe("one\r\ntwo"));

    // ---- against a real node ----------------------------------------------

    /// <summary>
    /// End to end: a calibration computed by the graph, read back as
    /// the panel would show it.
    /// </summary>
    [Fact]
    public void A_run_leaves_something_to_read()
    {
        var graph = new VisionGraph();
        var node = new CalibrateNode
        {
            Source = CalibrationSource.Scale,
            UnitsPerPixel = 0.05,
            Unit = "mm",
        };

        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var readouts = node.Outputs
            .Where(Readout.IsReadable)
            .Select(port => Readout.For(port, scheduler.GetOutput(port)))
            .ToArray();

        Assert.Equal(2, readouts.Length);

        Assert.Equal("Calibration", readouts[0].Name);
        Assert.Equal("0.05 mm", readouts[0].Value);

        Assert.Equal("Units/Pixel", readouts[1].Name);
        Assert.Equal("0.05", readouts[1].Value);
    }

    /// <summary>
    /// Before anything has run there is nothing to show, and the panel
    /// says so rather than showing a stale or invented number.
    /// </summary>
    [Fact]
    public void Before_a_run_every_output_reads_as_nothing()
    {
        var graph = new VisionGraph();
        var node = new MeasureNode();
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);

        var readout = Readout.For(node.ValueOut, scheduler.GetOutput(node.ValueOut));

        Assert.Equal("-", readout.Value);
    }

    private static GraphScheduler CreateScheduler(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
