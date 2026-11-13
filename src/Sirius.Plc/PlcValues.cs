using System.Globalization;

namespace Sirius.Plc;

/// <summary>The kinds of value a PLC address is read or written as.</summary>
/// <remarks>
/// The names are the IEC 61131-3 ones most PLC programmers know. Each is
/// carried in .NET as: Bool <c>bool</c>; Byte <c>byte</c>; Int16
/// <c>short</c>; UInt16 <c>ushort</c>; Int32 <c>int</c>; UInt32
/// <c>uint</c>; Real <c>float</c>; LReal <c>double</c>; String
/// <c>string</c>.
/// </remarks>
public enum PlcType
{
    Bool,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Real,
    LReal,
    String,
}

/// <summary>
/// An address in a PLC and what is kept there: <c>DB34.20.0</c> as a Bool,
/// <c>40001</c> as an Int16, <c>D100</c> as a String of 20 characters.
/// </summary>
/// <param name="Address">In the PLC brand's own words; each driver reads its own.</param>
/// <param name="Type">What the value is read or written as.</param>
/// <param name="Length">For a String, how many characters; ignored otherwise.</param>
public readonly record struct PlcVariable(string Address, PlcType Type, int Length = 0)
{
    public override string ToString() =>
        Type == PlcType.String ? $"{Address} (String[{Length}])" : $"{Address} ({Type})";
}

/// <summary>A PLC could not be reached, or refused what was asked of it.</summary>
/// <remarks>
/// What a driver throws for anything that is the line's fault rather than
/// the program's - no answer, an address the PLC does not have, a value it
/// would not take. The message is shown as it is, so it says what went
/// wrong in a sentence.
/// </remarks>
public class PlcException : Exception
{
    public PlcException(string message)
        : base(message)
    {
    }

    public PlcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Values to and from the kinds a PLC keeps.</summary>
public static class PlcValues
{
    /// <summary>The .NET type a value of <paramref name="type"/> is carried as.</summary>
    public static Type ClrType(PlcType type) => type switch
    {
        PlcType.Bool => typeof(bool),
        PlcType.Byte => typeof(byte),
        PlcType.Int16 => typeof(short),
        PlcType.UInt16 => typeof(ushort),
        PlcType.Int32 => typeof(int),
        PlcType.UInt32 => typeof(uint),
        PlcType.Real => typeof(float),
        PlcType.LReal => typeof(double),
        PlcType.String => typeof(string),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>What an address never written holds: false, 0, or empty text.</summary>
    public static object Default(PlcType type) => type == PlcType.String ? string.Empty : Convert(0, type);

    /// <summary>
    /// <paramref name="value"/> as <paramref name="type"/> carries it -
    /// from a number, yes/no or text.
    /// </summary>
    /// <exception cref="PlcException">It cannot be: text that is not a number, a number too large.</exception>
    public static object Convert(object? value, PlcType type)
    {
        try
        {
            return type switch
            {
                PlcType.Bool => ToBool(value),
                PlcType.String => Text(value),
                PlcType.Real => (float)ToDouble(value),
                PlcType.LReal => ToDouble(value),
                _ => ToWhole(value, type),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            throw new PlcException($"'{Text(value)}' is not a value a {type} can hold.", ex);
        }
    }

    /// <summary>
    /// Whether a value counts as on: true, a number other than 0, or text
    /// reading true, on, yes or a number other than 0.
    /// </summary>
    public static bool IsOn(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => TryBool(text, out var flag) && flag,
        IConvertible number => System.Convert.ToDouble(number, CultureInfo.InvariantCulture) != 0,
        _ => false,
    };

    /// <summary>A value as text, numbers in the invariant culture.</summary>
    public static string Text(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => TryBool(text, out var flag) ? flag : throw new FormatException(),
        _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
    };

    private static bool TryBool(string text, out bool flag)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "yes" or "1":
                flag = true;
                return true;

            case "false" or "off" or "no" or "0" or "":
                flag = false;
                return true;

            default:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    flag = number != 0;
                    return true;
                }

                flag = false;
                return false;
        }
    }

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        bool flag => flag ? 1 : 0,
        string text => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static object ToWhole(object? value, PlcType type)
    {
        // Whole numbers from text or a number with nothing after the point;
        // 2.5 into an Int16 is a mistake, not something to round.
        var number = ToDouble(value);

        if (number != Math.Floor(number))
            throw new FormatException();

        return type switch
        {
            PlcType.Byte => checked((byte)number),
            PlcType.Int16 => checked((short)number),
            PlcType.UInt16 => checked((ushort)number),
            PlcType.Int32 => checked((int)number),
            PlcType.UInt32 => checked((uint)number),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }
}
