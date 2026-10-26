using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Components;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorIconTextOptions
{
    public bool AlignTitleToFramePadding { get; init; }
    public bool WrapTitle { get; init; }
    public bool WrapSubtitle { get; init; }
    public float? IconSpacing { get; init; }
    public float? LineSpacing { get; init; }
    public Vector4? TitleColor { get; init; }
    public Vector4? SubtitleColor { get; init; }
    public Action? DrawAfterTitle { get; init; }
    public Action? DrawAfterSubtitle { get; init; }
}

internal static class EditorIconText
{
    public static float MeasureWidth(
        FontAwesomeIcon? icon,
        string title,
        string subtitle,
        float? iconSpacing = null)
    {
        float textWidth = MathF.Max(ImGui.CalcTextSize(title).X, ImGui.CalcTextSize(subtitle).X);
        return icon is { } glyph
            ? MeasureIconSize(glyph).X + ResolveIconSpacing(iconSpacing) + textWidth
            : textWidth;
    }

    public static float MeasureHeight(
        FontAwesomeIcon? icon,
        string title,
        string subtitle,
        float availableWidth,
        EditorIconTextOptions options = default)
    {
        Vector2 iconSize = default;

        if (icon is { } glyph)
        {
            iconSize = MeasureIconSize(glyph);
            availableWidth -= iconSize.X + ResolveIconSpacing(options.IconSpacing);
        }

        float textWidth = MathF.Max(1f, availableWidth);
        float textHeight = MeasureTextHeight(title, options.WrapTitle, textWidth);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            float lineSpacing = options.LineSpacing is { } spacing
                ? spacing * ImGuiHelpers.GlobalScale
                : ImGui.GetStyle().ItemSpacing.Y;
            textHeight += lineSpacing + MeasureTextHeight(subtitle, options.WrapSubtitle, textWidth);
        }

        return MathF.Max(iconSize.Y, textHeight);
    }

    public static void Draw(
        FontAwesomeIcon? icon,
        string title,
        string subtitle,
        Vector4 accent,
        EditorIconTextOptions options = default)
    {
        using var spacing = options.LineSpacing is { } lineSpacing
            ? ImRaii.PushStyle(
                ImGuiStyleVar.ItemSpacing,
                new Vector2(ImGui.GetStyle().ItemSpacing.X, lineSpacing * ImGuiHelpers.GlobalScale))
            : default;
        if (icon is { } glyph)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.PushColor(ImGuiCol.Text, accent))
            {
                ImGui.TextUnformatted(glyph.ToIconString());
            }

            ImGui.SameLine(0f, ResolveIconSpacing(options.IconSpacing));
        }

        using var group = ImRaii.Group();

        if (options.AlignTitleToFramePadding)
        {
            ImGui.AlignTextToFramePadding();
        }

        DrawText(title, options.WrapTitle, options.TitleColor);
        options.DrawAfterTitle?.Invoke();

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            DrawText(
                subtitle,
                options.WrapSubtitle,
                options.SubtitleColor ?? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        options.DrawAfterSubtitle?.Invoke();
    }

    private static Vector2 MeasureIconSize(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString());
    }

    private static float ResolveIconSpacing(float? spacing)
        => spacing is { } value ? value * ImGuiHelpers.GlobalScale : ImGui.GetStyle().ItemSpacing.X;

    private static void DrawText(string text, bool wrap, Vector4? colorOverride)
    {
        using var color = colorOverride is { } value
            ? ImRaii.PushColor(ImGuiCol.Text, value)
            : default;
        // avoid native wrap rounding splitting text that already fits at the current font scale
        bool needsWrap = wrap && ImGui.CalcTextSize(text).X > ImGui.GetContentRegionAvail().X;
        float wrapPosition = needsWrap ? 0f : -1f;
        using var textWrap = wrap ? ImRaii.TextWrapPos(wrapPosition) : default;
        ImGui.TextUnformatted(text);
    }

    private static float MeasureTextHeight(string text, bool wrap, float width)
    {
        Vector2 size = ImGui.CalcTextSize(text);
        return wrap && size.X > width ? ImGui.CalcTextSize(text, false, width).Y : size.Y;
    }
}
