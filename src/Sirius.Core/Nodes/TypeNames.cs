namespace Sirius.Core.Nodes;

/// <summary>
/// What a port's type is called where an operator can see it.
/// </summary>
/// <remarks>
/// <para>
/// "Image" rather than "ImageData", "Shapes" rather than "ShapeSet". The
/// class names are right for the code and wrong on a socket tooltip.
/// </para>
/// <para>
/// One table, because there were three - the node reference generator, the
/// connector tooltips and now the help window - and they had already drifted:
/// one called a double "double" and another "Number", and only one of them
/// knew what a ShapeSet was.
/// </para>
/// <para>
/// Keyed by type name rather than by type, so the user interface can use it
/// without taking a dependency on the node library that defines them.
/// </para>
/// </remarks>
public static class TypeNames
{
    /// <summary>
    /// An identifier written as words: "CentreToCentre" as "Centre To
    /// Centre", "Bgr2Gray" as "Bgr 2 Gray", "RGBImage" as "RGB Image".
    /// </summary>
    /// <remarks>
    /// For enum values in a dropdown and group names in the toolbox, which
    /// are identifiers in the code and labels on the screen.
    /// </remarks>
    public static string Words(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var builder = new System.Text.StringBuilder(identifier.Length + 8);

        for (var i = 0; i < identifier.Length; i++)
        {
            var current = identifier[i];

            if (i > 0 && identifier[i - 1] != ' ' && current != ' ')
            {
                var previous = identifier[i - 1];
                var next = i + 1 < identifier.Length ? identifier[i + 1] : '\0';

                var startsWord =
                    (char.IsUpper(current) && (char.IsLower(previous) || char.IsDigit(previous))) ||
                    (char.IsUpper(current) && char.IsUpper(previous) && char.IsLower(next)) ||
                    (char.IsDigit(current) && char.IsLetter(previous));

                if (startsWord)
                    builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    public static string Friendly(Type type) =>
        type is null ? "?" : Friendly(type.Name);

    public static string Friendly(string typeName) => typeName switch
    {
        "ImageData" => "Image",
        "Object" => "Any",
        "ShapeSet" => "Shapes",
        "HistogramData" => "Histogram",
        "ProfileData" => "Profile",
        "OrientedRect" => "Rectangle",
        "Point2" => "Point",
        "NumberMatrix" => "Matrix",
        "ValueRange" => "Range",
        "Rectangle" => "Region",
        "String" => "Text",
        "Int32" or "Int64" or "Int16" => "Integer",
        "Double" or "Single" => "Number",
        "Boolean" => "Yes/No",
        _ => typeName,
    };
}
