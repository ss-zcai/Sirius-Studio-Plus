using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sirius.UI.Controls;

/// <summary>
/// The cursor over a turning knob: a broad band round most of a circle,
/// open at the bottom left, with one head at the top pointing left - white,
/// edged with a single black pixel so it reads on a light picture and a
/// dark one.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no such cursor among its own, and a hand - what the knobs
/// had - says "grab", not "turn". Its outline is a drawing,
/// <c>res\cursors\turn-cursor.svg</c>, built in and edited as any icon is;
/// the white fill and black edge are put on here. The same shape is also
/// drawn below, for when the drawing cannot be read.
/// </para>
/// <para>
/// Laid out on the pixel grid: the circle's centre and every straight edge
/// - the head's base, the band at its top, bottom and sides - are on half
/// pixels, so the one-pixel edge falls on one row or column of pixels
/// rather than greyed across two. Only the curves and the head's slopes
/// are smoothed.
/// </para>
/// <para>
/// Made once, on first use, as a .cur in memory: a 32-pixel image with its
/// hot spot at the circle's centre, scaled by Windows with the display.
/// Should that ever fail, the hand is used, as before.
/// </para>
/// </remarks>
internal static class TurnCursor
{
    private const int Size = 32;

    /// <summary>The circle's centre, on a half pixel so its edges are crisp.</summary>
    private const double CentreX = 16.5;

    private const double CentreY = 17.5;

    /// <summary>The built-in shape's hot spot: the pixel at the circle's centre.</summary>
    internal const int HotX = 16;

    internal const int HotY = 17;

    /// <summary>The drawing it is made from: res\cursors\turn-cursor.svg, built in.</summary>
    internal const string DrawingName = "Sirius.UI.Cursors.turn-cursor.svg";

    private static Cursor? _cursor;

    /// <summary>
    /// The outline and hot spot to draw: from the drawing built in, or -
    /// should it be missing or unreadable - the shape drawn here.
    /// </summary>
    internal static (Geometry Outline, int HotX, int HotY) Shape()
    {
        using var stream = typeof(TurnCursor).Assembly.GetManifestResourceStream(DrawingName);

        return stream is not null && FromSvg(stream) is { } drawn
            ? drawn
            : (Outline(), HotX, HotY);
    }

    /// <summary>
    /// The outlines of a drawing's shapes - path, polygon, rect, circle -
    /// scaled from its view box to the cursor's 32 pixels, as one shape;
    /// and its hot spot, the circle named "hotspot", or the middle. Null
    /// when nothing in it can be read.
    /// </summary>
    internal static (Geometry Outline, int HotX, int HotY)? FromSvg(Stream svg)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Load(svg).Root;
            if (root is null)
                return null;

            var box = (root.Attribute("viewBox")?.Value ?? $"0 0 {Size} {Size}")
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            if (box.Length != 4 || box[2] <= 0 || box[3] <= 0)
                return null;

            double Number(System.Xml.Linq.XElement element, string name) =>
                double.TryParse(
                    element.Attribute(name)?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value) ? value : 0;

            var shapes = new List<Geometry>();
            var hot = new Point(box[0] + (box[2] / 2), box[1] + (box[3] / 2));

            foreach (var element in root.Descendants())
            {
                var id = element.Attribute("id")?.Value ?? string.Empty;

                switch (element.Name.LocalName)
                {
                    case "circle" when id.Contains("hotspot", StringComparison.OrdinalIgnoreCase):
                        hot = new Point(Number(element, "cx"), Number(element, "cy"));
                        break;

                    case "path" when element.Attribute("d")?.Value is { Length: > 0 } data:
                        shapes.Add(Geometry.Parse(data));
                        break;

                    case "polygon" when element.Attribute("points")?.Value is { Length: > 0 } points:
                        shapes.Add(Geometry.Parse("M" + points.Trim() + "Z"));
                        break;

                    case "rect":
                        shapes.Add(new RectangleGeometry(new Rect(
                            Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height"))));
                        break;

                    case "circle":
                        shapes.Add(new EllipseGeometry(
                            new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "r"), Number(element, "r")));
                        break;
                }
            }

            if (shapes.Count == 0)
                return null;

            Geometry outline = shapes[0];
            foreach (var shape in shapes.Skip(1))
                outline = Geometry.Combine(outline, shape, GeometryCombineMode.Union, null);

            var scale = new MatrixTransform(Size / box[2], 0, 0, Size / box[3], -box[0] * Size / box[2], -box[1] * Size / box[3]);
            var placed = outline.Clone();
            placed.Transform = scale;
            placed.Freeze();

            var hotPoint = scale.Transform(hot);

            return (
                placed,
                Math.Clamp((int)Math.Floor(hotPoint.X), 0, Size - 1),
                Math.Clamp((int)Math.Floor(hotPoint.Y), 0, Size - 1));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or InvalidOperationException
                                       or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public static Cursor Value
    {
        get
        {
            if (_cursor is not null)
                return _cursor;

            try
            {
                using var stream = new MemoryStream(CursorFile());
                _cursor = new Cursor(stream, scaleWithDpi: true);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
                _cursor = Cursors.Hand;
            }

            return _cursor;
        }
    }

    /// <summary>The cursor as a .cur file: one 32-bit image, its hot spot at the circle's centre.</summary>
    internal static byte[] CursorFile()
    {
        var (outline, hotX, hotY) = Shape();
        var pixels = Pixels(outline);

        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);

        const int maskStride = Size / 8;
        const int imageBytes = 40 + (Size * Size * 4) + (Size * maskStride);

        // ICONDIR: reserved, type 2 for a cursor, one image.
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);

        // ICONDIRENTRY: size, no palette, the hot spot, where the image is.
        writer.Write((byte)Size);
        writer.Write((byte)Size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)hotX);
        writer.Write((ushort)hotY);
        writer.Write(imageBytes);
        writer.Write(22);

        // BITMAPINFOHEADER: twice the height, for the colour and the mask.
        writer.Write(40);
        writer.Write(Size);
        writer.Write(Size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(Size * Size * 4);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // The colour, bottom row first; alpha does the masking.
        for (var row = Size - 1; row >= 0; row--)
            writer.Write(pixels, row * Size * 4, Size * 4);

        writer.Write(new byte[Size * maskStride]);
        writer.Flush();

        return file.ToArray();
    }

    /// <summary>
    /// The outline: the band and its head as one shape, so the black edge
    /// runs round the outside of both and never across where they join.
    /// </summary>
    internal static Geometry Outline()
    {
        const double outer = 11;
        const double inner = 6;

        // From the top, round clockwise on screen - right, bottom - to just
        // past the left, where the band is cut square.
        const double end = 165;

        Point On(double radius, double degrees) => new(
            CentreX + (radius * Math.Cos(degrees * Math.PI / 180)),
            CentreY + (radius * Math.Sin(degrees * Math.PI / 180)));

        var band = new StreamGeometry();
        using (var context = band.Open())
        {
            context.BeginFigure(new Point(CentreX, CentreY - outer), isFilled: true, isClosed: true);
            context.ArcTo(On(outer, end), new Size(outer, outer), 0, isLargeArc: true, SweepDirection.Clockwise, true, false);
            context.LineTo(On(inner, end), true, false);
            context.ArcTo(new Point(CentreX, CentreY - inner), new Size(inner, inner), 0, isLargeArc: true, SweepDirection.Counterclockwise, true, false);
        }

        band.Freeze();

        // The head: its base upright through the circle's centre, on a half
        // pixel, about three times the band's width; its point to the left,
        // level with the middle of the band.
        var middle = CentreY - ((outer + inner) / 2);
        const double half = 6.5;
        const double reach = 8;

        var head = new StreamGeometry();
        using (var context = head.Open())
        {
            context.BeginFigure(new Point(CentreX, middle - half), isFilled: true, isClosed: true);
            context.LineTo(new Point(CentreX, middle + half), true, false);
            context.LineTo(new Point(CentreX - reach, middle), true, false);
        }

        head.Freeze();

        var outline = Geometry.Combine(band, head, GeometryCombineMode.Union, null);
        outline.Freeze();
        return outline;
    }

    /// <summary>The picture, as straight (not premultiplied) BGRA, top row first.</summary>
    internal static byte[] Pixels(Geometry outline)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            var edge = new Pen(Brushes.Black, 1) { LineJoin = PenLineJoin.Miter, MiterLimit = 4 };
            context.DrawGeometry(Brushes.White, edge, outline);
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var pixels = new byte[Size * Size * 4];
        bitmap.CopyPixels(pixels, Size * 4, 0);

        // A cursor's alpha is straight; WPF draws premultiplied.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255)
                continue;

            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / alpha);
        }

        return pixels;
    }
}
