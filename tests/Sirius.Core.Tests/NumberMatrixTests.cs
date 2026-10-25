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
/// A grid of numbers, with a vector as the single-row case.
/// </summary>
/// <remarks>
/// A vector is not its own type: one row is 1xN, one column is Nx1, and
/// either is a matrix. Two types would have meant anything consuming
/// vectors being taught matrices separately later.
/// </remarks>
public class NumberMatrixTests
{
    public NumberMatrixTests() => TestCatalog.Ensure();

    // ---- reading what people type ---------------------------------------------

    [Theory]
    [InlineData("3, 3", 1, 2)]
    [InlineData("3 3 1", 1, 3)]
    [InlineData("1 0; 0 1", 2, 2)]
    [InlineData("1,0,-1; 2,0,-2; 1,0,-1", 3, 3)]
    [InlineData("  1 2 ;  3 4  ", 2, 2)]
    public void It_reads_what_people_type(string text, int rows, int columns)
    {
        Assert.True(NumberMatrix.TryParse(text, out var matrix));

        Assert.Equal(rows, matrix.Rows);
        Assert.Equal(columns, matrix.Columns);
    }

    [Fact]
    public void A_single_row_is_a_vector()
    {
        var matrix = NumberMatrix.Parse("3, 3, 1");

        Assert.True(matrix.IsVector);
        Assert.False(matrix.IsSquare);
        Assert.Equal(new double[] { 3, 3, 1 }, matrix.Values);
    }

    [Fact]
    public void A_single_column_is_also_a_vector()
    {
        var matrix = NumberMatrix.Parse("3; 3; 1");

        Assert.Equal(3, matrix.Rows);
        Assert.Equal(1, matrix.Columns);
        Assert.True(matrix.IsVector);
    }

    /// <summary>
    /// A ragged grid is a typing mistake, not a shape worth having -
    /// accepting one would push the question onto every consumer.
    /// </summary>
    [Theory]
    [InlineData("1 2 3; 4 5")]
    [InlineData("1 2; 3 4 5")]
    public void A_ragged_grid_is_refused(string text) =>
        Assert.False(NumberMatrix.TryParse(text, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData("1, banana")]
    public void Nonsense_is_refused(string? text) =>
        Assert.False(NumberMatrix.TryParse(text, out _));

    [Fact]
    public void It_round_trips_through_its_own_text()
    {
        var original = new NumberMatrix(2, 3, [1, 2, 3, 4, 5, 6.5]);

        Assert.True(NumberMatrix.TryParse(original.ToString(), out var again));
        Assert.Equal(original, again);
    }

    [Fact]
    public void Reading_past_the_end_is_zero()
    {
        var matrix = new NumberMatrix(2, 2, [1, 2, 3, 4]);

        Assert.Equal(4, matrix[1, 1]);
        Assert.Equal(0, matrix[9, 9]);
        Assert.Equal(0, matrix[-1, 0]);
        Assert.Equal(0, matrix.At(99));
    }

    [Fact]
    public void A_mis_sized_matrix_is_refused_outright() =>
        Assert.Throws<ArgumentException>(() => new NumberMatrix(2, 2, [1, 2, 3]));

    // ---- the node ---------------------------------------------------------------

    [Fact]
    public void The_node_starts_as_a_vector()
    {
        var node = new MatrixNode();

        Assert.Equal("3, 3", node.Values);
        Assert.True(node.Value.IsVector);
    }

    [Fact]
    public void Unreadable_text_warns_rather_than_failing()
    {
        var graph = new VisionGraph();
        var node = new MatrixNode { Values = "1 2; 3" };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Contains("rectangular", node.StatusMessage!, StringComparison.Ordinal);
    }

    // ---- a kernel on a wire -------------------------------------------------------

    /// <summary>
    /// The first real consumer: a kernel is exactly a small grid of
    /// numbers, and until now one could only be typed into the filter.
    /// </summary>
    [Fact]
    public void A_wired_matrix_is_used_as_the_kernel()
    {
        // A kernel that multiplies by three, against an identity typed
        // into the node - so the result says which one was used.
        var (status, value) = RunFilter("3 0 0; 0 0 0; 0 0 0", typed: "0 0 0; 0 1 0; 0 0 0");

        Assert.Equal(NodeStatus.Ready, status);
        Assert.Equal(150, value);
    }

    [Fact]
    public void Without_a_wire_the_typed_kernel_is_used()
    {
        var (status, value) = RunFilter(null, typed: "0 0 0; 0 1 0; 0 0 0");

        Assert.Equal(NodeStatus.Ready, status);
        Assert.Equal(50, value);
    }

    /// <summary>
    /// A kernel has to be square and odd-sized, or there is no centre
    /// pixel to convolve about. Said plainly rather than left to OpenCV.
    /// </summary>
    [Theory]
    [InlineData("1 2 3")]
    [InlineData("1 2; 3 4")]
    public void A_kernel_that_cannot_convolve_is_refused(string wired)
    {
        var (status, _) = RunFilter(wired, typed: "0 0 0; 0 1 0; 0 0 0");

        Assert.Equal(NodeStatus.Error, status);
    }

    /// <summary>
    /// The matrix's own size wins over Kernel Size: a matrix knows how
    /// big it is, and a second number kept in step with it by hand would
    /// be a setting whose only job is to be wrong.
    /// </summary>
    [Fact]
    public void The_wired_size_wins_over_the_setting()
    {
        var (status, value) = RunFilter(
            "0 0 0 0 0; 0 0 0 0 0; 0 0 2 0 0; 0 0 0 0 0; 0 0 0 0 0",
            typed: "0 0 0; 0 1 0; 0 0 0",
            size: 3);

        Assert.Equal(NodeStatus.Ready, status);
        Assert.Equal(100, value);
    }

    /// <summary>
    /// Runs a 2D Filter over a flat image and reads the middle pixel.
    /// </summary>
    private static (NodeStatus Status, int Value) RunFilter(
        string? wired, string typed, int size = 3)
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 50 };
        var filter = new Filter2DNode { Kernel = typed, KernelSize = size };

        graph.Add(source);
        graph.Add(filter);
        graph.TryConnect(source.ImageOut, filter.ImageIn);

        if (wired is not null)
        {
            var matrix = new MatrixNode { Values = wired };
            graph.Add(matrix);
            graph.TryConnect(matrix.MatrixOut, filter.MatrixIn);
        }

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        if (filter.Status != NodeStatus.Ready)
            return (filter.Status, 0);

        var result = (ImageData)scheduler.GetOutput(filter.ImageOut)!;

        // Read one row straight out of the Mat: the middle pixel of a flat
        // image says what the kernel did to it.
        var row = new byte[result.Mat.Cols];
        System.Runtime.InteropServices.Marshal.Copy(
            result.Mat.DataPointer + (16 * result.Mat.Step), row, 0, row.Length);

        return (filter.Status, row[16]);
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

