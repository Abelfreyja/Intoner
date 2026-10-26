using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Drawing;

/// <summary> text and optional labels </summary>
internal readonly record struct TextPrimitive(
    Vector2 Position, string Text, ImFontPtr Font, float FontSize, TextStyle Style, ScreenAnchor? Anchor)
{
    public Vector2 Measure()
        => ImGui.CalcTextSizeA(Font, FontSize, float.MaxValue, 0f, Text, out _);
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TextStyle(
    Vector4 Color,
    Vector2 Padding = default,
    Vector4 Background = default,
    Vector4 Border = default,
    float Rounding = 0f,
    float BorderThickness = 1f,
    float ExtraHeight = 0f)
{
    public void Draw(ImDrawListPtr drawList, Vector2 position, Vector2 textSize, ImFontPtr font, float fontSize, string text, float alpha)
    {
        Vector2 min = position - Padding;
        Vector2 max = position + textSize + Padding + new Vector2(0f, ExtraHeight);
        if (Background.W > 0f)
        {
            drawList.AddRectFilled(min, max, ToColor(Background, alpha), Rounding);
        }

        if (Border.W > 0f)
        {
            drawList.AddRect(min, max, ToColor(Border, alpha), Rounding, ImDrawFlags.None, BorderThickness);
        }

        drawList.AddText(font, fontSize, position, ToColor(Color, alpha), text);
    }

    private static uint ToColor(Vector4 color, float alpha)
        => ImGui.ColorConvertFloat4ToU32(color with { W = color.W * alpha });
}
