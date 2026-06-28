using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.UI.Theme;

internal sealed class IntonerThemeStyle
{
    private static readonly Vector4 AccentPurple = UIColors.Get("AccentPurple");
    private static readonly Vector4 AccentPurpleActive = UIColors.Get("AccentPurpleActive");
    private static readonly Vector4 ButtonDefault = UIColors.Get("ButtonDefault");

    private static readonly StyleColor[] ColorStyles =
    [
        new(ImGuiCol.Text,                  Rgba(255, 255, 255, 255)),
        new(ImGuiCol.TextDisabled,          Rgba(128, 128, 128, 255)),
        new(ImGuiCol.WindowBg,              Rgba(23,  23,  23,  248)),
        new(ImGuiCol.ChildBg,               Rgba(23,  23,  23,  66)),
        new(ImGuiCol.PopupBg,               Rgba(23,  23,  23,  248)),
        new(ImGuiCol.Border,                Rgba(65,  65,  65,  255)),
        new(ImGuiCol.BorderShadow,          Rgba(0,   0,   0,   150)),
        new(ImGuiCol.FrameBg,               Rgba(40,  40,  40,  255)),
        new(ImGuiCol.FrameBgHovered,        Rgba(50,  50,  50,  100)),
        new(ImGuiCol.FrameBgActive,         Rgba(30,  30,  30,  255)),
        new(ImGuiCol.TitleBg,               Rgba(24,  24,  24,  232)),
        new(ImGuiCol.TitleBgActive,         Rgba(30,  30,  30,  255)),
        new(ImGuiCol.TitleBgCollapsed,      Rgba(27,  27,  27,  255)),
        new(ImGuiCol.MenuBarBg,             Rgba(36,  36,  36,  255)),
        new(ImGuiCol.ScrollbarBg,           Rgba(0,   0,   0,   0)),
        new(ImGuiCol.ScrollbarGrab,         Rgba(62,  62,  62,  255)),
        new(ImGuiCol.ScrollbarGrabHovered,  Rgba(70,  70,  70,  255)),
        new(ImGuiCol.ScrollbarGrabActive,   Rgba(70,  70,  70,  255)),
        new(ImGuiCol.CheckMark,             AccentPurple),
        new(ImGuiCol.SliderGrab,            Rgba(101, 101, 101, 255)),
        new(ImGuiCol.SliderGrabActive,      Rgba(123, 123, 123, 255)),
        new(ImGuiCol.Button,                ButtonDefault),
        new(ImGuiCol.ButtonHovered,         AccentPurple),
        new(ImGuiCol.ButtonActive,          AccentPurpleActive),
        new(ImGuiCol.Header,                ButtonDefault),
        new(ImGuiCol.HeaderHovered,         AccentPurple),
        new(ImGuiCol.HeaderActive,          AccentPurpleActive),
        new(ImGuiCol.Separator,             Rgba(75,  75,  75,  121)),
        new(ImGuiCol.SeparatorHovered,      AccentPurple),
        new(ImGuiCol.SeparatorActive,       AccentPurpleActive),
        new(ImGuiCol.ResizeGrip,            Rgba(0,   0,   0,   0)),
        new(ImGuiCol.ResizeGripHovered,     Rgba(0,   0,   0,   0)),
        new(ImGuiCol.ResizeGripActive,      AccentPurpleActive),
        new(ImGuiCol.Tab,                   Rgba(40,  40,  40,  255)),
        new(ImGuiCol.TabHovered,            AccentPurple),
        new(ImGuiCol.TabActive,             AccentPurpleActive),
        new(ImGuiCol.TabUnfocused,          Rgba(40,  40,  40,  255)),
        new(ImGuiCol.TabUnfocusedActive,    AccentPurpleActive),
        new(ImGuiCol.DockingPreview,        AccentPurpleActive),
        new(ImGuiCol.DockingEmptyBg,        Rgba(50,  50,  50,  255)),
        new(ImGuiCol.PlotLines,             Rgba(150, 150, 150, 255)),
        new(ImGuiCol.TableHeaderBg,         Rgba(48,  48,  48,  255)),
        new(ImGuiCol.TableBorderStrong,     Rgba(79,  79,  89,  255)),
        new(ImGuiCol.TableBorderLight,      Rgba(59,  59,  64,  255)),
        new(ImGuiCol.TableRowBg,            Rgba(0,   0,   0,   0)),
        new(ImGuiCol.TableRowBgAlt,         Rgba(255, 255, 255, 15)),
        new(ImGuiCol.TextSelectedBg,        Rgba(173, 138, 245, 255)),
        new(ImGuiCol.DragDropTarget,        Rgba(173, 138, 245, 255)),
        new(ImGuiCol.NavHighlight,          Rgba(173, 138, 245, 179)),
        new(ImGuiCol.NavWindowingDimBg,     Rgba(204, 204, 204, 51)),
        new(ImGuiCol.NavWindowingHighlight, Rgba(204, 204, 204, 89)),
    ];

    private static readonly StyleVector[] VectorStyles =
    [
        new(ImGuiStyleVar.WindowPadding,    new Vector2(6f, 6f)),
        new(ImGuiStyleVar.FramePadding,     new Vector2(4f, 3f)),
        new(ImGuiStyleVar.CellPadding,      new Vector2(4f, 4f)),
        new(ImGuiStyleVar.ItemSpacing,      new Vector2(4f, 4f)),
        new(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4f, 4f)),
    ];

    private static readonly StyleFloat[] FloatStyles =
    [
        new(ImGuiStyleVar.IndentSpacing,     21f),
        new(ImGuiStyleVar.ScrollbarSize,     10f),
        new(ImGuiStyleVar.GrabMinSize,       20f),
        new(ImGuiStyleVar.WindowBorderSize,  1.5f),
        new(ImGuiStyleVar.ChildBorderSize,   1.5f),
        new(ImGuiStyleVar.PopupBorderSize,   1.5f),
        new(ImGuiStyleVar.FrameBorderSize,   0f),
        new(ImGuiStyleVar.WindowRounding,    7f),
        new(ImGuiStyleVar.ChildRounding,     4f),
        new(ImGuiStyleVar.FrameRounding,     4f),
        new(ImGuiStyleVar.PopupRounding,     4f),
        new(ImGuiStyleVar.ScrollbarRounding, 4f),
        new(ImGuiStyleVar.GrabRounding,      4f),
        new(ImGuiStyleVar.TabRounding,       4f),
    ];

    public Scope Push()
    {
        var colorCount = 0;
        var styleVarCount = 0;

        foreach (StyleColor style in ColorStyles)
        {
            ImGui.PushStyleColor(style.Target, style.Value);
            colorCount++;
        }

        foreach (StyleVector style in VectorStyles)
        {
            ImGui.PushStyleVar(style.Target, style.Value);
            styleVarCount++;
        }

        foreach (StyleFloat style in FloatStyles)
        {
            ImGui.PushStyleVar(style.Target, style.Value);
            styleVarCount++;
        }

        return new Scope(colorCount, styleVarCount);
    }

    private static void Pop(int colorCount, int styleVarCount)
    {
        if (styleVarCount > 0)
        {
            ImGui.PopStyleVar(styleVarCount);
        }

        if (colorCount > 0)
        {
            ImGui.PopStyleColor(colorCount);
        }
    }

    private static Vector4 Rgba(byte red, byte green, byte blue, byte alpha = 255)
        => new(red / 255f, green / 255f, blue / 255f, alpha / 255f);

    private readonly record struct StyleColor(ImGuiCol Target, Vector4 Value);
    private readonly record struct StyleVector(ImGuiStyleVar Target, Vector2 Value);
    private readonly record struct StyleFloat(ImGuiStyleVar Target, float Value);

    public readonly struct Scope : IDisposable
    {
        private readonly int _colorCount;
        private readonly int _styleVarCount;

        public Scope(int colorCount, int styleVarCount)
        {
            _colorCount = colorCount;
            _styleVarCount = styleVarCount;
        }

        public void Dispose()
            => Pop(_colorCount, _styleVarCount);
    }
}
