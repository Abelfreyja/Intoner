using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Components;

internal static class EditorScrollListOverlay
{
    private const float EdgeCueHeight = 28f;
    private const float EdgeCueMaxAlpha = 0.72f;
    private const float ScrollbarInset = 3f;
    private const float ScrollbarIdleWidth = 2f;
    private const float ScrollbarHoverWidth = 5f;
    private const float ScrollbarOverlayHitWidth = 14f;
    private const float ScrollbarMinThumbHeight = 24f;
    private const float EdgeCueArrowWidth = 7f;
    private const float EdgeCueArrowHeight = 4f;
    private const string ScrollbarId = "##editorScrollListScrollbar";

    private static uint _dragId;
    private static float _dragOffset;

    public static void Draw(EditorScrollList.ResolvedOptions options)
    {
        float scrollY = ImGui.GetScrollY();
        float scrollMaxY = ImGui.GetScrollMaxY();
        if (scrollMaxY <= 0.5f)
        {
            return;
        }

        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        if (max.X <= min.X || max.Y <= min.Y)
        {
            return;
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        try
        {
            if (options.ScrollbarLayout == EditorScrollListScrollbarLayout.Overlay)
            {
                ScrollbarGeometry scrollbar = ResolveScrollbarGeometry(min, max, scrollY, scrollMaxY, options);
                HandleScrollbarInput(scrollbar, scrollMaxY);
                scrollY = ImGui.GetScrollY();
                scrollbar = ResolveScrollbarGeometry(min, max, scrollY, scrollMaxY, options);
                DrawEdgeCues(drawList, min, max, scrollY, scrollMaxY, options);
                DrawScrollbar(drawList, scrollbar, options);
                return;
            }

            DrawEdgeCues(drawList, min, max, scrollY, scrollMaxY, options);
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    private static void DrawEdgeCues(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float scrollY,
        float scrollMaxY,
        EditorScrollList.ResolvedOptions options)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float edgeHeight = MathF.Min(EdgeCueHeight * scale, (max.Y - min.Y) * 0.35f);
        if (edgeHeight <= 0f)
        {
            return;
        }

        float topAlpha = Math.Clamp(scrollY / edgeHeight, 0f, 1f) * EdgeCueMaxAlpha;
        float bottomAlpha = Math.Clamp((scrollMaxY - scrollY) / edgeHeight, 0f, 1f) * EdgeCueMaxAlpha;
        DrawEdgeCue(drawList, min, max, edgeHeight, topAlpha, true, options);
        DrawEdgeCue(drawList, min, max, edgeHeight, bottomAlpha, false, options);
    }

    private static void DrawEdgeCue(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float edgeHeight,
        float alpha,
        bool top,
        EditorScrollList.ResolvedOptions options)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        float maxCornerRadius = MathF.Min(
            options.Rounding,
            MathF.Min((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f));
        ImDrawFlags leftCorner = top ? ImDrawFlags.RoundCornersTopLeft : ImDrawFlags.RoundCornersBottomLeft;
        ImDrawFlags rightCorner = top ? ImDrawFlags.RoundCornersTopRight : ImDrawFlags.RoundCornersBottomRight;
        float leftCornerRadius = (options.CornerFlags & leftCorner) != ImDrawFlags.None ? maxCornerRadius : 0f;
        float rightCornerRadius = (options.CornerFlags & rightCorner) != ImDrawFlags.None ? maxCornerRadius : 0f;
        DrawRoundedEdgeCueGradient(
            drawList,
            min,
            max,
            edgeHeight,
            alpha,
            top,
            options.EdgeColor,
            leftCornerRadius,
            rightCornerRadius);

        if (options.ShowEdgeCueArrow)
        {
            DrawEdgeCueArrow(drawList, min, max, alpha, top);
        }
    }

    private static void DrawRoundedEdgeCueGradient(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float edgeHeight,
        float alpha,
        bool top,
        Vector4 edgeColor,
        float leftCornerRadius,
        float rightCornerRadius)
    {
        float capHeight = MathF.Min(MathF.Max(leftCornerRadius, rightCornerRadius), edgeHeight);
        if (capHeight > 0f)
        {
            DrawRoundedEdgeCueCap(
                drawList,
                min,
                max,
                edgeHeight,
                alpha,
                top,
                edgeColor,
                leftCornerRadius,
                rightCornerRadius,
                capHeight);
        }

        if (capHeight >= edgeHeight)
        {
            return;
        }

        float capAlpha = alpha * (1f - (capHeight / edgeHeight));
        uint capColor = ImGui.GetColorU32(ThemeColors.WithAlpha(edgeColor, capAlpha));
        uint clear = ImGui.GetColorU32(ThemeColors.WithAlpha(edgeColor, 0f));
        Vector2 gradientMin = top
            ? new Vector2(min.X, min.Y + capHeight)
            : new Vector2(min.X, max.Y - edgeHeight);
        Vector2 gradientMax = top
            ? new Vector2(max.X, min.Y + edgeHeight)
            : new Vector2(max.X, max.Y - capHeight);
        drawList.AddRectFilledMultiColor(
            gradientMin,
            gradientMax,
            top ? capColor : clear,
            top ? capColor : clear,
            top ? clear : capColor,
            top ? clear : capColor);
    }

    private static void DrawRoundedEdgeCueCap(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float edgeHeight,
        float alpha,
        bool top,
        Vector4 edgeColor,
        float leftCornerRadius,
        float rightCornerRadius,
        float capHeight)
    {
        float capMinY = top ? min.Y : max.Y - capHeight;
        int segmentCount = Math.Max(1, (int)MathF.Ceiling(capHeight));
        for (int segmentIndex = 0; segmentIndex < segmentCount; ++segmentIndex)
        {
            float segmentTop = capMinY + ((capHeight * segmentIndex) / segmentCount);
            float segmentBottom = capMinY + ((capHeight * (segmentIndex + 1)) / segmentCount);
            float topDistance = top ? segmentTop - min.Y : max.Y - segmentTop;
            float bottomDistance = top ? segmentBottom - min.Y : max.Y - segmentBottom;
            float leftTopInset = ResolveRoundedCornerInset(leftCornerRadius, topDistance);
            float rightTopInset = ResolveRoundedCornerInset(rightCornerRadius, topDistance);
            float leftBottomInset = ResolveRoundedCornerInset(leftCornerRadius, bottomDistance);
            float rightBottomInset = ResolveRoundedCornerInset(rightCornerRadius, bottomDistance);
            float segmentAlpha = alpha * (1f - (((topDistance + bottomDistance) * 0.5f) / edgeHeight));
            uint color = ImGui.GetColorU32(ThemeColors.WithAlpha(edgeColor, segmentAlpha));
            drawList.AddQuadFilled(
                new Vector2(min.X + leftTopInset, segmentTop),
                new Vector2(max.X - rightTopInset, segmentTop),
                new Vector2(max.X - rightBottomInset, segmentBottom),
                new Vector2(min.X + leftBottomInset, segmentBottom),
                color);
        }
    }

    private static float ResolveRoundedCornerInset(float radius, float edgeDistance)
    {
        float distanceToCenter = radius - Math.Clamp(edgeDistance, 0f, radius);
        return radius - MathF.Sqrt(MathF.Max(0f, (radius * radius) - (distanceToCenter * distanceToCenter)));
    }

    private static void DrawEdgeCueArrow(ImDrawListPtr drawList, Vector2 min, Vector2 max, float alpha, bool top)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = EdgeCueArrowWidth * scale;
        float height = EdgeCueArrowHeight * scale;
        float centerX = (min.X + max.X) * 0.5f;
        float centerY = top
            ? min.Y + (10f * scale)
            : max.Y - (10f * scale);
        Vector2 left = new(centerX - width, top ? centerY + height : centerY - height);
        Vector2 center = new(centerX, top ? centerY - height : centerY + height);
        Vector2 right = new(centerX + width, top ? centerY + height : centerY - height);
        uint color = ImGui.GetColorU32(ThemeColors.WithAlpha(ThemeColors.Text, Math.Clamp(alpha, 0f, 0.72f)));
        float thickness = MathF.Max(1f, 1.25f * scale);
        drawList.AddLine(left, center, color, thickness);
        drawList.AddLine(center, right, color, thickness);
    }

    private static ScrollbarGeometry ResolveScrollbarGeometry(
        Vector2 min,
        Vector2 max,
        float scrollY,
        float scrollMaxY,
        EditorScrollList.ResolvedOptions options)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = max.Y - min.Y;
        float inset = MathF.Max(ScrollbarInset * scale, options.Rounding * 0.35f);
        float trackHeight = MathF.Max(1f, height - (inset * 2f));
        float width = ResolveScrollbarWidth(false);
        float xMax = max.X - inset;
        float xMin = xMax - width;
        float visibleRatio = Math.Clamp(height / (height + scrollMaxY), 0.05f, 1f);
        float thumbHeight = MathF.Min(trackHeight, MathF.Max(ScrollbarMinThumbHeight * scale, trackHeight * visibleRatio));
        float travel = MathF.Max(1f, trackHeight - thumbHeight);
        float thumbTop = min.Y + inset + (travel * Math.Clamp(scrollY / scrollMaxY, 0f, 1f));
        return new(
            new Vector2(xMin, min.Y + inset),
            new Vector2(xMax, max.Y - inset),
            new Vector2(xMin, thumbTop),
            new Vector2(xMax, thumbTop + thumbHeight),
            new Vector2(max.X - (ScrollbarOverlayHitWidth * scale), min.Y),
            max,
            travel,
            thumbHeight);
    }

    private static float ResolveScrollbarWidth(bool hovered)
        => (hovered ? ScrollbarHoverWidth : ScrollbarIdleWidth) * ImGuiHelpers.GlobalScale;

    private static void HandleScrollbarInput(ScrollbarGeometry scrollbar, float scrollMaxY)
    {
        uint id = ImGui.GetID(ScrollbarId);
        if (_dragId != 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            ClearScrollbarDrag();
        }

        bool hovered = ImGui.IsMouseHoveringRect(scrollbar.HitMin, scrollbar.HitMax);
        Vector2 mouse = ImGui.GetIO().MousePos;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _dragId = id;
            _dragOffset = mouse.Y >= scrollbar.ThumbMin.Y && mouse.Y <= scrollbar.ThumbMax.Y
                ? mouse.Y - scrollbar.ThumbMin.Y
                : scrollbar.ThumbHeight * 0.5f;
            ApplyScrollbarDrag(mouse.Y, scrollbar, scrollMaxY);
        }

        if (_dragId != id)
        {
            return;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            ApplyScrollbarDrag(mouse.Y, scrollbar, scrollMaxY);
        }
        else
        {
            ClearScrollbarDrag();
        }
    }

    private static void ClearScrollbarDrag()
    {
        _dragId = 0;
        _dragOffset = 0f;
    }

    private static void ApplyScrollbarDrag(float mouseY, ScrollbarGeometry scrollbar, float scrollMaxY)
    {
        float thumbTop = Math.Clamp(
            mouseY - _dragOffset,
            scrollbar.TrackMin.Y,
            scrollbar.TrackMax.Y - scrollbar.ThumbHeight);
        float ratio = Math.Clamp((thumbTop - scrollbar.TrackMin.Y) / scrollbar.Travel, 0f, 1f);
        ImGui.SetScrollY(scrollMaxY * ratio);
    }

    private static void DrawScrollbar(
        ImDrawListPtr drawList,
        ScrollbarGeometry scrollbar,
        EditorScrollList.ResolvedOptions options)
    {
        bool hovered = ImGui.IsMouseHoveringRect(scrollbar.HitMin, scrollbar.HitMax)
            || _dragId == ImGui.GetID(ScrollbarId);
        float width = ResolveScrollbarWidth(hovered);
        float xMax = scrollbar.TrackMax.X;
        Vector2 trackMin = new(xMax - width, scrollbar.TrackMin.Y);
        Vector2 trackMax = new(xMax, scrollbar.TrackMax.Y);
        Vector2 thumbMin = new(xMax - width, scrollbar.ThumbMin.Y);
        Vector2 thumbMax = new(xMax, scrollbar.ThumbMax.Y);
        float rounding = width * 0.5f;

        Vector4 trackColor = ThemeColors.Color(1f, 1f, 1f, hovered ? 0.07f : 0.035f);
        Vector4 thumbColor = ThemeColors.WithAlpha(options.Accent, hovered ? 0.74f : 0.46f);
        drawList.AddRectFilled(
            trackMin,
            trackMax,
            ImGui.GetColorU32(trackColor),
            rounding);
        drawList.AddRectFilled(
            thumbMin,
            thumbMax,
            ImGui.GetColorU32(thumbColor),
            rounding);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ScrollbarGeometry(
        Vector2 TrackMin,
        Vector2 TrackMax,
        Vector2 ThumbMin,
        Vector2 ThumbMax,
        Vector2 HitMin,
        Vector2 HitMax,
        float Travel,
        float ThumbHeight);
}
