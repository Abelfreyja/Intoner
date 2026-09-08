using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class FolderColorPicker
{
    private const float SwatchWidth = 18f;
    private const float SwatchHeight = 12f;
    private const float SwatchSpacing = 6f;

    private static readonly IReadOnlyList<string> Swatches =
    [
        string.Empty,
        ..EditorColors.FolderSwatches,
    ];

    public static Vector4 ResolveAccent(string colorValue)
        => ObjectFolderUtility.TryParseFolderColorValue(colorValue, out Vector4 color)
            ? color
            : ResolveDefaultAccent();

    public static string? Draw(string id, string currentColorValue)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = SwatchWidth * scale;
        float spacing = SwatchSpacing * scale;
        float rowWidth = (Swatches.Count * width) + ((Swatches.Count - 1) * spacing);
        string currentColor = ObjectFolderUtility.SanitizeFolderColorValue(currentColorValue);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX()
            + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - rowWidth) * 0.5f));

        for (int index = 0; index < Swatches.Count; ++index)
        {
            if (index > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            string colorValue = Swatches[index];
            bool selected = string.Equals(currentColor, colorValue, StringComparison.OrdinalIgnoreCase);
            if (DrawSwatch($"{id}:{index}", colorValue, selected))
            {
                return colorValue;
            }
        }

        return null;
    }

    private static bool DrawSwatch(string id, string colorValue, bool selected)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 size = new(SwatchWidth * scale, SwatchHeight * scale);
        bool clicked = ImGui.InvisibleButton(id, size);
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector4 fill = string.IsNullOrEmpty(colorValue)
            ? ThemeColors.ButtonDefault with { W = 0.96f }
            : ResolveAccent(colorValue) with { W = 0.96f };
        Vector4 border = selected
            ? ThemeColors.Text
            : ThemeColors.Border with { W = 0.78f };
        float rounding = 3f * scale;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            rounding,
            ImDrawFlags.None,
            selected ? 2f * scale : scale);
        if (string.IsNullOrEmpty(colorValue))
        {
            drawList.AddLine(
                new Vector2(min.X + (3f * scale), max.Y - (3f * scale)),
                new Vector2(max.X - (3f * scale), min.Y + (3f * scale)),
                ImGui.GetColorU32(ResolveDefaultAccent()),
                1.25f * scale);
        }

        if (ImGui.IsItemHovered())
        {
            string normalizedColor = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
            IntonerTooltip.DrawText(normalizedColor.Length == 0 ? "Default color" : $"Color {normalizedColor}");
        }

        return clicked;
    }

    private static Vector4 ResolveDefaultAccent()
        => ObjectFolderUtility.TryParseFolderColorValue(EditorColors.FolderPurple, out Vector4 color)
            ? color
            : ThemeColors.Color(0.6784f, 0.5412f, 0.9608f, 1f);
}
