using Dalamud.Interface.Textures.TextureWraps;
using System.Numerics;

namespace Intoner.Objects.Models;

internal enum ObjectPreviewBackgroundStyle
{
    White,
    DarkBlue,
}

internal enum ObjectCatalogPreviewMode
{
    Detail,
    Thumbnail,
}

internal static class ObjectPreviewBackgroundPalette
{
    public static (Vector3 Top, Vector3 Bottom) GetGradient(ObjectPreviewBackgroundStyle style)
        => style switch
        {
            ObjectPreviewBackgroundStyle.White => (new Vector3(0.93f, 0.94f, 0.96f), new Vector3(0.84f, 0.86f, 0.90f)),
            ObjectPreviewBackgroundStyle.DarkBlue => (new Vector3(0.10f, 0.11f, 0.14f), new Vector3(0.05f, 0.06f, 0.08f)),
            _ => (new Vector3(0.10f, 0.11f, 0.14f), new Vector3(0.05f, 0.06f, 0.08f)),
        };

    public static Vector4 GetPlaceholderFill(ObjectPreviewBackgroundStyle style)
    {
        Vector3 top = GetGradient(style).Top;
        return style switch
        {
            ObjectPreviewBackgroundStyle.White => new Vector4(top, 1f),
            ObjectPreviewBackgroundStyle.DarkBlue => new Vector4(top, 0.34f),
            _ => new Vector4(top, 0.34f),
        };
    }

    public static Vector4 GetSwatchFill(ObjectPreviewBackgroundStyle style)
        => new(GetGradient(style).Top, 1f);
}

internal readonly record struct ObjectPreviewCameraState(float Yaw, float Pitch, float Zoom, ObjectPreviewBackgroundStyle BackgroundStyle);

internal readonly record struct ObjectCatalogPreviewRequest(
    int Width,
    int Height,
    int YawHundredths,
    int PitchHundredths,
    int ZoomHundredths,
    ObjectPreviewBackgroundStyle BackgroundStyle,
    ObjectCatalogPreviewMode Mode = ObjectCatalogPreviewMode.Detail)
{
    public ObjectPreviewCameraState ToCameraState()
        => new(YawHundredths / 100f, PitchHundredths / 100f, ZoomHundredths / 100f, BackgroundStyle);
}

internal readonly record struct ObjectCatalogPreviewResult(
    IDalamudTextureWrap? Texture,
    bool IsLoading,
    string? Error);

