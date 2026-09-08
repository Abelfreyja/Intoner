using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Services.Configuration;

[JsonConverter(typeof(RgbColorJsonConverter))]
[StructLayout(LayoutKind.Auto)]
internal readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static RgbColor FromNormalizedVector3(Vector3 value)
        => new(ToByte(value.X), ToByte(value.Y), ToByte(value.Z));

    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#')
        {
            return false;
        }

        if (!byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red)
         || !byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green)
         || !byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            return false;
        }

        color = new RgbColor(red, green, blue);
        return true;
    }

    public Vector3 ToNormalizedVector3()
        => new(Red / 255f, Green / 255f, Blue / 255f);

    public override string ToString()
        => $"#{Red:X2}{Green:X2}{Blue:X2}";

    private static byte ToByte(float value)
        => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}

internal sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
         || !RgbColor.TryParse(reader.GetString(), out RgbColor color))
        {
            throw new JsonException("RGB colors must use the #RRGGBB format");
        }

        return color;
    }

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
