using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Sirius.Core.Shapes;

namespace Sirius.Vision;

/// <summary>
/// Turns found geometry back into pixels.
/// </summary>
/// <remarks>
/// Shapes travel through the graph as geometry, which is what lets a blob
/// be measured, sorted and filtered. Using one as a mask needs the opposite
/// journey, and it is only worth making in one place: every shape kind has
/// a sensible filled form, and getting one of them wrong is a hole in a
/// mask that looks like a detection fault.
/// </remarks>
public static class ShapeRaster
{
    /// <summary>
    /// Hard edges. A mask is a yes or a no, and an anti-aliased edge makes
    /// a ring of partly-set pixels that every consumer then has to decide
    /// about - most of them by treating anything non-zero as yes, which
    /// quietly makes the region a pixel bigger all the way round.
    /// </summary>
    private const LineType Solid = LineType.EightConnected;

    /// <summary>
    /// Draws every shape into a single-channel mask: 255 inside, 0 outside.
    /// </summary>
    /// <param name="shapes">What to draw. A point or an open line has no
    /// inside, so it is drawn as a stroke of <paramref name="thickness"/>.</param>
    /// <param name="width">Mask width, in pixels.</param>
    /// <param name="height">Mask height, in pixels.</param>
    /// <param name="thickness">Stroke width for shapes with no area.</param>
    public static Mat Fill(ShapeSet? shapes, int width, int height, int thickness = 1)
    {
        var mask = new Mat(height, width, DepthType.Cv8U, 1);
        mask.SetTo(new MCvScalar(0));

        if (shapes is null)
            return mask;

        try
        {
            foreach (var shape in shapes.Shapes)
                Draw(mask, shape, Math.Max(1, thickness));
        }
        catch
        {
            mask.Dispose();
            throw;
        }

        return mask;
    }

    private static void Draw(Mat mask, Shape shape, int thickness)
    {
        var white = new MCvScalar(255);

        switch (shape)
        {
            case CircleShape circle:
                CvInvoke.Circle(
                    mask,
                    Round(circle.Centre),
                    Math.Max(1, (int)Math.Round(circle.Radius)),
                    white,
                    -1,
                    Solid);
                break;

            case EllipseShape ellipse:
                CvInvoke.Ellipse(
                    mask,
                    Round(ellipse.Centre),
                    new Size(
                        Math.Max(1, (int)Math.Round(ellipse.MajorAxis / 2)),
                        Math.Max(1, (int)Math.Round(ellipse.MinorAxis / 2))),
                    ellipse.Angle,
                    0,
                    360,
                    white,
                    -1,
                    Solid);
                break;

            case RectShape rect:
                // OpenCV takes two corners and includes them both, so a
                // ten-wide region drawn with a width of ten covers eleven
                // columns. Half a pixel of error everywhere a mask meets an
                // edge is exactly the sort of thing that is never noticed
                // and quietly shifts a measurement.
                CvInvoke.Rectangle(
                    mask,
                    new Rectangle(
                        (int)Math.Round(rect.Rect.X),
                        (int)Math.Round(rect.Rect.Y),
                        Math.Max(1, (int)Math.Round(rect.Rect.Width) - 1),
                        Math.Max(1, (int)Math.Round(rect.Rect.Height) - 1)),
                    white,
                    -1,
                    Solid);
                break;

            case OrientedRectShape oriented:
                FillPolygon(mask, oriented.Corners());
                break;

            case PolygonShape { Closed: true } polygon:
                FillPolygon(mask, polygon.Points);
                break;

            // No inside to fill, so the stroke is the shape.
            case PolygonShape polyline:
                StrokePolyline(mask, polyline.Points, thickness);
                break;

            case LineShape line:
                CvInvoke.Line(mask, Round(line.Start), Round(line.End), white, thickness);
                break;

            case PointShape point:
                CvInvoke.Circle(mask, Round(point.At), Math.Max(1, thickness), white, -1);
                break;
        }
    }

    private static void FillPolygon(Mat mask, IReadOnlyList<Point2> points)
    {
        if (points.Count < 3)
            return;

        using var contour = new VectorOfPoint(points.Select(Round).ToArray());
        using var contours = new VectorOfVectorOfPoint(contour);

        CvInvoke.FillPoly(mask, contours, new MCvScalar(255), Solid);
    }

    private static void StrokePolyline(Mat mask, IReadOnlyList<Point2> points, int thickness)
    {
        for (var i = 1; i < points.Count; i++)
            CvInvoke.Line(mask, Round(points[i - 1]), Round(points[i]), new MCvScalar(255), thickness);
    }

    private static Point Round(Point2 point) =>
        new((int)Math.Round(point.X), (int)Math.Round(point.Y));
}
