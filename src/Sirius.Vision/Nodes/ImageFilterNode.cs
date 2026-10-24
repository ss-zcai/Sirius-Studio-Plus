using Emgu.CV;
using Sirius.Core.Graph;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Base for the common case: one image in, one image out.
/// </summary>
/// <remarks>
/// <para>
/// Each of the old tools spent roughly 45 lines on identical plumbing - nine
/// interface members, each with <c>[Browsable(false)]</c>, plus a try/catch that
/// set <c>Status</c> and <c>StatusMessage</c>. Subclasses here implement
/// <see cref="Apply"/> only; port declaration, allocation, cleanup on failure,
/// and status reporting all happen once.
/// </para>
/// <para>
/// Exceptions propagate deliberately. The scheduler turns them into
/// <see cref="NodeStatus.Error"/> with the message, and forwards the input so
/// downstream nodes still produce something - the old behaviour, minus the
/// aliasing that <c>OutputImage = InputImage</c> introduced.
/// </para>
/// </remarks>
public abstract class ImageFilterNode : VisionNode
{
    protected ImageFilterNode()
    {
        ImageIn = AddInput<ImageData>("In");
        ImageOut = AddOutput<ImageData>("Out");
    }

    public InputPort ImageIn { get; }

    public OutputPort ImageOut { get; }

    protected sealed override void OnEvaluate(EvalContext context)
    {
        // Required input, so the scheduler guarantees this is non-null.
        var source = context.Get<ImageData>(ImageIn)!;

        var destination = new Mat();
        try
        {
            Apply(source, destination, context);
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        if (destination.IsEmpty)
        {
            destination.Dispose();
            throw new InvalidOperationException($"{Descriptor.DisplayName} produced an empty image.");
        }

        context.Set(ImageOut, ImageData.Adopt(destination, SpaceOf(source, destination)));
    }

    /// <summary>
    /// What the result's channels mean.
    /// </summary>
    /// <remarks>
    /// The input's, by default: a blur, a morphology or a brightness change
    /// does not reinterpret the channels. Overridden by the few that do -
    /// Colour Convert says what it converted to, and anything producing a
    /// mask says so.
    /// </remarks>
    protected virtual ColourSpace SpaceOf(ImageData source, Mat destination) =>
        destination.NumberOfChannels == source.Channels
            ? source.Space
            : ColourSpaces.FromChannelCount(destination.NumberOfChannels);

    /// <summary>
    /// Writes the result into <paramref name="destination"/>. Do not dispose
    /// <paramref name="source"/> or modify it in place.
    /// </summary>
    protected abstract void Apply(ImageData source, Mat destination, EvalContext context);
}
