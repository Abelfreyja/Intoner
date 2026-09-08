using Dalamud.Bindings.ImGui;
using Intoner.Services.Configuration;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Theme;

internal sealed class IntonerThemeStyle : IDisposable
{
    private readonly IIntonerConfigurationService _configuration;
    private ThemeColorPalette _palette;
    private ThemeColorPalette? _previewPalette;

    private static readonly StyleColor[] ColorStyles =
    [
        new(ImGuiCol.Text,                  ThemeColors.TextDefault),
        new(ImGuiCol.TextDisabled,          ThemeColors.TextDisabledDefault),
        new(ImGuiCol.WindowBg,              ThemeColors.FromBytes(23,  23,  23,  248)),
        new(ImGuiCol.ChildBg,               ThemeColors.FromBytes(23,  23,  23,  66)),
        new(ImGuiCol.PopupBg,               ThemeColors.FromBytes(23,  23,  23,  248)),
        new(ImGuiCol.Border,                ThemeColors.FromBytes(65,  65,  65,  255)),
        new(ImGuiCol.BorderShadow,          ThemeColors.FromBytes(0,   0,   0,   150)),
        new(ImGuiCol.FrameBg,               ThemeColors.FromBytes(40,  40,  40,  255)),
        new(ImGuiCol.FrameBgHovered,        ThemeColors.FromBytes(50,  50,  50,  100)),
        new(ImGuiCol.FrameBgActive,         ThemeColors.FromBytes(30,  30,  30,  255)),
        new(ImGuiCol.TitleBg,               ThemeColors.FromBytes(24,  24,  24,  232)),
        new(ImGuiCol.TitleBgActive,         ThemeColors.FromBytes(30,  30,  30,  255)),
        new(ImGuiCol.TitleBgCollapsed,      ThemeColors.FromBytes(27,  27,  27,  255)),
        new(ImGuiCol.MenuBarBg,             ThemeColors.FromBytes(36,  36,  36,  255)),
        new(ImGuiCol.ScrollbarBg,           ThemeColors.FromBytes(0,   0,   0,   0)),
        new(ImGuiCol.ScrollbarGrab,         ThemeColors.FromBytes(62,  62,  62,  255)),
        new(ImGuiCol.ScrollbarGrabHovered,  ThemeColors.FromBytes(70,  70,  70,  255)),
        new(ImGuiCol.ScrollbarGrabActive,   ThemeColors.FromBytes(70,  70,  70,  255)),
        StyleColor.Primary(ImGuiCol.CheckMark),
        new(ImGuiCol.SliderGrab,            ThemeColors.FromBytes(101, 101, 101, 255)),
        new(ImGuiCol.SliderGrabActive,      ThemeColors.FromBytes(123, 123, 123, 255)),
        new(ImGuiCol.Button,                ThemeColors.ButtonDefault),
        StyleColor.Primary(ImGuiCol.ButtonHovered),
        StyleColor.Active(ImGuiCol.ButtonActive),
        new(ImGuiCol.Header,                ThemeColors.ButtonDefault),
        StyleColor.Primary(ImGuiCol.HeaderHovered),
        StyleColor.Active(ImGuiCol.HeaderActive),
        new(ImGuiCol.Separator,             ThemeColors.FromBytes(75,  75,  75,  121)),
        StyleColor.Primary(ImGuiCol.SeparatorHovered),
        StyleColor.Active(ImGuiCol.SeparatorActive),
        new(ImGuiCol.ResizeGrip,            ThemeColors.FromBytes(0,   0,   0,   0)),
        new(ImGuiCol.ResizeGripHovered,     ThemeColors.FromBytes(0,   0,   0,   0)),
        StyleColor.Active(ImGuiCol.ResizeGripActive),
        new(ImGuiCol.Tab,                   ThemeColors.FromBytes(40,  40,  40,  255)),
        StyleColor.Primary(ImGuiCol.TabHovered),
        StyleColor.Active(ImGuiCol.TabActive),
        new(ImGuiCol.TabUnfocused,          ThemeColors.FromBytes(40,  40,  40,  255)),
        StyleColor.Active(ImGuiCol.TabUnfocusedActive),
        StyleColor.Active(ImGuiCol.DockingPreview),
        new(ImGuiCol.DockingEmptyBg,        ThemeColors.FromBytes(50,  50,  50,  255)),
        new(ImGuiCol.PlotLines,             ThemeColors.FromBytes(150, 150, 150, 255)),
        new(ImGuiCol.TableHeaderBg,         ThemeColors.FromBytes(48,  48,  48,  255)),
        new(ImGuiCol.TableBorderStrong,     ThemeColors.FromBytes(79,  79,  89,  255)),
        new(ImGuiCol.TableBorderLight,      ThemeColors.FromBytes(59,  59,  64,  255)),
        new(ImGuiCol.TableRowBg,            ThemeColors.FromBytes(0,   0,   0,   0)),
        new(ImGuiCol.TableRowBgAlt,         ThemeColors.FromBytes(255, 255, 255, 15)),
        StyleColor.Primary(ImGuiCol.TextSelectedBg),
        StyleColor.Primary(ImGuiCol.DragDropTarget),
        StyleColor.Primary(ImGuiCol.NavHighlight, 0.70f),
        new(ImGuiCol.NavWindowingDimBg,     ThemeColors.FromBytes(204, 204, 204, 51)),
        new(ImGuiCol.NavWindowingHighlight, ThemeColors.FromBytes(204, 204, 204, 89)),
    ];

    private static readonly StyleVector[] VectorStyles =
    [
        new(ImGuiStyleVar.WindowPadding,    new Vector2(6f, 6f)),
        new(ImGuiStyleVar.FramePadding,     new Vector2(4f, 4f)),
        new(ImGuiStyleVar.CellPadding,      new Vector2(4f, 4f)),
        new(ImGuiStyleVar.ItemSpacing,      new Vector2(4f, 4f)),
        new(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4f, 4f)),
    ];

    private static readonly StyleFloat[] FloatStyles =
    [
        new(ImGuiStyleVar.IndentSpacing,     21f),
        new(ImGuiStyleVar.ScrollbarSize,     10f),
        new(ImGuiStyleVar.GrabMinSize,       20f),
        new(ImGuiStyleVar.WindowBorderSize,  1f),
        new(ImGuiStyleVar.ChildBorderSize,   1.5f),
        new(ImGuiStyleVar.PopupBorderSize,   1.5f),
        new(ImGuiStyleVar.FrameBorderSize,   0f),
        new(ImGuiStyleVar.WindowRounding,    8f),
        new(ImGuiStyleVar.ChildRounding,     4f),
        new(ImGuiStyleVar.FrameRounding,     4f),
        new(ImGuiStyleVar.PopupRounding,     4f),
        new(ImGuiStyleVar.ScrollbarRounding, 4f),
        new(ImGuiStyleVar.GrabRounding,      4f),
        new(ImGuiStyleVar.TabRounding,       4f),
    ];

    public IntonerThemeStyle(IIntonerConfigurationService configuration)
    {
        _configuration = configuration;
        _palette = ResolvePalette();
        _configuration.ConfigurationChanged += HandleConfigurationChanged;
    }

    public void Dispose()
        => _configuration.ConfigurationChanged -= HandleConfigurationChanged;

    public Scope Push()
    {
        ThemeColorPalette palette = TakeFramePalette();

        foreach (StyleColor style in ColorStyles)
        {
            ImGui.PushStyleColor(style.Target, style.Resolve(palette));
        }

        foreach (StyleVector style in VectorStyles)
        {
            ImGui.PushStyleVar(style.Target, style.Value);
        }

        foreach (StyleFloat style in FloatStyles)
        {
            ImGui.PushStyleVar(style.Target, style.Value);
        }

        return new Scope(ColorStyles.Length, VectorStyles.Length + FloatStyles.Length);
    }

    public void PreviewPrimaryColor(RgbColor color)
        => Volatile.Write(ref _previewPalette, ThemeColors.GetCustomThemePalette(color));

    internal ThemeColorPalette TakeFramePalette()
        => Interlocked.Exchange(ref _previewPalette, null) ?? Volatile.Read(ref _palette);

    private void HandleConfigurationChanged()
    {
        Volatile.Write(ref _palette, ResolvePalette());
        Volatile.Write(ref _previewPalette, null);
    }

    private ThemeColorPalette ResolvePalette()
        => ThemeColors.GetThemePalette(_configuration.Current.Ui.Theme.PrimaryColor);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct StyleColor(
        ImGuiCol Target,
        Vector4 Value,
        ThemeColorRole ColorRole = ThemeColorRole.Fixed,
        float Opacity = 1f)
    {
        public static StyleColor Primary(ImGuiCol target, float opacity = 1f)
            => new(target, default, ThemeColorRole.Primary, opacity);

        public static StyleColor Active(ImGuiCol target)
            => new(target, default, ThemeColorRole.Active);

        public Vector4 Resolve(ThemeColorPalette palette)
        {
            Vector4 color = ColorRole switch
            {
                ThemeColorRole.Primary => palette.Primary,
                ThemeColorRole.Active => palette.Active,
                _ => Value,
            };
            return color with { W = color.W * Opacity };
        }
    }

    private enum ThemeColorRole
    {
        Fixed,
        Primary,
        Active,
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct StyleVector(ImGuiStyleVar Target, Vector2 Value);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct StyleFloat(ImGuiStyleVar Target, float Value);

    [StructLayout(LayoutKind.Auto)]
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
        {
            if (_styleVarCount > 0)
            {
                ImGui.PopStyleVar(_styleVarCount);
            }

            if (_colorCount > 0)
            {
                ImGui.PopStyleColor(_colorCount);
            }
        }
    }
}
