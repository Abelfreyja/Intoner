using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal static class EditorBadgeRenderer
{
    /// <summary> an action segment attached to the final inline badge row </summary>
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct InlineAction(string Id, EditorBadge Badge, bool Enabled = true);

    private enum Surface
    {
        Row,
        Panel,
    }

    private const float BadgeHeight = 22f;
    private const float IconScale = 0.84f;
    private const float ThumbnailWidth = 30f;
    private const float ThumbnailHeight = 18f;
    private const float ThumbnailSpacing = 2f;

    public static float Height => Scaled(BadgeHeight);

    public static void Draw(EditorBadge badge)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        float width = MathF.Min(MeasureWidth(badge), MathF.Max(0f, ImGui.GetContentRegionAvail().X));
        ImGui.Dummy(new Vector2(width, Height));
        DrawGroup(ImGui.GetWindowDrawList(), null, badge, min.X, min.Y, width, Height, false, 1f, Surface.Panel, null);
    }

    public static float DrawRightAligned(
        ImDrawListPtr drawList,
        IReadOnlyList<EditorBadge>? badges,
        EditorBadge? trailingBadge,
        float right,
        float top,
        bool selected,
        float contentAlpha,
        bool panelSurface = false,
        Vector4? borderColor = null)
    {
        float width = MeasureGroupWidth(badges, trailingBadge);
        float left = right - width;
        DrawGroup(
            drawList,
            badges,
            trailingBadge,
            left,
            top,
            width,
            Height,
            selected,
            contentAlpha,
            panelSurface ? Surface.Panel : Surface.Row,
            borderColor);
        return left;
    }

    public static float DrawAt(
        ImDrawListPtr drawList,
        IReadOnlyList<EditorBadge> badges,
        float left,
        float top,
        bool selected = false,
        float contentAlpha = 1f,
        float? maxRight = null,
        bool panelSurface = false,
        Vector4? borderColor = null)
    {
        float width = MeasureGroupWidth(badges, null);
        if (maxRight is { } right)
        {
            width = MathF.Min(width, MathF.Max(0f, right - left));
        }

        DrawGroup(
            drawList,
            badges,
            null,
            left,
            top,
            width,
            Height,
            selected,
            contentAlpha,
            panelSurface ? Surface.Panel : Surface.Row,
            borderColor);
        return left + width;
    }

    public static bool DrawInline(
        IReadOnlyList<EditorBadge> badges,
        bool selected = false,
        float maxWidth = float.PositiveInfinity,
        Vector4? borderColor = null,
        bool wrap = false,
        InlineAction? action = null)
    {
        if (badges.Count == 0)
        {
            return false;
        }

        float availableWidth = MathF.Min(
            MathF.Max(0f, maxWidth),
            MathF.Max(0f, ImGui.GetContentRegionAvail().X));
        float height = Height;
        float actionWidth = action is { } inlineAction ? MeasureWidth(inlineAction.Badge) : 0f;
        bool clicked = false;
        int start = 0;
        while (start < badges.Count)
        {
            float width = 0f;
            int count = 0;
            while (start + count < badges.Count)
            {
                float badgeWidth = MeasureWidth(badges[start + count]);
                float trailingWidth = start + count == badges.Count - 1 ? actionWidth : 0f;
                if (wrap && count > 0 && width + badgeWidth + trailingWidth > availableWidth)
                {
                    break;
                }

                width += badgeWidth;
                ++count;
            }

            bool hasAction = action.HasValue && start + count == badges.Count;
            width = MathF.Min(width + (hasAction ? actionWidth : 0f), availableWidth);
            Vector2 topLeft = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(width, height));
            DrawGroup(
                ImGui.GetWindowDrawList(),
                badges,
                hasAction ? action!.Value.Badge : null,
                topLeft.X,
                topLeft.Y,
                width,
                height,
                selected,
                1f,
                Surface.Panel,
                borderColor,
                start,
                count,
                reserveTrailingBadge: hasAction);
            if (hasAction && width > 0f)
            {
                Vector2 actionSize = new(MathF.Min(actionWidth, width), height);
                Vector2 actionMin = topLeft + new Vector2(width - actionSize.X, 0f);
                clicked = DrawAction(action!.Value, actionMin, actionSize);
            }

            start += count;
        }

        return clicked;
    }

    private static bool DrawAction(InlineAction action, Vector2 min, Vector2 size)
    {
        Vector2 cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(min);
        using var disabled = ImRaii.Disabled(!action.Enabled);
        using var button = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, Vector4.Zero)
            .Push(ImGuiCol.ButtonActive, Vector4.Zero);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);
        bool clicked = ImGui.Button($"##{action.Id}", size);
        ImGui.SetCursorScreenPos(cursor);
        return clicked;
    }

    private static float MeasureGroupWidth(
        IReadOnlyList<EditorBadge>? badges,
        EditorBadge? trailingBadge)
    {
        float width = 0f;
        int count = badges?.Count ?? 0;
        for (int index = 0; index < count; ++index)
        {
            width += MeasureWidth(badges![index]);
        }

        if (trailingBadge is { } trailing)
        {
            width += MeasureWidth(trailing);
        }

        return width;
    }

    private static void DrawGroup(
        ImDrawListPtr drawList,
        IReadOnlyList<EditorBadge>? badges,
        EditorBadge? trailingBadge,
        float left,
        float top,
        float width,
        float height,
        bool selected,
        float contentAlpha,
        Surface surface,
        Vector4? borderColor,
        int badgeStart = 0,
        int? visibleBadgeCount = null,
        bool reserveTrailingBadge = false)
    {
        int badgeCount = visibleBadgeCount ?? badges?.Count ?? 0;
        int segmentCount = badgeCount + (trailingBadge.HasValue ? 1 : 0);
        if (segmentCount == 0 || width <= 0f)
        {
            return;
        }

        Vector2 min = new(left, top);
        Vector2 max = new(left + width, top + height);
        float rounding = Scaled(5f);
        const float panelAlpha = 0.50f;
        Vector4 background = surface == Surface.Panel
            ? ThemeColors.ButtonDefault with { W = panelAlpha * contentAlpha }
            : ThemeColors.WindowBg with { W = 0.66f * contentAlpha };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(background), rounding);
        Vector4 border = borderColor ?? (selected ? ThemeColors.AccentPrimary : ThemeColors.Border);
        float borderAlpha = 0.50f;
        if (selected)
        {
            borderAlpha = 0.44f;
        }
        else if (surface == Surface.Panel)
        {
            borderAlpha = 0.38f;
        }

        border.W = borderAlpha * contentAlpha;
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            rounding,
            ImDrawFlags.None,
            Scaled(1f));

        float segmentLeft = left;
        float trailingWidth = reserveTrailingBadge && trailingBadge is { } reserved
            ? MathF.Min(width, MeasureWidth(reserved))
            : 0f;
        float remainingWidth = width - trailingWidth;
        for (int index = 0; index < badgeCount; ++index)
        {
            EditorBadge badge = badges![badgeStart + index];
            float measuredWidth = MeasureWidth(badge);
            float segmentWidth = MathF.Min(measuredWidth, remainingWidth);
            if (segmentWidth <= 0f)
            {
                break;
            }

            remainingWidth -= segmentWidth;
            DrawSegment(
                drawList,
                badge,
                segmentLeft,
                top,
                segmentWidth,
                height,
                index + 1 < segmentCount && (remainingWidth > 0f || trailingWidth > 0f),
                segmentWidth < measuredWidth,
                contentAlpha);
            segmentLeft += segmentWidth;
        }

        if (trailingWidth > 0f)
        {
            segmentLeft = left + width - trailingWidth;
            remainingWidth = trailingWidth;
        }

        if (trailingBadge is { } trailing && remainingWidth > 0f)
        {
            float measuredWidth = MeasureWidth(trailing);
            float segmentWidth = MathF.Min(measuredWidth, remainingWidth);
            DrawSegment(
                drawList,
                trailing,
                segmentLeft,
                top,
                segmentWidth,
                height,
                drawSeparator: false,
                clipped: segmentWidth < measuredWidth,
                contentAlpha: contentAlpha);
        }
    }

    public static float MeasureWidth(EditorBadge badge)
    {
        if (badge.Style == EditorBadgeStyle.IconOnly)
        {
            return Scaled(24f);
        }

        float width = Scaled(12f);
        if (badge.Thumbnails is { } thumbnails)
        {
            width += MeasureThumbnailStripWidth(thumbnails.Count);
        }
        else if (badge.Icon is { } icon)
        {
            width += EditorIcon.Measure(icon, IconScale).Size.X;
        }

        if (!string.IsNullOrEmpty(badge.Text))
        {
            if (badge.Icon is not null || badge.Thumbnails is not null)
            {
                width += Scaled(4f);
            }

            width += ImGui.CalcTextSize(badge.Text).X;
        }

        return width;
    }

    private static void DrawSegment(
        ImDrawListPtr drawList,
        EditorBadge badge,
        float left,
        float top,
        float width,
        float height,
        bool drawSeparator,
        bool clipped,
        float contentAlpha)
    {
        Vector2 min = new(left, top);
        Vector2 max = new(left + width, top + height);
        Vector4 color = badge.Color ?? ThemeColors.AccentPrimary;
        bool hovered = IntonerTooltip.IsAreaHovered(min, max);
        if (hovered)
        {
            float inset = Scaled(1f);
            drawList.AddRectFilled(
                min + new Vector2(inset),
                max - new Vector2(inset),
                ImGui.GetColorU32(color with { W = 0.12f * contentAlpha }),
                Scaled(4f));
        }

        drawList.PushClipRect(min, max, true);
        try
        {
            DrawContent(drawList, badge, min, width, height, color, contentAlpha);
        }
        finally
        {
            drawList.PopClipRect();
        }

        if (drawSeparator)
        {
            float separatorInset = Scaled(5f);
            drawList.AddLine(
                new Vector2(max.X, min.Y + separatorInset),
                new Vector2(max.X, max.Y - separatorInset),
                ImGui.GetColorU32(ThemeColors.Border with { W = 0.62f * contentAlpha }),
                Scaled(1f));
        }

        DrawTooltip(badge, color, clipped, hovered);
    }

    private static void DrawContent(
        ImDrawListPtr drawList,
        EditorBadge badge,
        Vector2 min,
        float width,
        float height,
        Vector4 color,
        float contentAlpha)
    {
        EditorIcon.Metrics iconMetrics = badge.Icon is { } icon
            ? EditorIcon.Measure(icon, IconScale)
            : default;
        Vector2 textSize = string.IsNullOrEmpty(badge.Text) ? Vector2.Zero : ImGui.CalcTextSize(badge.Text);
        float visualWidth = badge.Thumbnails is { } thumbnails
            ? MeasureThumbnailStripWidth(thumbnails.Count)
            : iconMetrics.Size.X;
        float contentWidth = visualWidth + textSize.X;
        if (visualWidth > 0f && textSize.X > 0f)
        {
            contentWidth += Scaled(4f);
        }

        float contentX = min.X + MathF.Max(0f, (width - contentWidth) * 0.5f);
        if (badge.Thumbnails is { } thumbnailStrip)
        {
            Vector2 thumbnailSize = new(
                Scaled(ThumbnailWidth),
                MathF.Min(Scaled(ThumbnailHeight), height - Scaled(4f)));
            float thumbnailTop = CenterY(min.Y, height, thumbnailSize.Y);
            float thumbnailStep = thumbnailSize.X + Scaled(ThumbnailSpacing);
            for (int index = 0; index < thumbnailStrip.Count; ++index)
            {
                Vector2 thumbnailMin = new(contentX + (thumbnailStep * index), thumbnailTop);
                IDalamudTextureWrap? thumbnail = thumbnailStrip.GetTexture(index);
                if (thumbnail is not null)
                {
                    EditorImage.DrawCover(
                        drawList,
                        thumbnail,
                        thumbnailMin,
                        thumbnailMin + thumbnailSize,
                        Scaled(3f),
                        contentAlpha);
                }
                else if (badge.Icon is { } fallbackIcon)
                {
                    DrawThumbnailFallback(
                        drawList,
                        fallbackIcon,
                        iconMetrics,
                        thumbnailMin,
                        thumbnailMin + thumbnailSize,
                        color,
                        contentAlpha);
                }
            }

            contentX += visualWidth;
            if (textSize.X > 0f)
            {
                contentX += Scaled(4f);
            }
        }
        else if (badge.Icon is { } badgeIcon)
        {
            EditorIcon.Draw(
                drawList,
                badgeIcon,
                iconMetrics,
                new Vector2(contentX, CenterY(min.Y, height, iconMetrics.Size.Y)),
                color with { W = 0.94f * contentAlpha });
            contentX += iconMetrics.Size.X;
            if (textSize.X > 0f)
            {
                contentX += Scaled(4f);
            }
        }

        if (textSize.X > 0f)
        {
            Vector4 textColor = badge.Style == EditorBadgeStyle.Muted
                ? ThemeColors.TextDisabled with { W = 0.82f * contentAlpha }
                : ThemeColors.Text with { W = 0.88f * contentAlpha };
            drawList.AddText(
                new Vector2(contentX, CenterY(min.Y, height, textSize.Y)),
                ImGui.GetColorU32(textColor),
                badge.Text);
        }
    }

    private static void DrawTooltip(
        EditorBadge badge,
        Vector4 color,
        bool clipped,
        bool hovered)
    {
        if (!hovered)
        {
            return;
        }

        if (badge.DrawTooltip is not null)
        {
            badge.DrawTooltip();
        }
        else if (!string.IsNullOrWhiteSpace(badge.Tooltip))
        {
            if (badge.Icon is { } icon)
            {
                IntonerTooltip.DrawDescription(
                    icon,
                    badge.Tooltip,
                    badge.TooltipDetail ?? string.Empty,
                    options: new IntonerTooltipOptions { Accent = color });
            }
            else
            {
                IntonerTooltip.DrawText(badge.Tooltip, color, 35f);
            }
        }
        else if (clipped && !string.IsNullOrWhiteSpace(badge.Text))
        {
            IntonerTooltip.DrawText(badge.Text, color, 35f);
        }
    }

    private static void DrawThumbnailFallback(
        ImDrawListPtr drawList,
        FontAwesomeIcon icon,
        EditorIcon.Metrics iconMetrics,
        Vector2 min,
        Vector2 max,
        Vector4 color,
        float contentAlpha)
    {
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(color with { W = 0.10f * contentAlpha }),
            Scaled(3f));
        Vector2 size = max - min;
        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            new Vector2(
                min.X + MathF.Max(0f, (size.X - iconMetrics.Size.X) * 0.5f),
                CenterY(min.Y, size.Y, iconMetrics.Size.Y)),
            color with { W = 0.78f * contentAlpha });
    }

    private static float MeasureThumbnailStripWidth(int count)
        => count <= 0
            ? 0f
            : (Scaled(ThumbnailWidth) * count) + (Scaled(ThumbnailSpacing) * (count - 1));

    private static float CenterY(float top, float height, float contentHeight)
        => top + MathF.Max(0f, (height - contentHeight) * 0.5f);

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
