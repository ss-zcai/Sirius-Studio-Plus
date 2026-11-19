using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// The cursor over a turning knob: a circle of arrow, heads at both ends,
/// white edged in black so it reads on a light picture and a dark one.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no such cursor among its own, and a hand - what the knobs
/// had - says "grab", not "turn". Drawn here rather than shipped as a
/// file, so it needs no resource and keeps to the shape of the rest.
/// </para>
/// <para>
/// Made once, on first use, as a .cur in memory: a 32-pixel image with
/// its hot spot in the middle, scaled by Windows with the display. Should
/// that ever fail, the hand is used, as before.
/// </para>
/// </remarks>
internal static class TurnCursor
{
    private const int Size = 32;

    private static Cursor? _cursor;

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

    /// <summary>The cursor as a .cur file: one 32-bit image, its hot spot at the centre.</summary>
    internal static byte[] CursorFile()
    {
        var pixels = Draw();

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
        writer.Write((ushort)(Size / 2));
        writer.Write((ushort)(Size / 2));
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

    /// <summary>The picture, as straight (not premultiplied) BGRA, top row first.</summary>
    private static byte[] Draw()
    {
        var centre = new Point(Size / 2.0, Size / 2.0);
        const double radius = 9;

        // A circle open at the top right: from 300 degrees round clockwise
        // to 210, on screen. Its heads point on round either way.
        const double start = -60;
        const double end = 210;

        Point At(double degrees) => new(
            centre.X + (radius * Math.Cos(degrees * Math.PI / 180)),
            centre.Y + (radius * Math.Sin(degrees * Math.PI / 180)));

        // Clockwise along the circle, as screen angles grow.
        Vector Along(double degrees) => new(
            -Math.Sin(degrees * Math.PI / 180),
            Math.Cos(degrees * Math.PI / 180));

        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(At(start), isFilled: false, isClosed: false);
            context.ArcTo(At(end), new Size(radius, radius), 0, isLargeArc: true, SweepDirection.Clockwise, true, false);
        }

        arc.Freeze();

        static Geometry Head(Point tip, Vector direction)
        {
            var across = new Vector(-direction.Y, direction.X);
            var point = tip + (direction * 3.5);
            var back = tip - (direction * 2.5);

            var head = new StreamGeometry();
            using (var context = head.Open())
            {
                context.BeginFigure(point, isFilled: true, isClosed: true);
                context.LineTo(back + (across * 4.5), true, false);
                context.LineTo(back - (across * 4.5), true, false);
            }

            head.Freeze();
            return head;
        }

        var heads = new GeometryGroup();
        heads.Children.Add(Head(At(end), Along(end)));
        heads.Children.Add(Head(At(start), -Along(start)));
        heads.Freeze();

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            var edge = new Pen(Brushes.Black, 4.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var line = new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            var headEdge = new Pen(Brushes.Black, 2.5) { LineJoin = PenLineJoin.Round };

            context.DrawGeometry(null, edge, arc);
            context.DrawGeometry(Brushes.Black, headEdge, heads);
            context.DrawGeometry(null, line, arc);
            context.DrawGeometry(Brushes.White, null, heads);
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
