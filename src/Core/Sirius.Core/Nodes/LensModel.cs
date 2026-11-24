using System.Globalization;

namespace Sirius.Core.Nodes;

/// <summary>
/// What a lens does to the picture: the camera's focal length and centre,
/// and how far it bends straight lines.
/// </summary>
/// <remarks>
/// <para>
/// The pinhole model with radial and tangential distortion that OpenCV
/// calibrates - the one every machine-vision library uses, so a model from
/// elsewhere can be typed in. Distortion is <c>k1, k2, p1, p2, k3</c>:
/// <c>k</c> bends lines more the further they are from the centre, which is
/// a wide lens's barrel or a long lens's pincushion; <c>p</c> is a sensor
/// not quite square to the lens.
/// </para>
/// <para>
/// Carried on a wire, as <see cref="Calibration"/> is, and for the same
/// reason: it belongs to one camera and one lens, not to a recipe, and a
/// recipe with two cameras has two.
/// </para>
/// </remarks>
/// <param name="Fx">Focal length across, in pixels.</param>
/// <param name="Fy">Focal length down, in pixels.</param>
/// <param name="Cx">Where the lens's axis meets the sensor, across.</param>
/// <param name="Cy">Where the lens's axis meets the sensor, down.</param>
/// <param name="Distortion">k1, k2, p1, p2, k3.</param>
/// <param name="Width">Width of the images it was calibrated on.</param>
/// <param name="Height">Height of the images it was calibrated on.</param>
/// <param name="Error">Root-mean-square distance between where the target's points were found and where the model puts them, in pixels.</param>
/// <param name="Views">How many pictures of the target it was calibrated from.</param>
public sealed record LensModel(
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    IReadOnlyList<double> Distortion,
    int Width,
    int Height,
    double Error,
    int Views)
{
    /// <summary>
    /// The model for another image size - a binned or resized frame of the
    /// same camera - with the focal length and centre scaled to match.
    /// </summary>
    public LensModel ScaledTo(int width, int height)
    {
        if (width == Width && height == Height || Width <= 0 || Height <= 0)
            return this;

        var sx = (double)width / Width;
        var sy = (double)height / Height;

        return this with
        {
            Fx = Fx * sx,
            Fy = Fy * sy,
            Cx = Cx * sx,
            Cy = Cy * sy,
            Width = width,
            Height = height,
        };
    }

    /// <summary>Whether two models would correct a picture the same way.</summary>
    public bool SameCorrection(LensModel? other) =>
        other is not null &&
        Fx == other.Fx && Fy == other.Fy && Cx == other.Cx && Cy == other.Cy &&
        Width == other.Width && Height == other.Height &&
        Distortion.SequenceEqual(other.Distortion);

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "f {0:0.#}, {1:0.#}; c {2:0.#}, {3:0.#}; k {4}; error {5:0.###} px from {6} view{7}",
        Fx,
        Fy,
        Cx,
        Cy,
        string.Join(", ", Distortion.Select(value => value.ToString("0.#####", CultureInfo.InvariantCulture))),
        Error,
        Views,
        Views == 1 ? string.Empty : "s");
}
