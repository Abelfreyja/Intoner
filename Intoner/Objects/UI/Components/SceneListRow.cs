using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Configuration;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal sealed class SceneListRow(EditorListCard listCard)
{
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct FolderResult(bool Collapsed, RowGeometry Geometry);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Metrics(
        SceneListRowSize Size,
        float ItemHeight,
        float FolderHeight,
        float ItemSpacing,
        float GroupSpacing,
        float ChildInset,
        float ChildRightInset,
        float TreeLineInset);

    public readonly record struct ColorMarker(Vector4 Color, string Label, string Tooltip);

    public readonly record struct Status(string Label, Vector4 Color);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct RowGeometry(Vector2 Min, Vector2 Max)
    {
        public float CenterY => Min.Y + ((Max.Y - Min.Y) * 0.5f);
    }

    public readonly record struct Item
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public required string TypeLabel { get; init; }
        public required FontAwesomeIcon TypeIcon { get; init; }
        public IDalamudTextureWrap? ItemIcon { get; init; }
        public required Vector4 Accent { get; init; }
        public Status? Status { get; init; }
        public ColorMarker? ColorMarker { get; init; }
        public required bool Emphasized { get; init; }
        public required bool Visible { get; init; }
        public required bool Locked { get; init; }
        public required bool Selected { get; init; }
        public float? LeftInset { get; init; }
        public float? RightInset { get; init; }
    }

    public readonly record struct Folder
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public required Vector4 Accent { get; init; }
        public FontAwesomeIcon? TypeIcon { get; init; }
        public string? TypeLabel { get; init; }
        public required int ChildCount { get; init; }
        public required bool Collapsed { get; init; }
    }

    public static Metrics ResolveMetrics(SceneListRowSize size)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float lineHeight = ImGui.GetTextLineHeight();
        return size switch
        {
            SceneListRowSize.Compact => new Metrics(
                size,
                MathF.Max(34f * scale, lineHeight + (14f * scale)),
                MathF.Max(38f * scale, lineHeight + (16f * scale)),
                2f * scale,
                6f * scale,
                32f * scale,
                4f * scale,
                18f * scale),
            _ => new Metrics(
                size,
                MathF.Max(52f * scale, (lineHeight * 2f) + (20f * scale)),
                MathF.Max(44f * scale, (lineHeight * 2f) + (14f * scale)),
                2f * scale,
                8f * scale,
                32f * scale,
                4f * scale,
                18f * scale),
        };
    }

    public static EditorListCard.Interaction DrawInteraction(Item item, Metrics metrics)
        => DrawInteraction(item.Id, item.Selected, metrics.ItemHeight, item.LeftInset, item.RightInset);

    public static EditorListCard.Interaction DrawFolderInteraction(
        Folder folder,
        Metrics metrics,
        float? leftInset = null,
        float? rightInset = null)
        => DrawInteraction(
            folder.Id,
            selected: false,
            height: metrics.FolderHeight,
            leftInset,
            rightInset,
            flags: ImGuiSelectableFlags.AllowItemOverlap);

    private static EditorListCard.Interaction DrawInteraction(
        string id,
        bool selected,
        float height,
        float? leftInset,
        float? rightInset,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 startPos = ImGui.GetCursorPos();
        float resolvedLeftInset = leftInset ?? (4f * scale);
        float resolvedRightInset = rightInset ?? (4f * scale);
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - resolvedLeftInset - resolvedRightInset);

        ImGui.SetCursorPosX(startPos.X + resolvedLeftInset);
        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            id,
            selected,
            new Vector2(width, height),
            flags);

        Vector2 endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));
        return interaction;
    }

    public void DrawContent(Item item, Metrics metrics, EditorListCard.Interaction interaction)
    {
        DrawChrome(interaction, item);
        if (metrics.Size == SceneListRowSize.Compact)
        {
            DrawCompactContent(interaction, item);
        }
        else
        {
            DrawLargeContent(interaction, item);
        }
    }

    public void DrawFolderChrome(Folder folder, EditorListCard.Interaction interaction)
    {
        Vector4 fill = ThemeColors.ButtonDefault with { W = 0.20f };
        Vector4 border = folder.Accent with { W = interaction.Hovered ? 0.54f : 0.28f };
        listCard.DrawFrame(
            ImGui.GetWindowDrawList(),
            interaction.Min,
            interaction.Max,
            fill,
            border,
            folder.Accent with { W = 0.54f },
            8f * ImGuiHelpers.GlobalScale,
            interaction.Hovered);
    }

    public static void DrawFolderContent(
        Folder folder,
        Metrics metrics,
        EditorListCard.Interaction interaction,
        float contentRight)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float scale = ImGuiHelpers.GlobalScale;
        float rowHeight = interaction.Max.Y - interaction.Min.Y;
        float padX = 10f * scale;
        float lineHeight = ImGui.GetTextLineHeight();
        FontAwesomeIcon disclosureIcon = folder.Collapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronDown;
        FontAwesomeIcon resolvedFolderIcon = folder.TypeIcon
            ?? (folder.Collapsed ? FontAwesomeIcon.Folder : FontAwesomeIcon.FolderOpen);
        EditorIcon.Metrics disclosureMetrics = EditorIcon.Measure(disclosureIcon);
        EditorIcon.Metrics folderIconMetrics = EditorIcon.Measure(resolvedFolderIcon);
        Vector2 disclosurePos = new(
            interaction.Min.X + padX,
            interaction.Min.Y + ((rowHeight - disclosureMetrics.Size.Y) * 0.5f));
        Vector2 folderIconPos = new(
            disclosurePos.X + disclosureMetrics.Size.X + (8f * scale),
            interaction.Min.Y + ((rowHeight - folderIconMetrics.Size.Y) * 0.5f));

        EditorIcon.Draw(drawList, disclosureIcon, disclosureMetrics, disclosurePos, folder.Accent with { W = 0.84f });
        EditorIcon.Draw(drawList, resolvedFolderIcon, folderIconMetrics, folderIconPos, folder.Accent with { W = 0.92f });
        if (!string.IsNullOrWhiteSpace(folder.TypeLabel))
        {
            DrawTooltip(folderIconPos, folderIconPos + folderIconMetrics.Size, folder.TypeLabel, folder.Accent);
        }

        float labelX = folderIconPos.X + folderIconMetrics.Size.X + (10f * scale);
        if (metrics.Size == SceneListRowSize.Compact)
        {
            float labelRight = DrawFolderMetadata(
                drawList,
                contentRight,
                interaction.Min.Y + (rowHeight * 0.5f),
                folder.ChildCount,
                folder.TypeLabel,
                folder.Accent);
            drawList.AddText(
                new Vector2(labelX, interaction.Min.Y + ((rowHeight - lineHeight) * 0.5f)),
                ImGui.GetColorU32(ThemeColors.Text),
                EditorTextUtility.ClipTextToWidth(
                    folder.Name,
                    MathF.Max(1f, labelRight - labelX)));
            return;
        }

        float labelWidth = MathF.Max(1f, contentRight - labelX);
        float textGap = 4f * scale;
        float titleY = interaction.Min.Y + ((rowHeight - ((lineHeight * 2f) + textGap)) * 0.5f);
        drawList.AddText(
            new Vector2(labelX, titleY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(folder.Name, labelWidth));
        drawList.AddText(
            new Vector2(labelX, titleY + lineHeight + textGap),
            ImGui.GetColorU32(ThemeColors.TextDisabled with { W = 0.90f }),
            EditorTextUtility.ClipTextToWidth(folder.Detail, labelWidth));
    }

    public static float ResolveActionButtonEdge()
    {
        float scale = ImGuiHelpers.GlobalScale;
        return MathF.Max(22f * scale, ImGui.GetTextLineHeight() + (6f * scale));
    }

    public static bool DrawActionButton(
        string id,
        FontAwesomeIcon icon,
        string tooltip,
        Vector4 color,
        bool highlighted,
        float edge)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector4 fill = highlighted
            ? color with { W = 0.10f }
            : Vector4.Zero;
        Vector4 iconColor = highlighted
            ? color with { W = 0.96f }
            : ThemeColors.TextDisabled with { W = 0.90f };

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill)
            .Push(ImGuiCol.ButtonHovered, color with { W = 0.14f })
            .Push(ImGuiCol.ButtonActive, color with { W = 0.22f });
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f)
            .Push(ImGuiStyleVar.FrameRounding, 4f * scale);
        return EditorIconButton.DrawDefault(id, icon, tooltip, edgeOverride: edge, iconColor: iconColor, tooltipAccent: color);
    }

    private static float DrawFolderMetadata(
        ImDrawListPtr drawList,
        float right,
        float centerY,
        int childCount,
        string? typeLabel,
        Vector4 accent)
    {
        float scale = ImGuiHelpers.GlobalScale;
        string count = childCount.ToString();
        string suffix = childCount == 1 ? " item" : " items";
        string separator = " · ";
        bool hasTypeLabel = !string.IsNullOrWhiteSpace(typeLabel);
        Vector2 typeSize = hasTypeLabel ? ImGui.CalcTextSize(typeLabel) : Vector2.Zero;
        Vector2 separatorSize = hasTypeLabel ? ImGui.CalcTextSize(separator) : Vector2.Zero;
        Vector2 countSize = ImGui.CalcTextSize(count);
        Vector2 suffixSize = ImGui.CalcTextSize(suffix);
        float left = right - typeSize.X - separatorSize.X - countSize.X - suffixSize.X;
        float textY = centerY - (MathF.Max(typeSize.Y, countSize.Y) * 0.5f);
        float textX = left;

        if (hasTypeLabel)
        {
            drawList.AddText(
                new Vector2(textX, textY),
                ImGui.GetColorU32(accent with { W = 0.90f }),
                typeLabel);
            textX += typeSize.X;
            drawList.AddText(
                new Vector2(textX, textY),
                ImGui.GetColorU32(ThemeColors.TextDisabled with { W = 0.56f }),
                separator);
            textX += separatorSize.X;
        }

        drawList.AddText(
            new Vector2(textX, textY),
            ImGui.GetColorU32(accent with { W = 0.86f }),
            count);
        drawList.AddText(
            new Vector2(textX + countSize.X, textY),
            ImGui.GetColorU32(ThemeColors.TextDisabled with { W = 0.82f }),
            suffix);
        return left - (10f * scale);
    }

    public static FolderChildrenScope BeginFolderChildren(
        Metrics metrics,
        Vector4 accent,
        int depth = 1,
        float? branchX = null)
        => new(metrics, accent, depth, branchX);

    [StructLayout(LayoutKind.Auto)]
    public ref struct FolderChildrenScope
    {
        private readonly Metrics _metrics;
        private readonly ImDrawListPtr _drawList;
        private readonly Vector4 _lineColor;
        private readonly float _scale;
        private readonly float _lineX;
        private float? _firstLineY;
        private float _lastLineY;

        internal FolderChildrenScope(Metrics metrics, Vector4 accent, int depth, float? branchX)
        {
            _metrics = metrics;
            _drawList = ImGui.GetWindowDrawList();
            _lineColor = accent with { W = 0.76f };
            _scale = ImGuiHelpers.GlobalScale;
            _lineX = branchX
                ?? ImGui.GetCursorScreenPos().X
                    + (Math.Max(0, depth - 1) * metrics.ChildInset)
                    + metrics.TreeLineInset;
            _firstLineY = null;
            _lastLineY = 0f;
            ImGui.Dummy(new Vector2(0f, 2f * _scale));
        }

        public void Add(RowGeometry geometry)
        {
            _firstLineY ??= geometry.Min.Y - (2f * _scale);
            _lastLineY = geometry.CenterY;
            _drawList.AddLine(
                new Vector2(_lineX, geometry.CenterY),
                new Vector2(geometry.Min.X, geometry.CenterY),
                ImGui.GetColorU32(_lineColor),
                _scale);
        }

        public void AddSpacing(bool hasNext)
        {
            if (hasNext)
            {
                ImGui.Dummy(new Vector2(0f, _metrics.ItemSpacing));
            }
        }

        public void Dispose()
        {
            if (_firstLineY.HasValue)
            {
                _drawList.AddLine(
                    new Vector2(_lineX, _firstLineY.Value),
                    new Vector2(_lineX, _lastLineY),
                    ImGui.GetColorU32(_lineColor),
                    _scale);
            }
        }
    }

    private void DrawChrome(EditorListCard.Interaction interaction, Item item)
    {
        float contentAlpha = (item.Emphasized, item.Locked) switch
        {
            (true, true)   => 0.78f,
            (true, false)  => 1f,
            (false, true)  => 0.62f,
            (false, false) => 0.72f,
        };
        Vector4 fill = (item.Selected, interaction.Hovered) switch
        {
            (true, _)     => item.Accent with { W = 0.17f * contentAlpha },
            (false, true) => item.Accent with { W = 0.08f * contentAlpha },
            _             => ThemeColors.ButtonDefault with { W = 0.22f * contentAlpha },
        };
        Vector4 border = (item.Selected, interaction.Hovered) switch
        {
            (true, _)     => item.Accent with { W = 0.85f * contentAlpha },
            (false, true) => item.Accent with { W = 0.58f * contentAlpha },
            _             => ThemeColors.Border with { W = 0.38f * contentAlpha },
        };

        listCard.DrawFrame(
            ImGui.GetWindowDrawList(),
            interaction.Min,
            interaction.Max,
            fill,
            border,
            item.Accent with { W = item.Selected ? 0.95f : 0.62f },
            8f * ImGuiHelpers.GlobalScale,
            interaction.Hovered && !item.Selected);
    }

    private static unsafe void DrawLargeContent(EditorListCard.Interaction interaction, Item item)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float scale = ImGuiHelpers.GlobalScale;
        float padX = 10f * scale;
        float padY = 8f * scale;
        float lineHeight = ImGui.GetTextLineHeight();
        float contentAlpha = item.Locked ? 0.72f : 1f;
        Vector4 textColor = ThemeColors.Text with { W = ThemeColors.Text.W * contentAlpha };
        Vector4 detailColor = ThemeColors.TextDisabled with { W = 0.88f * contentAlpha };

        float iconEdge = item.ItemIcon is null ? 18f * scale : 36f * scale;
        Vector2 iconMin = new(
            interaction.Min.X + padX,
            interaction.Min.Y + (((interaction.Max.Y - interaction.Min.Y) - iconEdge) * 0.5f));
        Vector2 iconMax = iconMin + new Vector2(iconEdge);
        DrawItemIcon(drawList, iconMin, iconMax, item, textColor.W);

        float right = interaction.Max.X - padX;
        float centerY = interaction.Min.Y + ((interaction.Max.Y - interaction.Min.Y) * 0.5f);
        if (item.Status is { } status)
        {
            right = DrawStatusIndicator(drawList, right, centerY, status, textColor.W);
        }

        if (item.Locked)
        {
            right = DrawStateIcon(
                drawList,
                right - (6f * scale),
                centerY - (lineHeight * 0.5f),
                FontAwesomeIcon.Lock,
                "Locked",
                ThemeColors.DimRed,
                lineHeight,
                textColor.W);
        }

        float nameX = iconMax.X + (7f * scale);
        float availableNameWidth = right - nameX - (8f * scale);
        float colorBadgeReserve = item.ColorMarker.HasValue
            ? MathF.Min(110f * scale, MathF.Max(0f, availableNameWidth - EditorListCard.MinimumTextWidth))
            : 0f;
        float nameWidth = MathF.Max(1f, availableNameWidth - colorBadgeReserve);
        string renderedName = EditorTextUtility.ClipTextToWidth(item.Name, nameWidth);
        drawList.AddText(new Vector2(nameX, interaction.Min.Y + padY), ImGui.GetColorU32(textColor), renderedName);

        if (item.ColorMarker is { } colorMarker)
        {
            DrawLargeColorMarker(
                drawList,
                colorMarker,
                nameX + ImGui.CalcTextSize(renderedName).X + (8f * scale),
                right,
                interaction.Min.Y + padY,
                item.Accent,
                item.Selected,
                detailColor);
        }

        float detailY = interaction.Min.Y + padY + lineHeight + (4f * scale);
        FontAwesomeIcon visibilityIcon = item.Visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        float visibilityWidth = MathF.Max(lineHeight, EditorIcon.Measure(visibilityIcon).Size.X);
        // center on visible text bounds instead of the font's line padding
        ImFontPtr font = ImGui.GetFont();
        ImFontGlyphPtr ascender = (ImFontGlyphPtr)font.FindGlyph('A');
        ImFontGlyphPtr descender = (ImFontGlyphPtr)font.FindGlyph('g');
        float textCenterY = detailY + ((ascender.Y0 + descender.Y1) * ImGui.GetFontSize() / font.FontSize * 0.5f);
        _ = DrawStateIcon(
            drawList,
            nameX + visibilityWidth,
            textCenterY - (lineHeight * 0.5f),
            visibilityIcon,
            item.Visible ? "Visible" : "Hidden",
            item.Visible ? ThemeColors.TextDisabled : ThemeColors.DimRed,
            lineHeight,
            textColor.W);
        float detailX = nameX + visibilityWidth + (8f * scale);
        drawList.AddText(
            new Vector2(detailX, detailY),
            ImGui.GetColorU32(detailColor),
            EditorTextUtility.ClipTextToWidth(
                item.Detail,
                MathF.Max(1f, right - detailX)));
    }

    private static void DrawCompactContent(EditorListCard.Interaction interaction, Item item)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        float scale = ImGuiHelpers.GlobalScale;
        float padX = 9f * scale;
        float lineHeight = ImGui.GetTextLineHeight();
        float centerY = interaction.Min.Y + ((interaction.Max.Y - interaction.Min.Y) * 0.5f);
        float iconEdge = item.ItemIcon is null ? 18f * scale : 24f * scale;
        float iconY = centerY - (iconEdge * 0.5f);
        float stateIconY = centerY - (lineHeight * 0.5f);
        float contentAlpha = item.Locked ? 0.72f : 1f;
        Vector4 textColor = ThemeColors.Text with { W = ThemeColors.Text.W * contentAlpha };

        Vector2 iconMin = new(interaction.Min.X + padX, iconY);
        Vector2 iconMax = iconMin + new Vector2(iconEdge);
        DrawItemIcon(drawList, iconMin, iconMax, item, textColor.W);

        float right = interaction.Max.X - padX;
        if (item.Status is { } status)
        {
            right = DrawStatusIndicator(drawList, right, centerY, status, textColor.W);
        }

        if (item.Locked)
        {
            right = DrawStateIcon(drawList, right, stateIconY, FontAwesomeIcon.Lock, "Locked", ThemeColors.DimRed, lineHeight, textColor.W);
        }

        right = DrawStateIcon(
            drawList,
            right,
            stateIconY,
            item.Visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
            item.Visible ? "Visible" : "Hidden",
            item.Visible ? ThemeColors.TextDisabled : ThemeColors.DimRed,
            lineHeight,
            textColor.W);

        float nameX = iconMax.X + (7f * scale);
        float markerWidth = item.ColorMarker.HasValue ? 18f * scale : 0f;
        float nameWidth = MathF.Max(1f, right - nameX - markerWidth - (6f * scale));
        string renderedName = EditorTextUtility.ClipTextToWidth(item.Name, nameWidth);
        drawList.AddText(
            new Vector2(nameX, centerY - (lineHeight * 0.5f)),
            ImGui.GetColorU32(textColor),
            renderedName);

        if (item.ColorMarker is { } colorMarker)
        {
            float swatchEdge = 10f * scale;
            Vector2 swatchMin = new(
                nameX + ImGui.CalcTextSize(renderedName).X + (6f * scale),
                centerY - (swatchEdge * 0.5f));
            Vector2 swatchMax = swatchMin + new Vector2(swatchEdge);
            drawList.AddRectFilled(swatchMin, swatchMax, ImGui.GetColorU32(colorMarker.Color), 2f * scale);
            drawList.AddRect(swatchMin, swatchMax, ImGui.GetColorU32(item.Accent with { W = 0.62f }), 2f * scale);
            DrawTooltip(swatchMin, swatchMax, colorMarker.Tooltip, item.Accent);
        }
    }

    private static void DrawTypeIcon(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        FontAwesomeIcon icon,
        string tooltip,
        Vector4 accent,
        float contentAlpha)
    {
        EditorIcon.DrawCentered(drawList, icon, min, max, accent with { W = contentAlpha });
        DrawTooltip(min, max, icon, tooltip, accent);
    }

    private static void DrawItemIcon(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Item item,
        float contentAlpha)
    {
        if (item.ItemIcon is not { } itemIcon)
        {
            DrawTypeIcon(drawList, min, max, item.TypeIcon, item.TypeLabel, item.Accent, contentAlpha);
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        float rounding = 6f * scale;
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.WindowBg with { W = 0.28f * contentAlpha }),
            rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(item.Accent with { W = (item.Selected ? 0.52f : 0.28f) * contentAlpha }),
            rounding);
        drawList.AddImage(itemIcon.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFF);
        DrawTooltip(min, max, item.TypeIcon, item.TypeLabel, item.Accent);
    }

    private static float DrawStatusIndicator(
        ImDrawListPtr drawList,
        float right,
        float centerY,
        Status status,
        float contentAlpha)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float radius = 3.5f * scale;
        Vector2 center = new(right - radius, centerY);
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(status.Color with { W = contentAlpha }));
        drawList.AddCircle(
            center,
            radius + (2f * scale),
            ImGui.GetColorU32(status.Color with { W = 0.24f * contentAlpha }),
            12,
            scale);
        Vector2 tooltipExtent = new(radius + (4f * scale));
        DrawTooltip(center - tooltipExtent, center + tooltipExtent, status.Label, status.Color);
        return center.X - radius - (8f * scale);
    }

    private static float DrawStateIcon(
        ImDrawListPtr drawList,
        float right,
        float y,
        FontAwesomeIcon icon,
        string tooltip,
        Vector4 color,
        float edge,
        float contentAlpha)
    {
        Vector2 iconSize = EditorIcon.Measure(icon).Size;
        float width = MathF.Max(edge, iconSize.X);
        float height = MathF.Max(edge, iconSize.Y);
        Vector2 min = new(right - width, y + ((edge - height) * 0.5f));
        Vector2 max = min + new Vector2(width, height);
        EditorIcon.DrawCentered(drawList, icon, min, max, color with { W = contentAlpha });
        DrawTooltip(min, max, icon, tooltip, color);
        return min.X - (5f * ImGuiHelpers.GlobalScale);
    }

    private static void DrawLargeColorMarker(
        ImDrawListPtr drawList,
        ColorMarker marker,
        float left,
        float right,
        float y,
        Vector4 accent,
        bool selected,
        Vector4 textColor)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float swatchEdge = MathF.Max(12f * scale, ImGui.GetTextLineHeight() - (2f * scale));
        if (left + swatchEdge > right)
        {
            return;
        }

        Vector2 swatchMin = new(left, y + MathF.Max(0f, (ImGui.GetTextLineHeight() - swatchEdge) * 0.5f));
        Vector2 swatchMax = swatchMin + new Vector2(swatchEdge);
        drawList.AddRectFilled(swatchMin, swatchMax, ImGui.GetColorU32(marker.Color), 3f * scale);
        drawList.AddRect(swatchMin, swatchMax, ImGui.GetColorU32(accent with { W = selected ? 0.78f : 0.58f }), 3f * scale);

        float labelX = swatchMax.X + (6f * scale);
        if (labelX < right)
        {
            drawList.AddText(
                new Vector2(labelX, y),
                ImGui.GetColorU32(textColor),
                EditorTextUtility.ClipTextToWidth(marker.Label, right - labelX));
        }

        DrawTooltip(swatchMin, new Vector2(right, swatchMax.Y), marker.Tooltip, accent);
    }

    private static void DrawTooltip(Vector2 min, Vector2 max, string text, Vector4 accent)
    {
        if (IntonerTooltip.IsAreaHovered(min, max))
        {
            IntonerTooltip.DrawText(text, accent, 35f);
        }
    }

    private static void DrawTooltip(
        Vector2 min,
        Vector2 max,
        FontAwesomeIcon icon,
        string text,
        Vector4 accent)
    {
        if (IntonerTooltip.IsAreaHovered(min, max))
        {
            IntonerTooltip.DrawDescription(
                icon,
                text,
                options: new IntonerTooltipOptions { Accent = accent });
        }
    }

}
