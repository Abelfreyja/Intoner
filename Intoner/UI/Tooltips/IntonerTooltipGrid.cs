using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipGridOptions
{
    public int Columns { get; init; }
    public float RowHeight { get; init; }
    public Vector4? TextColor { get; init; }
    public Vector4? AlternateRowColor { get; init; }
}

internal static class IntonerTooltipGrid
{
    private const float DefaultRowHeight = 21f;

    public static float MeasureHeight(int itemCount, IntonerTooltipGridOptions options = default)
    {
        if (itemCount <= 0)
        {
            return 0f;
        }

        int columns = ResolveColumns(options);
        int rows = (itemCount + columns - 1) / columns;
        return rows * ResolveRowHeight(options);
    }

    public static void Draw(IReadOnlyList<string> items, IntonerTooltipGridOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        int columns = ResolveColumns(options);
        int rowCount = (items.Count + columns - 1) / columns;
        float rowHeight = ResolveRowHeight(options);
        Vector2 start = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        float cellWidth = width / columns;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint textColor = ImGui.GetColorU32(options.TextColor ?? ThemeColors.Text);
        uint alternateRowColor = ImGui.GetColorU32(
            options.AlternateRowColor ?? ThemeColors.ButtonDefault with { W = 0.10f });

        for (int row = 0; row < rowCount; ++row)
        {
            float rowY = start.Y + (row * rowHeight);
            if ((row & 1) == 0)
            {
                drawList.AddRectFilled(
                    new Vector2(start.X, rowY),
                    new Vector2(start.X + width, rowY + rowHeight),
                    alternateRowColor);
            }

            for (int column = 0; column < columns; ++column)
            {
                int index = (row * columns) + column;
                if (index >= items.Count)
                {
                    break;
                }

                string label = items[index] ?? string.Empty;
                Vector2 textSize = ImGui.CalcTextSize(label);
                float cellX = start.X + (column * cellWidth);
                drawList.AddText(
                    new Vector2(
                        cellX + MathF.Max(0f, (cellWidth - textSize.X) * 0.5f),
                        rowY + MathF.Max(0f, (rowHeight - textSize.Y) * 0.5f)),
                    textColor,
                    label);
            }
        }

        ImGui.Dummy(new Vector2(width, rowCount * rowHeight));
    }

    private static int ResolveColumns(IntonerTooltipGridOptions options)
        => Math.Max(1, options.Columns);

    private static float ResolveRowHeight(IntonerTooltipGridOptions options)
        => MathF.Max(
            1f,
            (options.RowHeight > 0f ? options.RowHeight : DefaultRowHeight) * ImGuiHelpers.GlobalScale);
}
