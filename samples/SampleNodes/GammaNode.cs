using Sirius.Plugins;

namespace Sirius.Samples;

/// <summary>
/// Brightens or darkens the mid-tones, leaving black and white where they are.
/// </summary>
/// <remarks>
/// The smallest useful image-to-image node: one picture in, one out, one
/// setting - and it handles all three sample types, which is the part most
/// often forgotten. A node tested only on 8-bit frames fails the day
/// somebody connects a 12-bit camera.
/// </remarks>
[CustomNode("Gamma",
    TypeKey = "samples.gamma",
    Icon = "ph_brightness",
    Description = "Raises each pixel to a power. Above 1 darkens the mid-tones, below 1 brightens them. " +
                  "Black and white are unchanged.")]
public sealed class GammaNode : ICustomNode
{
    private InPort<Picture> _in = null!;
    private OutPort<Picture> _out = null!;

    [Setting(Min = 0.1, Max = 5.0, Step = 0.05, Connectable = true,
             Description = "The power. 1 leaves the image as it is.")]
    public double Gamma { get; set; } = 1.0;

    public void Declare(INodeShape shape)
    {
        _in = shape.Input<Picture>("In");
        _out = shape.Output<Picture>("Out");
    }

    public void Run(IRunContext context)
    {
        var source = context.Read(_in);
        if (source is null)
            return;

        // Through Resolve, not the property: a wire into Gamma overrides
        // what was typed in the properties panel.
        var gamma = context.Resolve(nameof(Gamma), Gamma);

        if (gamma <= 0)
            throw new InvalidOperationException($"Gamma must be above zero, not {gamma}.");

        // Nothing to do: hand the input straight back, which costs nothing.
        if (Math.Abs(gamma - 1.0) < 1e-9)
        {
            context.Publish(_out, source);
            return;
        }

        var result = Picture.Allocate(source);

        switch (source.Pixels)
        {
            case PixelType.Byte8:
                Apply(context, source, result, Table<byte>(255, gamma));
                break;

            case PixelType.UInt16:
                Apply(context, source, result, Table<ushort>(65535, gamma));
                break;

            default:
                ApplyFloat(context, source, result, gamma);
                break;
        }

        context.Publish(_out, result);
    }

    /// <summary>Every possible input value mapped once, rather than a power per pixel.</summary>
    private static T[] Table<T>(int fullScale, double gamma) where T : unmanaged
    {
        var table = new T[fullScale + 1];

        for (var value = 0; value <= fullScale; value++)
        {
            var mapped = Math.Round(fullScale * Math.Pow(value / (double)fullScale, gamma));
            table[value] = (T)Convert.ChangeType(mapped, typeof(T));
        }

        return table;
    }

    private static void Apply<T>(IRunContext context, Picture source, Picture result, T[] table)
        where T : unmanaged, IConvertible
    {
        for (var y = 0; y < source.Height; y++)
        {
            context.ThrowIfCancelled();

            var from = source.Row<T>(y);
            var to = result.WritableRow<T>(y);

            for (var x = 0; x < from.Length; x++)
                to[x] = table[from[x].ToInt32(null)];
        }
    }

    /// <summary>
    /// A float frame has no fixed full scale - it may run 0 to 1, or 0 to
    /// 255, or be the output of a filter - so its own brightest value is
    /// taken as white.
    /// </summary>
    private static void ApplyFloat(IRunContext context, Picture source, Picture result, double gamma)
    {
        var samples = source.Samples<float>();
        var white = 0f;

        foreach (var sample in samples)
            white = Math.Max(white, sample);

        if (white <= 0)
        {
            context.Warn("The picture has nothing above zero, so it was left as it is.");
            samples.CopyTo(result.WritableSamples<float>());
            return;
        }

        for (var y = 0; y < source.Height; y++)
        {
            context.ThrowIfCancelled();

            var from = source.Row<float>(y);
            var to = result.WritableRow<float>(y);

            for (var x = 0; x < from.Length; x++)
            {
                to[x] = from[x] <= 0
                    ? from[x]
                    : (float)(white * Math.Pow(from[x] / white, gamma));
            }
        }
    }
}
