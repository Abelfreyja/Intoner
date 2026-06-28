using FFXIVClientStructs.FFXIV.Client.Graphics;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectColorUtility
{
    public static Vector4 ClampNormalizedColor(Vector4 color)
        => new(
            Math.Clamp(color.X, 0f, 1f),
            Math.Clamp(color.Y, 0f, 1f),
            Math.Clamp(color.Z, 0f, 1f),
            Math.Clamp(color.W, 0f, 1f));

    public static Vector4 ClampOpaqueNormalizedColor(Vector4 color)
        => new(
            Math.Clamp(color.X, 0f, 1f),
            Math.Clamp(color.Y, 0f, 1f),
            Math.Clamp(color.Z, 0f, 1f),
            1f);

    public static byte ToRoundedByteComponent(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    public static ByteColor ToByteColor(Vector4 color)
        => new()
        {
            R = ToRoundedByteComponent(color.X),
            G = ToRoundedByteComponent(color.Y),
            B = ToRoundedByteComponent(color.Z),
            A = ToRoundedByteComponent(color.W),
        };

    public static ByteColor ToOpaqueByteColor(byte red, byte green, byte blue)
        => new()
        {
            R = red,
            G = green,
            B = blue,
            A = byte.MaxValue,
        };

    public static ByteColor ToOpaqueByteColor(Vector4 color)
        => new()
        {
            R = ToRoundedByteComponent(color.X),
            G = ToRoundedByteComponent(color.Y),
            B = ToRoundedByteComponent(color.Z),
            A = byte.MaxValue,
        };

    public static Vector4 ToOpaqueNormalizedColor(ByteColor color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f, 1f);

    public static bool TryParseHexColor(string? value, out Vector4 color)
    {
        color = default;
        if (!TryParseHexBytes(value, out var red, out var green, out var blue, out var alpha))
        {
            return false;
        }

        color = new Vector4(
            red / 255f,
            green / 255f,
            blue / 255f,
            alpha / 255f);
        return true;
    }

    public static bool TryParseHexBytes(string? value, out byte red, out byte green, out byte blue, out byte alpha)
    {
        red = 0;
        green = 0;
        blue = 0;
        alpha = 255;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
            || !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
            || !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
        {
            return false;
        }

        return hex.Length == 6
               || byte.TryParse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha);
    }

    public static int ComputeRgbDistanceSquared(byte leftR, byte leftG, byte leftB, byte rightR, byte rightG, byte rightB)
    {
        var deltaR = leftR - rightR;
        var deltaG = leftG - rightG;
        var deltaB = leftB - rightB;
        return (deltaR * deltaR) + (deltaG * deltaG) + (deltaB * deltaB);
    }
}

