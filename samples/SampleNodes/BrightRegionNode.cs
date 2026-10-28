using Sirius.Core.Shapes;
using Sirius.Plugins;

namespace Sirius.Samples;

/// <summary>
/// Finds the part of an image brighter than a level, and how much of it there is.
/// </summary>
/// <remarks>
/// Shows the other outputs a node can have: a shape the viewer draws over
/// the image, a number, and a yes/no - any of which can be wired into a
/// Limit Check or another node's connectable setting.
/// </remarks>
[CustomNode("Bright Region",
    TypeKey = "samples.brightRegion",
    Group = NodeGroup.Analysis,
    Description = "Boxes everything brighter than a level, and reports what fraction of the image that is.")]
public sealed class BrightRegionNode : ICustomNode
{
    private InPort<Picture> _in = null!;
    private OutPort<ShapeSet> _region = null!;
    private OutPort<double> _fraction = null!;
    private OutPort<bool> _found = null!;

    /// <summary>
    /// As a fraction of full scale rather than a raw value, so one setting
    /// means the same thing on an 8-bit frame, a 16-bit one and a float one.
    /// </summary>
    [Setting(Min = 0, Max = 1, Step = 0.01, Connectable = true,
             Description = "How bright counts as bright, from 0 (black) to 1 (white).")]
    public double Level { get; set; } = 0.5;

    [Setting(Editor = SettingEditor.Colour, Group = "Display",
             Description = "The colour the box is drawn in.")]
    public string Colour { get; set; } = "#22C55E";

    public void Declare(INodeShape shape)
    {
        _in = shape.Input<Picture>("In");
        _region = shape.Output<ShapeSet>("Region");
        _fraction = shape.Output<double>("Fraction");
        _found = shape.Output<bool>("Found");
    }

    public void Run(IRunContext context)
    {
        var source = context.Read(_in);
        if (source is null)
            return;

        if (source.Channels > 1)
            context.Warn($"Uses the first of {source.Channels} channels; convert to Gray first to use brightness.");

        var level = Math.Clamp(context.Resolve(nameof(Level), Level), 0, 1);
        var threshold = level * FullScale(source);

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        long bright = 0;

        for (var y = 0; y < source.Height; y++)
        {
            context.ThrowIfCancelled();

            for (var x = 0; x < source.Width; x++)
            {
                if (Sample(source, x, y) <= threshold)
                    continue;

                bright++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        var fraction = (double)bright / ((long)source.Width * source.Height);
        var shapes = new List<Shape>();

        if (bright > 0)
        {
            shapes.Add(new RectShape(new Bounds(left, top, right - left + 1, bottom - top + 1))
            {
                Label = $"{fraction:P1}",
                Colour = ParseColour(Colour),
            });
        }

        context.Publish(_region, new ShapeSet(shapes, source.Width, source.Height));
        context.Publish(_fraction, fraction);
        context.Publish(_found, bright > 0);
    }

    private static double FullScale(Picture picture) => picture.Pixels switch
    {
        PixelType.Byte8 => 255,
        PixelType.UInt16 => 65535,
        _ => 1.0,
    };

    private static double Sample(Picture picture, int x, int y)
    {
        var index = x * picture.Channels;

        return picture.Pixels switch
        {
            PixelType.Byte8 => picture.Row<byte>(y)[index],
            PixelType.UInt16 => picture.Row<ushort>(y)[index],
            _ => picture.Row<float>(y)[index],
        };
    }

    /// <summary>"#RRGGBB" to the ARGB a shape's colour is stored as; null leaves the viewer's default.</summary>
    private static uint? ParseColour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var hex = text.TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? 0xFF000000 | rgb
            : null;
    }
}
