using System.Numerics;
using PreviewGeometryReader = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.Preview.Rendering;

internal readonly struct ThumbnailTriangle(
    bool isVisible,
    Vector2 screen0,
    Vector2 screen1,
    Vector2 screen2,
    float depth0,
    float depth1,
    float depth2,
    float lighting,
    Vector3 untexturedDiffuseColor,
    PreviewGeometryReader.PreviewTexture? diffuseTexture,
    bool applyAlphaClip,
    bool enableTransparency,
    float transparency,
    float sortDepth,
    Vector2 uv0,
    Vector2 uv1,
    Vector2 uv2)
{
    public bool IsVisible { get; } = isVisible;
    public Vector2 Screen0 { get; } = screen0;
    public Vector2 Screen1 { get; } = screen1;
    public Vector2 Screen2 { get; } = screen2;
    public float Depth0 { get; } = depth0;
    public float Depth1 { get; } = depth1;
    public float Depth2 { get; } = depth2;
    public float Lighting { get; } = lighting;
    public Vector3 UntexturedDiffuseColor { get; } = untexturedDiffuseColor;
    public PreviewGeometryReader.PreviewTexture? DiffuseTexture { get; } = diffuseTexture;
    public bool ApplyAlphaClip { get; } = applyAlphaClip;
    public bool EnableTransparency { get; } = enableTransparency;
    public float Transparency { get; } = transparency;
    public float SortDepth { get; } = sortDepth;
    public Vector2 Uv0 { get; } = uv0;
    public Vector2 Uv1 { get; } = uv1;
    public Vector2 Uv2 { get; } = uv2;
}
