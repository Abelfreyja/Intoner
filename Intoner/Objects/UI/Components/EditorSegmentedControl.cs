using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal enum EditorSegmentBackground
{
    Default,
    Emphasized,
}

internal static class EditorSegmentedControl
{
    private enum SegmentStyle
    {
        Standard,
        Compact,
        Filter,
    }

    private const float SegmentSpacing = 2f;
    private const float FilterPadding = 7f;
    private const float ContentGap = 6f;

    public static float Spacing => Scaled(SegmentSpacing);

    public static float MeasureWidth(FontAwesomeIcon? icon, string label)
    {
        label ??= string.Empty;
        float iconWidth = icon is { } glyph ? EditorIcon.Measure(glyph, 0.88f).Size.X : 0f;
        float gap = iconWidth > 0f && label.Length > 0 ? Scaled(ContentGap) : 0f;
        return iconWidth + gap + ImGui.CalcTextSize(label).X + Scaled(16f);
    }

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
        bool showSelectionIndicator = true,
        EditorSegmentBackground background = EditorSegmentBackground.Default)
        => DrawSegmentCore(id, icon, label, count, selected, enabled, tooltip, accent, size, index, segmentCount,
            action: false, style: SegmentStyle.Standard, rounding: rounding,
            showSelectionIndicator: showSelectionIndicator, disabledIconColor: null, background: background);

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
        => DrawSegmentCore(id, null, label, count, selected, enabled, tooltip, accent, size, index, segmentCount,
            action: false, style: SegmentStyle.Compact, rounding: 4f,
            showSelectionIndicator: false, disabledIconColor: null);

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
        => DrawSegmentCore(id, icon, string.Empty, null, emphasized, enabled, tooltip, accent, size, index, segmentCount,
            action: true, style: SegmentStyle.Compact, rounding: 4f,
            showSelectionIndicator: false, disabledIconColor: disabledIconColor);

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
        => DrawSegmentCore(id, icon, label, null, false, enabled, tooltip, accent, size, index, segmentCount,
            action: true, style: SegmentStyle.Standard, rounding: rounding,
            showSelectionIndicator: false, disabledIconColor: null);

    public static float MeasureFilterWidth(string label, string? count = null)
        => string.IsNullOrEmpty(count)
            ? ImGui.CalcTextSize(label).X + Scaled(FilterPadding * 2f)
            : EditorBadgeRenderer.MeasureWidth(EditorBadge.Label(label)) + EditorBadgeRenderer.MeasureWidth(new EditorBadge(count, EditorBadgeStyle.Muted));

    public static bool DrawFilterSegment(string id, string label, string? count, bool selected, Vector4 accent, Vector2 size)
        => DrawSegmentCore(id, null, label, count, selected, true, label, accent, size, 0, 1,
            action: false, style: SegmentStyle.Filter, rounding: 5f, showSelectionIndicator: false, disabledIconColor: null);

    public static bool DrawFilterAction(string id, FontAwesomeIcon icon, bool selected, string tooltip, Vector4 accent, Vector2 size)
        => DrawSegmentCore(id, icon, string.Empty, null, selected, true, tooltip, accent, size, 0, 1,
            action: true, style: SegmentStyle.Filter, rounding: 5f, showSelectionIndicator: false, disabledIconColor: null);

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
        SegmentStyle style,
        float rounding,
        bool showSelectionIndicator,
        Vector4? disabledIconColor,
        EditorSegmentBackground background = EditorSegmentBackground.Default)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            if (style == SegmentStyle.Filter)
            {
                using ImRaii.IdDisposable itemId = ImRaii.PushId(id);
                using ImRaii.ColorDisposable colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                    .Push(ImGuiCol.ButtonHovered, Vector4.Zero)
                    .Push(ImGuiCol.ButtonActive, Vector4.Zero);
                using ImRaii.StyleDisposable border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);
                clicked = ImGui.Button("##filter", size);
            }
            else
            {
                clicked = ImGui.InvisibleButton(id, size);
            }
        }

        bool hovered = enabled && (ImGui.IsItemHovered()
            || style == SegmentStyle.Filter && ImGui.GetIO().NavVisible && ImGui.IsItemFocused());
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();

        if (!ImGui.IsItemVisible())
        {
            return clicked && enabled;
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        DrawFrame(drawList, min, max, selected, enabled, hovered, ImGui.IsItemActive(), accent,
            ResolveRoundingFlags(index, segmentCount), action, style, rounding, background);
        drawList.PushClipRect(min, max, true);
        try
        {
            DrawContent(drawList, min, max, icon, label, count, selected || action, enabled, hovered,
                accent, style, disabledIconColor);
        }
        finally
        {
            drawList.PopClipRect();
        }

        if (selected && style == SegmentStyle.Standard && showSelectionIndicator)
        {
            DrawSelectionIndicator(drawList, min, max, accent, enabled);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(tooltip))
        {
            if (style == SegmentStyle.Standard && icon is { } tooltipIcon)
            {
                IntonerTooltip.Attach(tooltipIcon, tooltip, options: new IntonerTooltipOptions { Accent = accent });
            }
            else
            {
                IntonerTooltip.Attach(tooltip);
            }
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
        SegmentStyle style,
        float rounding,
        EditorSegmentBackground background)
    {
        rounding = Scaled(rounding);
        Vector4 fill;
        Vector4 border;

        if (style == SegmentStyle.Filter)
        {
            fill = ThemeColors.ButtonDefault with { W = 0.50f };
            border = ThemeColors.Border with { W = 0.38f };

            if (selected)
            {
                fill = accent with { W = 0.18f };
                border = accent with { W = 0.32f };
            }

            if (hovered)
            {
                fill = accent with { W = selected ? 0.30f : 0.22f };
                border.W = 0.62f;
            }

            if (active)
            {
                fill = accent with { W = selected ? 0.40f : 0.32f };
                border.W = 0.76f;
            }
        }
        else if (style == SegmentStyle.Compact)
        {
            Vector4 baseFill = ThemeColors.ButtonDefault with { W = 0.44f };

            if (selected)
            {
                fill = accent with { W = hovered ? 0.28f : 0.20f };
                border = accent with { W = hovered ? 0.66f : 0.48f };
            }
            else
            {
                fill = hovered
                    ? Vector4.Lerp(baseFill, accent, 0.10f) with { W = 0.52f }
                    : baseFill;
                border = ThemeColors.Border with { W = hovered ? 0.46f : 0.32f };
            }

            if (!enabled)
            {
                fill.W *= 0.48f;
                border.W *= 0.50f;
            }
        }
        else if (!enabled)
        {
            fill = ThemeColors.ButtonDefault with { W = 0.18f };
            border = ThemeColors.Border with { W = 0.14f };
        }
        else
        {
            fill = action
                ? ResolveActionFillColor(hovered, active, accent)
                : ResolveFillColor(selected, hovered, active, accent, background);
            border = action
                ? ResolveActionBorderColor(hovered, accent)
                : ResolveBorderColor(selected, hovered, accent);
        }

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
        SegmentStyle style,
        Vector4? disabledIconColor)
    {
        label ??= string.Empty;

        if (style == SegmentStyle.Filter && !string.IsNullOrEmpty(count))
        {
            EditorBadgeRenderer.DrawPairContents(drawList,
                EditorBadge.Label(label), new EditorBadge(count, EditorBadgeStyle.Muted, Color: ThemeColors.TextDisabled), min, max);
            return;
        }

        if (icon is { } iconOnly && label.Length == 0 && string.IsNullOrEmpty(count))
        {
            float iconAlpha = hovered ? 1f : 0.88f;
            Vector4 enabledIconColor = accent;

            if (style == SegmentStyle.Filter)
            {
                enabledIconColor = hovered ? ThemeColors.Text : ThemeColors.TextDisabled;
            }

            Vector4 iconColor = enabled
                ? enabledIconColor with { W = iconAlpha }
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
        float gap = Scaled(style == SegmentStyle.Compact ? 4f : ContentGap);
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

        float padding = Scaled(style switch
        {
            SegmentStyle.Compact => 6f,
            SegmentStyle.Filter => FilterPadding,
            _ => 8f,
        });
        float availableLabelWidth = MathF.Max(1f, max.X - min.X - fixedWidth - padding * 2f);
        string visibleLabel = EditorTextUtility.ClipTextToWidth(label, availableLabelWidth);
        Vector2 labelSize = ImGui.CalcTextSize(visibleLabel);
        float contentWidth = fixedWidth + labelSize.X;
        float contentX = min.X + MathF.Max(style == SegmentStyle.Filter ? padding : Scaled(8f), ((max.X - min.X) - contentWidth) * 0.5f);
        Vector4 textColor = enabled && style == SegmentStyle.Standard
            ? ThemeColors.Text
            : ResolveTextColor(selected, enabled, hovered);

        if (style == SegmentStyle.Filter)
        {
            textColor = ThemeColors.Text with { W = selected || hovered ? 0.98f : 0.90f };
        }

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
                new Vector2(contentX, EditorLayout.CenterY(min.Y, max.Y - min.Y, iconMetrics.Size.Y)),
                iconColor);
            contentX += iconMetrics.Size.X + (gapAfterIcon ? gap : 0f);
        }

        drawList.AddText(
            new Vector2(contentX, EditorLayout.CenterY(min.Y, max.Y - min.Y, labelSize.Y)),
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
                EditorLayout.CenterY(min.Y, max.Y - min.Y, countTextSize.Y)),
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

    private static Vector4 ResolveFillColor(bool selected, bool hovered, bool active, Vector4 accent, EditorSegmentBackground background)
    {
        bool emphasized = background == EditorSegmentBackground.Emphasized;

        if (selected)
        {
            float alpha = emphasized ? 0.44f : 0.26f;

            if (active)
            {
                alpha = emphasized ? 0.72f : 0.40f;
            }
            else if (hovered)
            {
                alpha = emphasized ? 0.62f : 0.35f;
            }

            return accent with { W = alpha };
        }

        if (emphasized && (hovered || active))
        {
            return ThemeColors.PrimaryButtonInteraction(accent, active);
        }

        float idleAlpha = emphasized ? 0.65f : 0.38f;
        return ThemeColors.ButtonDefault with { W = hovered ? 0.56f : idleAlpha };
    }

    private static Vector4 ResolveBorderColor(bool selected, bool hovered, Vector4 accent)
    {
        if (selected)
        {
            return accent with { W = hovered ? 0.70f : 0.54f };
        }

        return ThemeColors.Border with { W = hovered ? 0.42f : 0.26f };
    }

    private static Vector4 ResolveActionFillColor(bool hovered, bool active, Vector4 accent)
    {
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

    private static Vector4 ResolveActionBorderColor(bool hovered, Vector4 accent)
        => accent with { W = hovered ? 0.68f : 0.46f };

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

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
