using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class SplashScreenLayoutPicker
{
    private const float Padding = 12f;
    private const float Rounding = 5f;
    private const float HeaderGap = 8f;
    private const float CloseButtonSize = 18f;

    public static SplashScreenActionRequest? Draw(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        IReadOnlyList<SplashScreenActionItem> layouts,
        float scale,
        out bool hovered)
    {
        if (!EditorInputUtility.HasArea(min, max))
        {
            hovered = false;
            return null;
        }

        hovered = EditorInputUtility.IsMouseInside(min, max);
        float rounding = Rounding * scale;
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.WindowBg, 0.98f)),
            rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Border, 0.82f)),
            rounding,
            ImDrawFlags.None,
            MathF.Max(1f, scale));

        float padding = Padding * scale;
        Vector2 contentMin = min + new Vector2(padding, padding);
        Vector2 contentMax = max - new Vector2(padding, padding);
        drawList.AddText(
            contentMin,
            ImGui.GetColorU32(EditorColors.Text),
            "Open Layout");
        SplashScreenActionRequest? closeRequest = DrawCloseButton(drawList, contentMin, contentMax, scale, out bool closeHovered);
        hovered |= closeHovered;

        Vector2 listMin = contentMin + new Vector2(0f, ImGui.GetTextLineHeight() + (HeaderGap * scale));
        if (layouts.Count == 0)
        {
            drawList.AddText(
                listMin,
                ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.TextDisabled, 0.76f)),
                "No saved layouts.");
            return closeRequest;
        }

        SplashScreenActionRequest? request = SplashScreenActionList.Draw(
            drawList,
            listMin,
            contentMax,
            [new SplashScreenActionSection(string.Empty, layouts)],
            scale,
            out bool listHovered);
        hovered |= listHovered;
        return closeRequest ?? request;
    }

    private static SplashScreenActionRequest? DrawCloseButton(
        ImDrawListPtr drawList,
        Vector2 contentMin,
        Vector2 contentMax,
        float scale,
        out bool hovered)
    {
        float size = CloseButtonSize * scale;
        float headerHeight = MathF.Max(ImGui.GetTextLineHeight(), size);
        Vector2 min = new(contentMax.X - size, contentMin.Y + ((headerHeight - size) * 0.5f));
        Vector2 max = min + new Vector2(size);
        hovered = EditorInputUtility.IsMouseInside(min, max);
        if (hovered)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Button, 0.55f)), 4f * scale);
        }

        string iconText = FontAwesomeIcon.Times.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText(
                min + ((new Vector2(size) - iconSize) * 0.5f),
                ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.TextDisabled, hovered ? 0.95f : 0.74f)),
                iconText);
        }

        return EditorInputUtility.IsMouseClickedInside(min, max)
            ? new SplashScreenActionRequest(SplashScreenActionKind.CloseLayouts)
            : null;
    }
}

