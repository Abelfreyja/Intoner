using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Rendering.Drawing;

internal readonly record struct DrawContext(
    Vector2 ViewportPos,
    Vector2 ViewportSize,
    Matrix4x4 ViewProjection,
    Matrix4x4 ViewMatrix,
    float NearPlane,
    float AlphaMultiplier,
    DrawLayer Layer)
{
    public static bool TryCaptureEditor(
        Vector2 viewportPos,
        Vector2 viewportSize,
        DrawLayer layer,
        float alphaMultiplier,
        out DrawContext context)
    {
        context = default;
        if (!ObjectMathUtility.IsFinite(viewportPos)
            || !ObjectMathUtility.IsFinite(viewportSize)
            || viewportSize.X <= 0f
            || viewportSize.Y <= 0f
            || !ObjectViewportProjectionUtility.TryGetEditorCameraProjection(out var viewProjection, out var viewMatrix, out var nearPlane))
        {
            return false;
        }

        context = new DrawContext(
            viewportPos,
            viewportSize,
            viewProjection,
            viewMatrix,
            nearPlane,
            NormalizeAlphaMultiplier(alphaMultiplier),
            layer);
        return true;
    }

    private static float NormalizeAlphaMultiplier(float value)
        => !float.IsFinite(value)
            ? 1f
            : Math.Clamp(value, 0f, 1f);
}

