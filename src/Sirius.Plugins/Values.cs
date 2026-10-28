using System.Globalization;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Plugins;

/// <summary>
/// The conversions a wire is allowed to make on the way into a node.
/// </summary>
/// <remarks>
/// The same ones the built-in nodes get, and shared with
/// <see cref="Testing.TestRun"/> so a test sees what the application does:
/// any number into any number, and a two-value matrix into a point.
/// </remarks>
internal static class Values
{
    public static bool TryConvert(object? value, Type target, out object? converted)
    {
        converted = null;

        if (value is null)
            return false;

        if (target.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (IsNumber(value.GetType()) && IsNumber(target))
        {
            try
            {
                converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (target == typeof(Point2) && value is NumberMatrix { Count: 2 } matrix)
        {
            converted = new Point2(matrix.At(0), matrix.At(1));
            return true;
        }

        return false;
    }

    private static bool IsNumber(Type type) =>
        type == typeof(double) || type == typeof(float) ||
        type == typeof(int) || type == typeof(long) ||
        type == typeof(short) || type == typeof(byte);
}
