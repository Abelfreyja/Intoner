using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal enum SplashScreenActionKind
{
    Layout,
    OpenLayouts,
    CloseLayouts,
    RecoverLastSession,
    Redirect,
}

internal readonly record struct SplashScreenActionRequest(SplashScreenActionKind Kind, Guid? LayoutId = null);

internal readonly record struct SplashScreenActionItem(
    SplashScreenActionKind Kind,
    FontAwesomeIcon Icon,
    string Label,
    string Detail,
    Vector4 Accent,
    Guid? LayoutId = null,
    bool Enabled = true,
    string Tooltip = "");

internal readonly record struct SplashScreenActionSection(string Title, IReadOnlyList<SplashScreenActionItem> Items);

internal static class SplashScreenActionList
{
    private const float SectionGap = 9f;
    private const float RowHeight = 25f;
    private const float RowRounding = 4f;
    private const float RowPadX = 8f;
    private const float IconColumnWidth = 20f;
    private const float DetailGap = 8f;
    private const float DetailWidthRatio = 0.48f;

    public static SplashScreenActionRequest? Draw(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        IReadOnlyList<SplashScreenActionSection> sections,
        float scale,
        out bool hovered)
    {
        hovered = false;
        if (!EditorInputUtility.HasArea(min, max))
        {
            return null;
        }

        SplashScreenActionRequest? request = null;
        float y = min.Y;
        drawList.PushClipRect(min, max, true);
        try
        {
            foreach (SplashScreenActionSection section in sections)
            {
                if (section.Items.Count == 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(section.Title))
                {
                    y = DrawSectionTitle(drawList, section.Title, new Vector2(min.X, y), max.X, scale);
                }

                foreach (SplashScreenActionItem item in section.Items)
                {
                    if (y + (RowHeight * scale) > max.Y)
                    {
                        return request;
                    }

                    var rowMin = new Vector2(min.X, y);
                    var rowMax = new Vector2(max.X, y + (RowHeight * scale));
                    request ??= DrawRow(drawList, rowMin, rowMax, item, scale, ref hovered);
                    y = rowMax.Y;
                }

                y += SectionGap * scale;
            }

            return request;
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    private static float DrawSectionTitle(ImDrawListPtr drawList, string title, Vector2 pos, float maxX, float scale)
    {
        string text = EditorTextUtility.ClipTextToWidth(title, MathF.Max(1f, maxX - pos.X));
        drawList.AddText(
            pos,
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.TextDisabled, 0.74f)),
            text);
        return pos.Y + ImGui.GetTextLineHeight() + (3f * scale);
    }

    private static SplashScreenActionRequest? DrawRow(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        SplashScreenActionItem item,
        float scale,
        ref bool hoveredAny)
    {
        bool hovered = EditorInputUtility.IsMouseInside(min, max);
        hoveredAny |= hovered;
        var rounding = RowRounding * scale;
        Vector4 textColor = item.Enabled
            ? EditorColors.Text
            : EditorColors.WithAlpha(EditorColors.TextDisabled, 0.56f);
        Vector4 detailColor = item.Enabled
            ? EditorColors.WithAlpha(EditorColors.TextDisabled, 0.84f)
            : EditorColors.WithAlpha(EditorColors.TextDisabled, 0.48f);
        Vector4 iconColor = item.Enabled
            ? item.Accent
            : EditorColors.WithAlpha(EditorColors.TextDisabled, 0.46f);

        if (hovered && item.Enabled)
        {
            drawList.AddRectFilled(
                min,
                max,
                ImGui.GetColorU32(EditorColors.WithAlpha(item.Accent, 0.11f)),
                rounding);
        }

        DrawRowContent(drawList, min, max, item, textColor, detailColor, iconColor, scale);
        if (hovered && !string.IsNullOrWhiteSpace(item.Tooltip))
        {
            UiSharedService.DrawAccentTooltipText(item.Tooltip, item.Accent, wrapEms: 35f);
        }

        drawList.AddLine(
            new Vector2(min.X + (RowPadX * scale), max.Y),
            new Vector2(max.X - (RowPadX * scale), max.Y),
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Separator, 0.20f)));

        return item.Enabled && EditorInputUtility.IsMouseClickedInside(min, max)
            ? new SplashScreenActionRequest(item.Kind, item.LayoutId)
            : null;
    }

    private static void DrawRowContent(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        SplashScreenActionItem item,
        Vector4 textColor,
        Vector4 detailColor,
        Vector4 iconColor,
        float scale)
    {
        string iconText = item.Icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        float padX = RowPadX * scale;
        float iconColumnWidth = IconColumnWidth * scale;
        float contentHeight = max.Y - min.Y;
        Vector2 iconPos = new(
            min.X + padX + ((iconColumnWidth - iconSize.X) * 0.5f),
            min.Y + ((contentHeight - iconSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(iconPos, ImGui.GetColorU32(iconColor), iconText);
        }

        float labelX = min.X + padX + iconColumnWidth + (5f * scale);
        float detailWidth = string.IsNullOrWhiteSpace(item.Detail)
            ? 0f
            : MathF.Min(ImGui.CalcTextSize(item.Detail).X, (max.X - min.X) * DetailWidthRatio);
        float detailX = max.X - padX - detailWidth;
        float labelWidth = MathF.Max(1f, detailX - labelX - (DetailGap * scale));
        string label = EditorTextUtility.ClipTextToWidth(item.Label, labelWidth);
        Vector2 labelSize = ImGui.CalcTextSize(label);
        float textY = min.Y + ((contentHeight - labelSize.Y) * 0.5f);
        drawList.AddText(new Vector2(labelX, textY), ImGui.GetColorU32(textColor), label);

        if (detailWidth <= 0f)
        {
            return;
        }

        string detail = EditorTextUtility.ClipTextToWidth(item.Detail, detailWidth);
        Vector2 detailSize = ImGui.CalcTextSize(detail);
        drawList.AddText(
            new Vector2(max.X - padX - detailSize.X, min.Y + ((contentHeight - detailSize.Y) * 0.5f)),
            ImGui.GetColorU32(detailColor),
            detail);
    }
}
