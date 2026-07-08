using System.Numerics;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailPixels
{
    public static void WritePixel(int pixelIndex, byte[] pixels, byte red, byte green, byte blue)
    {
        pixels[pixelIndex] = red;
        pixels[pixelIndex + 1] = green;
        pixels[pixelIndex + 2] = blue;
        pixels[pixelIndex + 3] = 255;
    }

    public static void WritePixel(byte[] pixels, int pixelIndex, Vector3 color)
    {
        WritePixel(
            pixelIndex,
            pixels,
            ToByteColorComponent(color.X),
            ToByteColorComponent(color.Y),
            ToByteColorComponent(color.Z));
    }

    public static void BlendPixel(int pixelIndex, byte[] pixels, byte red, byte green, byte blue, byte alpha)
    {
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            WritePixel(pixelIndex, pixels, red, green, blue);
            return;
        }

        var inverseAlpha = 255 - alpha;
        pixels[pixelIndex] = (byte)(((red * alpha) + (pixels[pixelIndex] * inverseAlpha)) / 255);
        pixels[pixelIndex + 1] = (byte)(((green * alpha) + (pixels[pixelIndex + 1] * inverseAlpha)) / 255);
        pixels[pixelIndex + 2] = (byte)(((blue * alpha) + (pixels[pixelIndex + 2] * inverseAlpha)) / 255);
        pixels[pixelIndex + 3] = 255;
    }

    public static byte ToByteColorComponent(float value)
        => (byte)Math.Clamp(value * 255f, 0f, 255f);

    public static byte ToByteColorComponentByteScale(float value)
        => (byte)Math.Clamp(value, 0f, 255f);
}
