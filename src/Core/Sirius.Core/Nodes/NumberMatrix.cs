using System.Globalization;
using System.Text;

namespace Sirius.Core.Nodes;

/// <summary>
/// A rectangular grid of numbers travelling as one value - a kernel, a
/// transform, a per-channel setting, or a vector as the single-row case.
/// </summary>
/// <remarks>
/// <para>
/// A vector is not its own type here. One row is 1xN, one column is Nx1,
/// and either is a matrix - so a Vector node and a Matrix node would be
/// two spellings of one idea, and anything consuming vectors would have
/// to be taught matrices separately later.
/// </para>
/// <para>
/// The text form is the one the 2D Filter already used for its kernel:
/// numbers separated by spaces or commas, rows by semicolons. That was not
/// chosen to match - it was already there, and matching it means a kernel
/// typed into the filter and a kernel arriving on a wire are written the
/// same way.
/// </para>
/// </remarks>
public sealed class NumberMatrix : IEquatable<NumberMatrix>
{
    private readonly double[] _values;

    public NumberMatrix(int rows, int columns, IReadOnlyList<double> values)
    {
        if (rows < 0 || columns < 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "A matrix cannot have negative extent.");

        if (values.Count != rows * columns)
        {
            throw new ArgumentException(
                $"A {rows}x{columns} matrix needs {rows * columns} values, not {values.Count}.",
                nameof(values));
        }

        Rows = rows;
        Columns = columns;
        _values = values.ToArray();
    }

    public static NumberMatrix Empty { get; } = new(0, 0, []);

    public int Rows { get; }

    public int Columns { get; }

    public int Count => _values.Length;

    /// <summary>Row-major, which is how the text form reads.</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>True when this is a single row or a single column.</summary>
    public bool IsVector => Rows == 1 || Columns == 1;

    /// <summary>True when it is as wide as it is tall, which a kernel must be.</summary>
    public bool IsSquare => Rows == Columns;

    public double this[int row, int column] =>
        row >= 0 && row < Rows && column >= 0 && column < Columns
            ? _values[(row * Columns) + column]
            : 0;

    /// <summary>
    /// The value at a position in reading order, or zero past the end.
    /// </summary>
    /// <remarks>
    /// Zero rather than a throw, because a consumer that wants three
    /// numbers from a two-number vector should be able to say so in its
    /// own words rather than crash.
    /// </remarks>
    public double At(int index) => index >= 0 && index < _values.Length ? _values[index] : 0;

    /// <summary>A single row, which is how a vector is written.</summary>
    public static NumberMatrix Row(params double[] values) =>
        new(values.Length == 0 ? 0 : 1, values.Length, values);

    /// <summary>
    /// Reads "1 2 3", "1,2,3" or "1 2 3; 4 5 6".
    /// </summary>
    /// <remarks>
    /// Every row must be the same length - a ragged grid is a typing
    /// mistake rather than a shape worth representing, and accepting one
    /// would push the question onto every consumer.
    /// </remarks>
    public static bool TryParse(string? text, out NumberMatrix matrix)
    {
        matrix = Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var rows = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new List<double[]>(rows.Length);

        foreach (var row in rows)
        {
            var parts = row.Split(
                [',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                continue;

            var values = new double[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(
                        parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return false;
                }
            }

            parsed.Add(values);
        }

        if (parsed.Count == 0)
            return false;

        var width = parsed[0].Length;

        if (parsed.Any(row => row.Length != width))
            return false;

        matrix = new NumberMatrix(parsed.Count, width, parsed.SelectMany(row => row).ToArray());
        return true;
    }

    public static NumberMatrix Parse(string? text) =>
        TryParse(text, out var matrix) ? matrix : Empty;

    public bool Equals(NumberMatrix? other) =>
        other is not null &&
        other.Rows == Rows &&
        other.Columns == Columns &&
        _values.SequenceEqual(other._values);

    public override bool Equals(object? obj) => Equals(obj as NumberMatrix);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Rows);
        hash.Add(Columns);

        foreach (var value in _values)
            hash.Add(value);

        return hash.ToHashCode();
    }

    /// <summary>
    /// Written the way it is typed, so a text box round-trips.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder();

        for (var row = 0; row < Rows; row++)
        {
            if (row > 0)
                text.Append("; ");

            for (var column = 0; column < Columns; column++)
            {
                if (column > 0)
                    text.Append(", ");

                text.Append(this[row, column].ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
}
