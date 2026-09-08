using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Intoner.UI.Components;

internal static class EditorImage
{
    public static void DrawCover(
        ImDrawListPtr drawList,
        IDalamudTextureWrap texture,
        Vector2 min,
        Vector2 max,
        float rounding = 0f,
        float alpha = 1f,
        bool drawBorder = true)
    {
        Vector2 uvMin = Vector2.Zero;
        Vector2 uvMax = Vector2.One;
        float targetWidth = max.X - min.X;
        float targetHeight = max.Y - min.Y;
        if (texture.Width > 0 && texture.Height > 0 && targetWidth > 0f && targetHeight > 0f)
        {
            float sourceAspect = texture.Width / (float)texture.Height;
            float targetAspect = targetWidth / targetHeight;
            if (sourceAspect > targetAspect)
            {
                float visibleWidth = targetAspect / sourceAspect;
                uvMin.X = (1f - visibleWidth) * 0.5f;
                uvMax.X = 1f - uvMin.X;
            }
            else if (sourceAspect < targetAspect)
            {
                float visibleHeight = sourceAspect / targetAspect;
                uvMin.Y = (1f - visibleHeight) * 0.5f;
                uvMax.Y = 1f - uvMin.Y;
            }
        }

        drawList.AddImageRounded(
            texture.Handle,
            min,
            max,
            uvMin,
            uvMax,
            ImGui.GetColorU32(Vector4.One with { W = alpha }),
            rounding);
        if (drawBorder)
        {
            drawList.AddRect(
                min,
                max,
                ImGui.GetColorU32(ThemeColors.Border with { W = 0.58f * alpha }),
                rounding,
                ImDrawFlags.None,
                MathF.Max(ImGuiHelpers.GlobalScale, 1f));
        }
    }
}
