using Sirius.Core.Nodes;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirius.Core.Serialization;

/// <summary>
/// Compact converters for the <c>System.Drawing.Primitives</c> structs used as
/// parameter types.
/// </summary>
/// <remarks>
/// These exist rather than relying on the default object serializer because
/// <see cref="Rectangle"/> exposes overlapping writable views of the same state
/// (<c>X/Y/Width/Height</c> plus <c>Location</c> and <c>Size</c>). Round-tripping
/// it by reflection makes the result depend on property ordering, which is not
/// something a saved recipe should be sensitive to.
/// </remarks>
public sealed class RectangleJsonConverter : JsonConverter<Rectangle>
{
    public override Rectangle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int x = 0, y = 0, w = 0, h = 0;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for Rectangle.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "x": x = reader.GetInt32(); break;
                case "y": y = reader.GetInt32(); break;
                case "w": w = reader.GetInt32(); break;
                case "h": h = reader.GetInt32(); break;
            }
        }

        return new Rectangle(x, y, w, h);
    }

    public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("w", value.Width);
        writer.WriteNumber("h", value.Height);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A <see cref="ValueRange"/> as <c>{ "low": 0, "high": 255 }</c>.
/// </summary>
public sealed class ValueRangeJsonConverter : JsonConverter<ValueRange>
{
    public override ValueRange Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double? low = null, high = null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for ValueRange.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            // An absent end is written as null and read back as one. Writing
            // a sentinel number instead would make "no limit" indistinguishable
            // from a limit that happens to equal it.
            var value = reader.TokenType == JsonTokenType.Null
                ? (double?)null
                : reader.GetDouble();

            switch (name)
            {
                case "low": low = value; break;
                case "high": high = value; break;
            }
        }

        return new ValueRange(low, high);
    }

    public override void Write(Utf8JsonWriter writer, ValueRange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Low is { } low)
            writer.WriteNumber("low", low);
        else
            writer.WriteNull("low");

        if (value.High is { } high)
            writer.WriteNumber("high", high);
        else
            writer.WriteNull("high");

        writer.WriteEndObject();
    }
}

/// <summary>
/// An <see cref="OrientedRect"/> as its five numbers.
/// </summary>
public sealed class OrientedRectJsonConverter : JsonConverter<OrientedRect>
{
    public override OrientedRect Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double x = 0, y = 0, w = 1, h = 1, angle = 0;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for OrientedRect.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "x": x = reader.GetDouble(); break;
                case "y": y = reader.GetDouble(); break;
                case "w": w = reader.GetDouble(); break;
                case "h": h = reader.GetDouble(); break;
                case "angle": angle = reader.GetDouble(); break;
            }
        }

        return new OrientedRect(x, y, w, h, angle);
    }

    public override void Write(
        Utf8JsonWriter writer, OrientedRect value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.CentreX);
        writer.WriteNumber("y", value.CentreY);
        writer.WriteNumber("w", value.Width);
        writer.WriteNumber("h", value.Height);
        writer.WriteNumber("angle", value.Angle);
        writer.WriteEndObject();
    }
}

public sealed class SizeJsonConverter : JsonConverter<Size>
{
    public override Size Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int w = 0, h = 0;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for Size.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "w": w = reader.GetInt32(); break;
                case "h": h = reader.GetInt32(); break;
            }
        }

        return new Size(w, h);
    }

    public override void Write(Utf8JsonWriter writer, Size value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("w", value.Width);
        writer.WriteNumber("h", value.Height);
        writer.WriteEndObject();
    }
}

public sealed class PointJsonConverter : JsonConverter<Point>
{
    public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        int x = 0, y = 0;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for Point.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "x": x = reader.GetInt32(); break;
                case "y": y = reader.GetInt32(); break;
            }
        }

        return new Point(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}

/// <summary>Stores a colour as "#RRGGBB" / "#AARRGGBB".</summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return Color.Empty;

        var hex = text.TrimStart('#');
        return hex.Length switch
        {
            6 => Color.FromArgb(
                255,
                Convert.ToInt32(hex[..2], 16),
                Convert.ToInt32(hex[2..4], 16),
                Convert.ToInt32(hex[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToInt32(hex[..2], 16),
                Convert.ToInt32(hex[2..4], 16),
                Convert.ToInt32(hex[4..6], 16),
                Convert.ToInt32(hex[6..8], 16)),
            _ => throw new JsonException($"'{text}' is not a valid colour."),
        };
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
}
