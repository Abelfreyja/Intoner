using Dalamud.Interface.Textures.TextureWraps;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview;

internal static class PreviewRender
{
    internal enum Mode
    {
        Detail,
        Thumbnail,
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Request(
        int Width,
        int Height,
        int YawHundredths,
        int PitchHundredths,
        int ZoomHundredths,
        BackgroundStyle BackgroundStyle,
        Mode Mode = Mode.Detail)
    {
        public CameraState ToCameraState()
            => new(YawHundredths / 100f, PitchHundredths / 100f, ZoomHundredths / 100f, BackgroundStyle);
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Result(
        IDalamudTextureWrap? Texture,
        bool IsLoading,
        string? Error);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct CameraState(float Yaw, float Pitch, float Zoom, BackgroundStyle BackgroundStyle);

    internal enum BackgroundStyle
    {
        White,
        DarkBlue,
    }

    internal static class BackgroundPalette
    {
        public static (Vector3 Top, Vector3 Bottom) GetGradient(BackgroundStyle style)
            => style switch
            {
                BackgroundStyle.White => (new Vector3(0.93f, 0.94f, 0.96f), new Vector3(0.84f, 0.86f, 0.90f)),
                BackgroundStyle.DarkBlue => (new Vector3(0.10f, 0.11f, 0.14f), new Vector3(0.05f, 0.06f, 0.08f)),
                _ => (new Vector3(0.10f, 0.11f, 0.14f), new Vector3(0.05f, 0.06f, 0.08f)),
            };

        public static Vector4 GetPlaceholderFill(BackgroundStyle style)
        {
            Vector3 top = GetGradient(style).Top;
            return style switch
            {
                BackgroundStyle.White => new Vector4(top, 1f),
                BackgroundStyle.DarkBlue => new Vector4(top, 0.34f),
                _ => new Vector4(top, 0.34f),
            };
        }

        public static Vector4 GetSwatchFill(BackgroundStyle style)
            => new(GetGradient(style).Top, 1f);
    }
}
