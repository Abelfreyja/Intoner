using System.Numerics;
using PreviewGeometryReader = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailTextureSampler
{
    private const float AlphaClipThreshold = 0.10f;
    private const float AlphaClipThresholdByte = AlphaClipThreshold * 255f;

    public static bool TrySampleTexturedColor(
        PreviewGeometryReader.PreviewTexture texture,
        Vector2 uv,
        float lighting,
        bool applyAlphaClip,
        float transparency,
        out byte red,
        out byte green,
        out byte blue,
        out byte alpha)
    {
        if (texture.Width <= 0 || texture.Height <= 0 || texture.RgbaPixels.Length < texture.Width * texture.Height * 4)
        {
            red = ThumbnailPixels.ToByteColorComponentByteScale(255f * lighting);
            green = red;
            blue = red;
            alpha = ThumbnailPixels.ToByteColorComponentByteScale(transparency * 255f);
            return true;
        }

        var wrappedU = WrapUv(uv.X);
        var wrappedV = WrapUv(uv.Y);
        var sampleX = wrappedU * (texture.Width - 1);
        var sampleY = wrappedV * (texture.Height - 1);

        var x0 = (int)MathF.Floor(sampleX);
        var y0 = (int)MathF.Floor(sampleY);
        var x1 = Math.Min(texture.Width - 1, x0 + 1);
        var y1 = Math.Min(texture.Height - 1, y0 + 1);
        var tx = sampleX - x0;
        var ty = sampleY - y0;
        var weightX0 = 1f - tx;
        var weightY0 = 1f - ty;
        var weight00 = weightX0 * weightY0;
        var weight10 = tx * weightY0;
        var weight01 = weightX0 * ty;
        var weight11 = tx * ty;
        var pixels = texture.RgbaPixels;
        var pixel00 = ((y0 * texture.Width) + x0) * 4;
        var pixel10 = ((y0 * texture.Width) + x1) * 4;
        var pixel01 = ((y1 * texture.Width) + x0) * 4;
        var pixel11 = ((y1 * texture.Width) + x1) * 4;
        var sampledAlpha = (pixels[pixel00 + 3] * weight00)
            + (pixels[pixel10 + 3] * weight10)
            + (pixels[pixel01 + 3] * weight01)
            + (pixels[pixel11 + 3] * weight11);
        if (applyAlphaClip && sampledAlpha < AlphaClipThresholdByte)
        {
            red = 0;
            green = 0;
            blue = 0;
            alpha = 0;
            return false;
        }

        red = ThumbnailPixels.ToByteColorComponentByteScale((
            (pixels[pixel00] * weight00)
          + (pixels[pixel10] * weight10)
          + (pixels[pixel01] * weight01)
          + (pixels[pixel11] * weight11)) * lighting);
        green = ThumbnailPixels.ToByteColorComponentByteScale((
            (pixels[pixel00 + 1] * weight00)
          + (pixels[pixel10 + 1] * weight10)
          + (pixels[pixel01 + 1] * weight01)
          + (pixels[pixel11 + 1] * weight11)) * lighting);
        blue = ThumbnailPixels.ToByteColorComponentByteScale((
            (pixels[pixel00 + 2] * weight00)
          + (pixels[pixel10 + 2] * weight10)
          + (pixels[pixel01 + 2] * weight01)
          + (pixels[pixel11 + 2] * weight11)) * lighting);
        alpha = ThumbnailPixels.ToByteColorComponentByteScale(sampledAlpha * transparency);
        return true;
    }

    private static float WrapUv(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        value -= MathF.Floor(value);
        return value < 0f ? value + 1f : value;
    }
}
