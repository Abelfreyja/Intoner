using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class EditorLayout
{
    internal static bool TryGetWindowBodyArea(out EditorOverlayArea area)
    {
        var style = ImGui.GetStyle();
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var contentMin = ImGui.GetWindowContentRegionMin();
        var titleBarHeight = MathF.Max(0f, contentMin.Y - style.WindowPadding.Y);
        Vector2 backgroundMin = new(windowPos.X, windowPos.Y + titleBarHeight);
        Vector2 backgroundMax = windowPos + windowSize;
        var backgroundSize = backgroundMax - backgroundMin;
        if (backgroundSize.X < 4f || backgroundSize.Y < 4f)
        {
            area = default;
            return false;
        }

        area = new EditorOverlayArea(
            backgroundMin,
            backgroundMax,
            MathF.Max(0f, style.WindowRounding),
            titleBarHeight > 0f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersAll);
        return true;
    }

    public static float ResolveDividerThickness()
        => MathF.Max(1f, ImGui.GetStyle().FrameBorderSize + 1f);

    public static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;

    public static Vector2 ScaledVector(float x, float y)
        => new(Scaled(x), Scaled(y));

    public static float Positive(float value)
        => MathF.Max(1f, value);

    public static float ResolveActionStripWidth(float actionEdge, int actionCount)
        => (actionEdge * actionCount) + (ImGui.GetStyle().ItemSpacing.X * MathF.Max(0, actionCount - 1));

    public static float ResolveRemainingRegionHeight(float minHeight = 1f, float bottomInset = 0f)
        => MathF.Max(minHeight, ImGui.GetContentRegionAvail().Y - bottomInset);

    public static float ResolveScrollableCardInnerHeight(float cardHeight, Vector2 padding, float contentStartY)
        => Positive(cardHeight - (padding.Y * 2f) - (ImGui.GetCursorPosY() - contentStartY) - ImGui.GetStyle().ItemSpacing.Y);

    public static Vector2 ResolveObjectListCardPadding()
        => ScaledVector(10f, 8f);

    public static float ResolveObjectListItemSpacingY()
        => Scaled(2f);

    public static float ResolveObjectListEntryHeight()
        => MathF.Max(Scaled(52f), (ImGui.GetTextLineHeight() * 2f) + Scaled(20f));
}
