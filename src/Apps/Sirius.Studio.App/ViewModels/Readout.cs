using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// What one of a node's outputs is currently carrying, as text.
/// </summary>
/// <remarks>
/// <para>
/// An image or a set of shapes can be opened in a viewer and looked at.
/// A number could not: a Calibrate node would report a scale, a Measure
/// a distance, a Read Text a string, and the only way to find out what
/// any of them had produced was to wire it into something that drew it
/// on a picture.
/// </para>
/// <para>
/// Shown on the properties panel rather than reached by wiring a watch
/// node in, because the question is asked of a node already selected
/// and being adjusted - and because a debugging aid that has to be
/// built into the recipe gets left there.
/// </para>
/// </remarks>
public sealed record Readout(string Name, string Type, string Value)
{
    /// <summary>
    /// Long text is cut off here. A barcode payload or a block of OCR
    /// would otherwise push the panel's width around on every run.
    /// </summary>
    private const int Longest = 60;

    /// <summary>
    /// Whether a port carries something worth writing out rather than
    /// opening in a viewer.
    /// </summary>
    public static bool IsReadable(Port port)
    {
        ArgumentNullException.ThrowIfNull(port);

        return port.Direction == PortDirection.Output &&
               port.DataType != typeof(ImageData) &&
               port.DataType != typeof(ShapeSet);
    }

    public static Readout For(Port port, object? value)
    {
        ArgumentNullException.ThrowIfNull(port);

        return new Readout(
            port.DisplayName,
            TypeNames.Friendly(port.DataType),
            Describe(value));
    }

    /// <summary>
    /// A value as a person would want to read it.
    /// </summary>
    /// <remarks>
    /// Invariant formatting, matching how the same numbers are written
    /// on the image and into a saved recipe: a readout that said "1,5"
    /// where the label said "1.5" would start an argument about which
    /// was right.
    /// </remarks>
    public static string Describe(object? value) => value switch
    {
        null => "-",

        // Before the string case: a decoded code or an OCR result that
        // read nothing is not the same as a port nothing has run on.
        string { Length: 0 } => "(empty)",
        string text => Shorten(text),

        bool yes => yes ? "Yes" : "No",

        // Whole numbers without a decimal point, the rest with enough
        // figures to see a sub-pixel difference.
        double number => number.ToString(
            Math.Abs(number % 1) < 1e-9 ? "0" : "0.####", CultureInfo.InvariantCulture),
        float number => Describe((double)number),

        IFormattable number and (int or long or short or byte or decimal) =>
            number.ToString(null, CultureInfo.InvariantCulture),

        NumberMatrix matrix => Shorten($"{matrix.Rows}x{matrix.Columns}  {matrix}"),

        // Everything else that writes itself: Calibration, Ring,
        // OrientedRect, Point2 - each already has the text form it uses
        // in the inspector and in a saved file.
        _ => Shorten(value.ToString() ?? "-"),
    };

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();

        return single.Length <= Longest ? single : single[..(Longest - 1)] + "…";
    }
}
