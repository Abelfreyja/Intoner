using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Components;

internal static class EditorIcon
{
    public static void DrawInline(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Metrics(Vector2 BoundsMin, Vector2 Size, float Scale);

    public static unsafe Metrics Measure(FontAwesomeIcon icon, float scale = 1f)
    {
        string text = icon.ToIconString();
        ImFontGlyphPtr glyph = (ImFontGlyphPtr)UiBuilder.IconFont.FindGlyph(text[0]);
        if (!glyph.IsNull)
        {
            return new Metrics(
                new Vector2(glyph.X0 * scale, glyph.Y0 * scale),
                new Vector2((glyph.X1 - glyph.X0) * scale, (glyph.Y1 - glyph.Y0) * scale),
                scale);
        }

        float fontSize = UiBuilder.IconFont.FontSize * scale;
        Vector2 fallbackSize = ImGui.CalcTextSizeA(
            UiBuilder.IconFont,
            fontSize,
            float.MaxValue,
            0f,
            text,
            out _);
        return new Metrics(Vector2.Zero, fallbackSize, scale);
    }

    public static void Draw(
        ImDrawListPtr drawList,
        FontAwesomeIcon icon,
        Metrics metrics,
        Vector2 visiblePosition,
        Vector4 color)
    {
        Vector2 position = visiblePosition - metrics.BoundsMin;
        drawList.AddText(
            UiBuilder.IconFont,
            UiBuilder.IconFont.FontSize * metrics.Scale,
            position,
            ImGui.GetColorU32(color),
            icon.ToIconString());
    }

    public static void DrawCentered(
        ImDrawListPtr drawList,
        FontAwesomeIcon icon,
        Vector2 min,
        Vector2 max,
        Vector4 color,
        float scale = 1f)
    {
        Metrics metrics = Measure(icon, scale);
        Vector2 size = max - min;
        Draw(
            drawList,
            icon,
            metrics,
            min + new Vector2(
                MathF.Max(0f, (size.X - metrics.Size.X) * 0.5f),
                MathF.Max(0f, (size.Y - metrics.Size.Y) * 0.5f)),
            color);
    }
}
