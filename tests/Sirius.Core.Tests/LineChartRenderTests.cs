using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sirius.Studio.App.Controls;
using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The chart drawing several profiles at once, on differing axes.
/// </summary>
[Collection(WpfCollection.Name)]
public class LineChartRenderTests
{
    public LineChartRenderTests(WpfTestHost host) => _ = host;

    /// <summary>
    /// A 72-sample ring and a 36-sample one with a gap, overlaid: both are
    /// drawn, the second in the second pen's amber.
    /// </summary>
    [Fact]
    public void Two_profiles_on_different_spacings_are_both_drawn()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var chart = new ProfileControl
            {
                Series =
                [
                    new ChartSeries("Band", Ring(72, 5, gapAt: null)),
                    new ChartSeries("Fitted", Ring(36, 10, gapAt: 9)),
                ],
            };

            var pixels = Render(chart, 480, 240);

            Assert.Contains(pixels, pixel => Near(pixel, 0x60, 0xA5, 0xFA));
            Assert.Contains(pixels, pixel => Near(pixel, 0xFB, 0xBF, 0x24));
        });
    }

    [Fact]
    public void Profiles_with_nothing_measured_draw_a_message_rather_than_fail()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var empty = new float[36];
            Array.Fill(empty, float.NaN);

            var chart = new ProfileControl
            {
                Series =
                [
                    new ChartSeries("A", new ProfileData(empty, PointF.Empty, PointF.Empty, 10)),
                    new ChartSeries("B", new ProfileData(empty, PointF.Empty, PointF.Empty, 10)),
                ],
            };

            Render(chart, 300, 160);
        });
    }

    /// <summary>A sine of one pixel's amplitude round a ring, in degrees.</summary>
    private static ProfileData Ring(int count, double step, int? gapAt)
    {
        var samples = Enumerable.Range(0, count)
            .Select(i => i == gapAt ? float.NaN : (float)Math.Sin(i * step * Math.PI / 180))
            .ToArray();

        return new ProfileData(samples, PointF.Empty, PointF.Empty, step)
        {
            XUnit = "°",
            ValueFormat = "0.###",
            GapLabel = "missed",
            IsLine = false,
        };
    }

    private static uint[] Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new System.Windows.Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>An opaque pixel close to a colour, allowing for antialiasing.</summary>
    private static bool Near(uint pixel, byte r, byte g, byte b)
    {
        var alpha = (pixel >> 24) & 0xFF;
        if (alpha < 0xF0)
            return false;

        int red = (int)((pixel >> 16) & 0xFF), green = (int)((pixel >> 8) & 0xFF), blue = (int)(pixel & 0xFF);
        return Math.Abs(red - r) < 24 && Math.Abs(green - g) < 24 && Math.Abs(blue - b) < 24;
    }
}
