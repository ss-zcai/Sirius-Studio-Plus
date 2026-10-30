using Sirius.Core.Graph;
using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Saying why something failed, not only that it did - the case that
/// prompted it being OpenCV failing to load on one tester's PC.
/// </summary>
public class DiagnosticsTests
{
    private static Exception CvExternMissing() =>
        new TypeInitializationException(
            "Emgu.CV.CvInvoke",
            new DllNotFoundException("Unable to load DLL 'cvextern' or one of its dependencies."));

    [Fact]
    public void An_exception_is_described_with_its_causes()
    {
        Assert.Equal(
            "The type initializer for 'Emgu.CV.CvInvoke' threw an exception. → " +
            "Unable to load DLL 'cvextern' or one of its dependencies.",
            ExceptionText.Describe(CvExternMissing()));
    }

    [Fact]
    public void A_cause_that_repeats_its_wrapper_is_said_once()
    {
        var repeated = new InvalidOperationException("No image.", new InvalidOperationException("No image."));

        Assert.Equal("No image.", ExceptionText.Describe(repeated));
    }

    /// <summary>A node that fails shows the reason in its status, not only the wrapper.</summary>
    [Fact]
    public void A_node_error_carries_the_cause()
    {
        var graph = new VisionGraph();
        var node = new ThrowingNode(CvExternMissing());
        graph.Add(node);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var scheduler = new GraphScheduler(graph);
            scheduler.RunAsync().GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("cvextern", node.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Opencv_loads_here()
    {
        Assert.Null(NativeLibraries.Problem());
    }

    [Fact]
    public void Without_media_foundation_the_advice_is_the_media_feature_pack()
    {
        var text = NativeLibraries.Explain(CvExternMissing(), hasMediaFoundation: false, hasCvExtern: true);

        Assert.Contains("Media Feature Pack", text, StringComparison.Ordinal);
        Assert.Contains("cvextern", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_cvextern_the_advice_is_to_extract_again()
    {
        var text = NativeLibraries.Explain(CvExternMissing(), hasMediaFoundation: true, hasCvExtern: false);

        Assert.Contains("Extract the whole zip again", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Media Feature Pack", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingNode(Exception failure) : VisionNode
    {
        protected override void OnEvaluate(EvalContext context) => throw failure;
    }
}
