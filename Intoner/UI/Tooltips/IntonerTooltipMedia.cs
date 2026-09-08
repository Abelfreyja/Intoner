using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.UI.Performance;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipMediaRow
{
    public required Vector2 MediaSize { get; init; }
    public required Action<ImDrawListPtr, Vector2, Vector2> DrawMedia { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Metadata { get; init; }
    public required string Trailing { get; init; }
    public Vector4? Accent { get; init; }
}

internal static class IntonerTooltipMedia
{
    private const float TextSpacing = 2f;
    private const float EndPadding = 8f;

    public static float MeasureRowHeight(Vector2 mediaSize)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float textHeight = (ImGui.GetTextLineHeight() * 3f) + (TextSpacing * 2f * scale);
        return MathF.Max(mediaSize.Y * scale, textHeight);
    }

    public static void DrawRow(in IntonerTooltipMediaRow row)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector4 accent = row.Accent ?? ThemeColors.AccentPrimary;
        float mediaWidth = row.MediaSize.X * scale;
        float endPadding = EndPadding * scale;
        Vector2 start = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        float rowHeight = MeasureRowHeight(row.MediaSize);
        Vector2 mediaMin = start;
        Vector2 mediaMax = new(start.X + mediaWidth, start.Y + rowHeight);
        Vector2 rowMax = new(start.X + width, start.Y + rowHeight);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            rowMax,
            ImGui.GetColorU32(ThemeColors.ButtonDefault with { W = 0.16f }));
        row.DrawMedia(drawList, mediaMin, mediaMax);
        drawList.AddRectFilled(
            new Vector2(mediaMax.X, start.Y),
            new Vector2(mediaMax.X + MathF.Max(scale, 1f), rowMax.Y),
            ImGui.GetColorU32(accent with { W = 0.40f }));

        float textX = mediaMax.X + (11f * scale);
        float textWidth = MathF.Max(1f, start.X + width - textX - endPadding);
        float lineHeight = ImGui.GetTextLineHeight();
        float lineSpacing = TextSpacing * scale;
        int visibleLines = 1
                         + (string.IsNullOrWhiteSpace(row.Subtitle) ? 0 : 1)
                         + (string.IsNullOrWhiteSpace(row.Metadata) ? 0 : 1);
        float visibleTextHeight = (lineHeight * visibleLines) + (lineSpacing * (visibleLines - 1));
        float textY = start.Y + MathF.Max(0f, (rowHeight - visibleTextHeight) * 0.5f);
        Vector2 trailingSize = string.IsNullOrWhiteSpace(row.Trailing)
            ? Vector2.Zero
            : ImGui.CalcTextSize(row.Trailing);
        float titleWidth = MathF.Max(
            1f,
            textWidth - (trailingSize.X > 0f ? trailingSize.X + (8f * scale) : 0f));

        drawList.AddText(
            new Vector2(textX, textY),
            ImGui.GetColorU32(ThemeColors.Text),
            UiText.ClipToWidth(row.Title ?? string.Empty, titleWidth));
        if (trailingSize.X > 0f)
        {
            drawList.AddText(
                new Vector2(rowMax.X - endPadding - trailingSize.X, textY),
                ImGui.GetColorU32(ThemeColors.Text),
                row.Trailing ?? string.Empty);
        }

        textY += lineHeight + lineSpacing;
        if (!string.IsNullOrWhiteSpace(row.Subtitle))
        {
            drawList.AddText(
                new Vector2(textX, textY),
                ImGui.GetColorU32(ThemeColors.TextDisabled with { W = 0.90f }),
                UiText.ClipToWidth(row.Subtitle, textWidth));
            textY += lineHeight + lineSpacing;
        }

        if (!string.IsNullOrWhiteSpace(row.Metadata))
        {
            drawList.AddText(
                new Vector2(textX, textY),
                ImGui.GetColorU32(ThemeColors.Text),
                UiText.ClipToWidth(row.Metadata, textWidth));
        }

        float dividerY = start.Y + rowHeight;
        drawList.AddLine(
            new Vector2(start.X, dividerY),
            new Vector2(start.X + width, dividerY),
            ImGui.GetColorU32(ThemeColors.Separator with { W = 0.26f }),
            MathF.Max(scale, 1f));
        ImGui.Dummy(new Vector2(width, rowHeight));
    }
}
