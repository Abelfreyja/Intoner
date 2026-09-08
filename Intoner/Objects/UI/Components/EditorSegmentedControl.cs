using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorSegmentedControl
{
    private const float SegmentSpacing = 2f;

    public static float Spacing => Scaled(SegmentSpacing);

    public static float ResolveSegmentWidth(float totalWidth, int count)
        => count <= 0
            ? 0f
            : MathF.Max(1f, (totalWidth - (Spacing * (count - 1))) / count);

    public static bool DrawSegment(
        string id,
        FontAwesomeIcon? icon,
        string label,
        string? count,
        bool selected,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        int index,
        int segmentCount,
        float rounding = 7f,
        bool showSelectionIndicator = true)
        => DrawSegmentCore(
            id,
            icon,
            label,
            count,
            selected,
            enabled,
            tooltip,
            accent,
            size,
            index,
            segmentCount,
            action: false,
            compact: false,
            rounding: rounding,
            showSelectionIndicator: showSelectionIndicator,
            disabledIconColor: null);

    public static bool DrawCompactSegment(
        string id,
        string label,
        string? count,
        bool selected,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        int index,
        int segmentCount)
        => DrawSegmentCore(
            id,
            icon: null,
            label,
            count,
            selected,
            enabled,
            tooltip,
            accent,
            size,
            index,
            segmentCount,
            action: false,
            compact: true,
            rounding: 4f,
            showSelectionIndicator: false,
            disabledIconColor: null);

    public static bool DrawCompactActionSegment(
        string id,
        FontAwesomeIcon icon,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        int index,
        int segmentCount,
        bool emphasized = true,
        Vector4? disabledIconColor = null)
        => DrawSegmentCore(
            id,
            icon,
            string.Empty,
            null,
            selected: emphasized,
            enabled,
            tooltip,
            accent,
            size,
            index,
            segmentCount,
            action: true,
            compact: true,
            rounding: 4f,
            showSelectionIndicator: false,
            disabledIconColor);

    public static bool DrawActionSegment(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        int index,
        int segmentCount,
        float rounding = 5f)
        => DrawSegmentCore(
            id,
            icon,
            label,
            null,
            selected: false,
            enabled,
            tooltip,
            accent,
            size,
            index,
            segmentCount,
            action: true,
            compact: false,
            rounding: rounding,
            showSelectionIndicator: false,
            disabledIconColor: null);

    private static bool DrawSegmentCore(
        string id,
        FontAwesomeIcon? icon,
        string label,
        string? count,
        bool selected,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        int index,
        int segmentCount,
        bool action,
        bool compact,
        float rounding,
        bool showSelectionIndicator,
        Vector4? disabledIconColor)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            clicked = ImGui.InvisibleButton(id, size);
        }

        bool hovered = enabled && ImGui.IsItemHovered();
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        DrawFrame(
            drawList,
            min,
            max,
            selected,
            enabled,
            hovered,
            ImGui.IsItemActive(),
            accent,
            ResolveRoundingFlags(index, segmentCount),
            action,
            compact,
            rounding);
        DrawContent(
            drawList,
            min,
            max,
            icon,
            label,
            count,
            selected || action,
            enabled,
            hovered,
            accent,
            compact,
            disabledIconColor);
        if (selected && !compact && showSelectionIndicator)
        {
            DrawSelectionIndicator(drawList, min, max, accent, enabled);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(tooltip))
        {
            IntonerTooltip.Attach(tooltip);
        }

        return clicked && enabled;
    }

    private static void DrawFrame(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool enabled,
        bool hovered,
        bool active,
        Vector4 accent,
        ImDrawFlags roundingFlags,
        bool action,
        bool compact,
        float rounding)
    {
        rounding = Scaled(rounding);
        if (compact)
        {
            Vector4 baseFill = ThemeColors.ButtonDefault with { W = 0.44f };
            Vector4 compactFill;
            Vector4 compactBorder;
            if (selected)
            {
                compactFill = accent with { W = hovered ? 0.28f : 0.20f };
                compactBorder = accent with { W = hovered ? 0.66f : 0.48f };
            }
            else
            {
                compactFill = hovered
                    ? Vector4.Lerp(baseFill, accent, 0.10f) with { W = 0.52f }
                    : baseFill;
                compactBorder = ThemeColors.Border with { W = hovered ? 0.46f : 0.32f };
            }

            if (!enabled)
            {
                compactFill.W *= 0.48f;
                compactBorder.W *= 0.50f;
            }

            drawList.AddRectFilled(min, max, ImGui.GetColorU32(compactFill), rounding, roundingFlags);
            drawList.AddRect(min, max, ImGui.GetColorU32(compactBorder), rounding, roundingFlags, Scaled(1f));
            return;
        }

        Vector4 fill = action
            ? ResolveActionFillColor(enabled, hovered, active, accent)
            : ResolveFillColor(selected, enabled, hovered, active, accent);
        Vector4 border = action
            ? ResolveActionBorderColor(enabled, hovered, accent)
            : ResolveBorderColor(selected, enabled, hovered, accent);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding, roundingFlags);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, roundingFlags, Scaled(1f));
    }

    private static void DrawContent(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        FontAwesomeIcon? icon,
        string label,
        string? count,
        bool selected,
        bool enabled,
        bool hovered,
        Vector4 accent,
        bool compact,
        Vector4? disabledIconColor)
    {
        label ??= string.Empty;
        if (icon is { } iconOnly && label.Length == 0 && string.IsNullOrEmpty(count))
        {
            float iconAlpha = hovered ? 1f : 0.88f;
            Vector4 iconColor = enabled
                ? accent with { W = iconAlpha }
                : disabledIconColor ?? (ThemeColors.TextDisabled with { W = 0.42f });
            EditorIcon.DrawCentered(drawList, iconOnly, min, max, iconColor, 0.82f);
            return;
        }

        string countText = count ?? string.Empty;
        EditorIcon.Metrics iconMetrics = icon is { } value
            ? EditorIcon.Measure(value, 0.88f)
            : default;
        Vector2 countTextSize = countText.Length == 0 ? Vector2.Zero : ImGui.CalcTextSize(countText);
        float countWidth = countTextSize.X;
        float gap = Scaled(compact ? 4f : 6f);
        bool gapAfterIcon = iconMetrics.Size.X > 0f && (label.Length > 0 || countWidth > 0f);
        bool gapBeforeCount = label.Length > 0 && countWidth > 0f;
        float fixedWidth = iconMetrics.Size.X + countWidth;
        if (gapAfterIcon)
        {
            fixedWidth += gap;
        }

        if (gapBeforeCount)
        {
            fixedWidth += gap;
        }

        float availableLabelWidth = MathF.Max(1f, max.X - min.X - fixedWidth - Scaled(compact ? 12f : 16f));
        string visibleLabel = EditorTextUtility.ClipTextToWidth(label, availableLabelWidth);
        Vector2 labelSize = ImGui.CalcTextSize(visibleLabel);
        float contentWidth = fixedWidth + labelSize.X;
        float contentX = min.X + MathF.Max(Scaled(8f), ((max.X - min.X) - contentWidth) * 0.5f);
        Vector4 textColor = ResolveTextColor(selected, enabled, hovered);

        if (icon is { } segmentIcon)
        {
            Vector4 iconColor = ThemeColors.TextDisabled with { W = 0.42f };
            if (enabled)
            {
                iconColor = accent with { W = selected ? 1f : 0.82f };
            }

            EditorIcon.Draw(
                drawList,
                segmentIcon,
                iconMetrics,
                new Vector2(contentX, CenterY(min.Y, max.Y, iconMetrics.Size.Y)),
                iconColor);
            contentX += iconMetrics.Size.X + (gapAfterIcon ? gap : 0f);
        }

        drawList.AddText(
            new Vector2(contentX, CenterY(min.Y, max.Y, labelSize.Y)),
            ImGui.GetColorU32(textColor),
            visibleLabel);
        contentX += labelSize.X;

        if (countWidth <= 0f)
        {
            return;
        }

        if (gapBeforeCount)
        {
            contentX += gap;
        }
        Vector4 countColor = ThemeColors.TextDisabled with { W = 0.42f };
        if (enabled)
        {
            countColor = selected
                ? ThemeColors.Text with { W = 0.78f }
                : ThemeColors.TextDisabled with { W = 0.72f };
        }

        drawList.AddText(
            new Vector2(
                contentX,
                CenterY(min.Y, max.Y, countTextSize.Y)),
            ImGui.GetColorU32(countColor),
            countText);
    }

    private static void DrawSelectionIndicator(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 accent,
        bool enabled)
    {
        float width = MathF.Min(Scaled(46f), MathF.Max(1f, (max.X - min.X) - Scaled(28f)));
        float height = MathF.Max(Scaled(2f), 1f);
        float centerX = (min.X + max.X) * 0.5f;
        float y = max.Y - Scaled(6f);
        drawList.AddRectFilled(
            new Vector2(centerX - (width * 0.5f), y),
            new Vector2(centerX + (width * 0.5f), y + height),
            ImGui.GetColorU32(accent with { W = enabled ? 0.92f : 0.32f }),
            height * 0.5f);
    }

    private static ImDrawFlags ResolveRoundingFlags(int index, int count)
    {
        if (count <= 1)
        {
            return ImDrawFlags.RoundCornersAll;
        }

        return index switch
        {
            0                                => ImDrawFlags.RoundCornersLeft,
            var last when last == count - 1 => ImDrawFlags.RoundCornersRight,
            _                                => ImDrawFlags.RoundCornersNone,
        };
    }

    private static Vector4 ResolveFillColor(bool selected, bool enabled, bool hovered, bool active, Vector4 accent)
    {
        if (!enabled)
        {
            return ThemeColors.ButtonDefault with { W = 0.18f };
        }

        if (selected)
        {
            float alpha = 0.26f;
            if (active)
            {
                alpha = 0.40f;
            }
            else if (hovered)
            {
                alpha = 0.35f;
            }

            return accent with { W = alpha };
        }

        return ThemeColors.ButtonDefault with { W = hovered ? 0.56f : 0.38f };
    }

    private static Vector4 ResolveBorderColor(bool selected, bool enabled, bool hovered, Vector4 accent)
    {
        if (!enabled)
        {
            return ThemeColors.Border with { W = 0.14f };
        }

        if (selected)
        {
            return accent with { W = hovered ? 0.70f : 0.54f };
        }

        return ThemeColors.Border with { W = hovered ? 0.42f : 0.26f };
    }

    private static Vector4 ResolveActionFillColor(bool enabled, bool hovered, bool active, Vector4 accent)
    {
        if (!enabled)
        {
            return ThemeColors.ButtonDefault with { W = 0.18f };
        }

        float alpha = 0.16f;
        if (active)
        {
            alpha = 0.34f;
        }
        else if (hovered)
        {
            alpha = 0.26f;
        }

        return Vector4.Lerp(ThemeColors.ButtonDefault, accent, alpha) with { W = 0.88f };
    }

    private static Vector4 ResolveActionBorderColor(bool enabled, bool hovered, Vector4 accent)
    {
        if (!enabled)
        {
            return ThemeColors.Border with { W = 0.14f };
        }

        return accent with { W = hovered ? 0.68f : 0.46f };
    }

    private static Vector4 ResolveTextColor(bool selected, bool enabled, bool hovered)
    {
        if (!enabled)
        {
            return ThemeColors.TextDisabled with { W = 0.48f };
        }

        if (selected)
        {
            return ThemeColors.Text with { W = 0.98f };
        }

        return hovered
            ? ThemeColors.Text with { W = 0.88f }
            : ThemeColors.TextDisabled with { W = 0.82f };
    }

    private static float CenterY(float top, float bottom, float height)
        => top + MathF.Max(0f, ((bottom - top) - height) * 0.5f);

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
