using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;

namespace Sirius.Vision;

/// <summary>
/// An image prepared for repeated line sampling.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="Nodes.LineProfileNode"/> so the image viewer can
/// take the same readings interactively. One implementation means a line drawn
/// on the viewer and the same line typed into the node give the same numbers -
/// two implementations would eventually disagree, and the disagreement would
/// be invisible.
/// </para>
/// <para>
/// The preparation - splitting the channels and promoting them to float - is
/// proportional to the whole image, while a sample is proportional only to
/// the line. Doing it once and keeping the result is what lets the viewer
/// re-profile on every mouse move without stuttering. The cost is four bytes
/// per pixel per channel, held for as long as the frame is on screen, which
/// is why the viewer only prepares one while the profile tool is in use.
/// </para>
/// </remarks>
public sealed class LineProfileSource : IDisposable
{
    /// <summary>Guards against an absurd allocation from a tiny spacing on a long line.</summary>
    public const int MaxSamples = 200_000;

    public const double MinSpacing = 0.05;
    public const double MaxSpacing = 50.0;

    private Mat[]? _channels;

    private LineProfileSource(Mat[] channels, IReadOnlyList<string> names, int width, int height)
    {
        _channels = channels;
        ChannelNames = names;
        Width = width;
        Height = height;
    }

    /// <summary>What each prepared channel is called, in the image's order.</summary>
    public IReadOnlyList<string> ChannelNames { get; } = Array.Empty<string>();

    /// <summary>How many channels a sample returns.</summary>
    public int ChannelCount => _channels?.Length ?? 0;

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Prepares a copy of <paramref name="source"/>. The source is not retained,
    /// so the caller may dispose it immediately - which matters because the
    /// graph reclaims its buffers between evaluations.
    /// </summary>
    public static LineProfileSource From(ImageData source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Every channel is kept, rather than reduced to one intensity. A
        // colour profile is three traces, and which of them matters depends
        // on what is being inspected - a red fiducial against green solder
        // resist is invisible in luminance.
        //
        // The cost is four bytes per pixel per channel for as long as the
        // frame is held, so the viewer still only prepares one while the
        // profile tool is in use.
        using var split = new VectorOfMat();
        CvInvoke.Split(source.Mat, split);

        var channels = new Mat[split.Size];

        try
        {
            for (var index = 0; index < split.Size; index++)
            {
                // Float throughout: it keeps any input depth, and stops cubic
                // and Lanczos overshoot being clipped the way it would be in
                // 8-bit.
                var prepared = new Mat();
                split[index].ConvertTo(prepared, DepthType.Cv32F);
                channels[index] = prepared;
            }
        }
        catch
        {
            foreach (var channel in channels)
                channel?.Dispose();

            throw;
        }

        return new LineProfileSource(
            channels,
            ColourSpaces.NamesOf(source.Space, channels.Length),
            source.Width,
            source.Height);
    }

    /// <summary>
    /// Samples along a line at an even spacing, optionally averaging a band
    /// perpendicular to it.
    /// </summary>
    public ProfileData Sample(
        PointF start,
        PointF end,
        double spacing = 1.0,
        int thickness = 1,
        ProfileInterpolation interpolation = ProfileInterpolation.Linear,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_channels is null, this);

        spacing = Math.Clamp(spacing, MinSpacing, MaxSpacing);
        var rows = Math.Max(1, thickness) | 1;

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        if (distance < 1)
        {
            warn?.Invoke("Start and end points are the same; the profile is empty.");
            return new ProfileData(Empty(), start, end, spacing, rows);
        }

        var count = (int)Math.Floor(distance / spacing) + 1;

        if (count > MaxSamples)
        {
            warn?.Invoke(
                $"A spacing of {spacing:0.###} px over {distance:0.#} px needs " +
                $"{count:N0} samples; truncated to {MaxSamples:N0}.");

            count = MaxSamples;
        }

        // Unit vectors along the line and across it. Sampling is laid out in
        // these rather than in x and y, so spacing and thickness mean the same
        // thing whatever angle the line sits at - which is precisely what
        // walking a Bresenham line could not do.
        var alongX = dx / distance;
        var alongY = dy / distance;
        var acrossX = -alongY;
        var acrossY = alongX;

        var halfBand = (rows - 1) / 2.0;

        using var mapX = new Matrix<float>(rows, count);
        using var mapY = new Matrix<float>(rows, count);

        // Which samples actually landed on the image is decided here rather
        // than left to the border mode. Feeding NaN in as the border value
        // would be neater, but IEEE makes 0 * NaN = NaN, so a sample sitting
        // exactly on the last pixel - weight zero on the pixel beyond it -
        // would come back NaN as well.
        var inside = new bool[rows, count];
        var maxX = Width - 1;
        var maxY = Height - 1;

        for (var row = 0; row < rows; row++)
        {
            var offset = row - halfBand;

            for (var i = 0; i < count; i++)
            {
                var along = i * spacing;

                var x = start.X + (alongX * along) + (acrossX * offset);
                var y = start.Y + (alongY * along) + (acrossY * offset);

                mapX.Data[row, i] = (float)x;
                mapY.Data[row, i] = (float)y;
                inside[row, i] = x >= 0 && x <= maxX && y >= 0 && y <= maxY;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var sampled = new Matrix<float>(rows, count);
        var profiled = new ProfileChannel[_channels.Length];

        for (var index = 0; index < _channels.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Replicate keeps the interpolator fed near the edges - cubic and
            // Lanczos reach two and four pixels out - while the bounds check
            // above decides what is reported. Samples off the image become
            // gaps, not a plateau of invented border values.
            CvInvoke.Remap(
                _channels[index],
                sampled,
                mapX,
                mapY,
                ToInter(interpolation),
                BorderType.Replicate);

            // Only the first channel reports gaps: the geometry is shared, so
            // every channel would report the same count.
            profiled[index] = new ProfileChannel(
                ChannelNames[index],
                Average(sampled, inside, rows, count, index == 0 ? warn : null));
        }

        return new ProfileData(profiled, start, end, spacing, rows);
    }

    /// <summary>
    /// Collapses the band to one value per step along the line.
    /// </summary>
    /// <remarks>
    /// Averaging only the rows that landed inside the image means a band that
    /// overhangs an edge degrades rather than disappears. A step with no valid
    /// row at all stays NaN, and is reported.
    /// </remarks>
    private static float[] Average(
        Matrix<float> sampled,
        bool[,] inside,
        int rows,
        int count,
        Action<string>? warn)
    {
        var result = new float[count];
        var outside = 0;

        for (var i = 0; i < count; i++)
        {
            var total = 0.0;
            var valid = 0;

            for (var row = 0; row < rows; row++)
            {
                if (!inside[row, i])
                    continue;

                var value = sampled.Data[row, i];

                if (float.IsFinite(value))
                {
                    total += value;
                    valid++;
                }
            }

            if (valid == 0)
            {
                result[i] = float.NaN;
                outside++;
                continue;
            }

            result[i] = (float)(total / valid);
        }

        if (outside > 0)
            warn?.Invoke($"{outside} of {count} samples fall outside the image and are left as gaps.");

        return result;
    }

    private static Inter ToInter(ProfileInterpolation interpolation) => interpolation switch
    {
        ProfileInterpolation.Nearest => Inter.Nearest,
        ProfileInterpolation.Cubic => Inter.Cubic,
        ProfileInterpolation.Lanczos4 => Inter.Lanczos4,
        _ => Inter.Linear,
    };

    /// <summary>An empty profile that still has the right shape.</summary>
    private ProfileChannel[] Empty() =>
        ChannelNames.Select(name => new ProfileChannel(name, [])).ToArray();

    public void Dispose()
    {
        if (_channels is null)
            return;

        foreach (var channel in _channels)
            channel.Dispose();

        _channels = null;
    }
}
