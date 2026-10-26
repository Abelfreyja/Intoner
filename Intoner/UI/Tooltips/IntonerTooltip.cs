using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipOptions
{
    public Vector4? Accent { get; init; }
    public Vector4? TitleColor { get; init; }
    public Vector4? Background { get; init; }
    public Vector4? Border { get; init; }
    public Vector2? Padding { get; init; }
    public Vector2? ItemSpacing { get; init; }
    public float? Width { get; init; }
    public float? MaxWidth { get; init; }
    public float? Rounding { get; init; }
    public float? BorderWidth { get; init; }
    public float? WrapWidthEms { get; init; }
}

internal static class IntonerTooltip
{
    private const float DefaultRichWidth = 346f;
    private const float DefaultMaxWidth = 420f;
    private const float DefaultRounding = 4f;
    private const float DefaultBorderWidth = 1f;
    private const float DefaultWrapWidthEms = 34f;
    private const float DefaultPadding = 8f;

    private static int _suppressionDepth;

    public static bool IsSuppressed => _suppressionDepth > 0;

    public static bool IsAreaHovered(Vector2 min, Vector2 max)
        => !IsSuppressed
        && max.X > min.X
        && max.Y > min.Y
        && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
        && ImGui.IsMouseHoveringRect(min, max);

    /// <summary> suppresses tooltips while drawing UI covered by an overlay </summary>
    public static SuppressionScope Suppress(bool suppress = true)
    {
        if (suppress)
        {
            ++_suppressionDepth;
        }

        return new SuppressionScope(suppress);
    }

    public static void Attach(
        string text,
        IntonerTooltipOptions options = default,
        ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled)
    {
        if (!IsSuppressed && ImGui.IsItemHovered(hoverFlags))
        {
            DrawText(text, options);
        }
    }

    public static void Attach(
        FontAwesomeIcon icon,
        string title,
        string detail = "",
        IntonerTooltipOptions options = default,
        ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled)
    {
        if (!IsSuppressed && ImGui.IsItemHovered(hoverFlags))
        {
            DrawDescription(icon, title, detail, options);
        }
    }

    public static void DrawText(string text, IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string normalizedText = text.Trim();
        DrawResolved(
            () => IntonerTooltipContent.Label(null, normalizedText, options.Accent, options.TitleColor),
            ResolveTextStyle(ImGui.CalcTextSize(normalizedText).X, options));
    }

    public static void DrawText(
        string text,
        Vector4 accent,
        float wrapWidthEms = DefaultWrapWidthEms,
        float width = 0f)
        => DrawText(
            text,
            new IntonerTooltipOptions
            {
                Accent = accent,
                WrapWidthEms = wrapWidthEms,
                Width = width > 0f ? width : null,
            });

    public static void DrawDescription(
        FontAwesomeIcon icon,
        string title,
        string detail = "",
        IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string normalizedTitle = title.Trim();
        string normalizedDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
        if (normalizedDetail.Length == 0)
        {
            DrawContentSized(
                () => IntonerTooltipContent.Label(icon, normalizedTitle, options.Accent, options.TitleColor),
                IntonerTooltipContent.MeasureItemWidth(icon, normalizedTitle),
                options);
            return;
        }

        if (!options.Width.HasValue && !options.MaxWidth.HasValue)
        {
            options = options with { MaxWidth = DefaultRichWidth };
        }

        DrawContentSized(
            () => IntonerTooltipContent.Header(icon, normalizedTitle, normalizedDetail, options.Accent, options.TitleColor),
            IntonerTooltipContent.MeasureHeaderWidth(icon, normalizedTitle, normalizedDetail),
            options);
    }

    public static void DrawContentSized(
        Action content,
        float measuredContentWidth,
        IntonerTooltipOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (IsSuppressed)
        {
            return;
        }

        DrawResolved(content, ResolveContentStyle(measuredContentWidth, options));
    }

    public static void Draw(Action content, IntonerTooltipOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (IsSuppressed)
        {
            return;
        }

        DrawResolved(content, ResolveStyle(options));
    }

    public static StyleScope PushStyle(IntonerTooltipOptions options = default)
        => PushStyle(ResolveStyle(options));

    private static StyleScope PushStyle(ResolvedStyle style)
        => new(style.Background, style.Border, style.Padding, style.ItemSpacing, style.Rounding, style.BorderWidth);

    private static void DrawResolved(Action content, ResolvedStyle style)
    {
        if (IsSuppressed)
        {
            return;
        }

        using var enabled = ImRaii.Enabled();
        ApplySizeConstraints(style);

        using var appearance = PushStyle(style);
        using var tooltip = ImRaii.Tooltip();

        if (!tooltip.Alive)
        {
            return;
        }

        content();
    }

    public static void DrawOverlayText(
        ImDrawListPtr drawList,
        Vector2 anchor,
        string text,
        IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string normalizedText = text.Trim();
        Vector2 textSize = ImGui.CalcTextSize(normalizedText);
        ResolvedStyle style = ResolveTextStyle(textSize.X, options);
        float width = MathF.Floor(style.Width);
        float contentWidth = MathF.Max(1f, width - style.Padding.X * 2f);
        float wrapWidth = textSize.X > contentWidth ? contentWidth : 0f;

        if (wrapWidth > 0f)
        {
            textSize = ImGui.CalcTextSize(normalizedText, false, wrapWidth);
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        Vector2 size = Vector2.Min(new Vector2(width, MathF.Ceiling(textSize.Y + style.Padding.Y * 2f)), viewport.WorkSize);
        Vector2 min = Vector2.Clamp(anchor, viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - size);
        Vector2 max = min + size;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(ResolveHeaderBackground(style.Background)), style.Rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(style.Border),
            style.Rounding,
            ImDrawFlags.None,
            style.BorderWidth);
        Vector2 textMin = min + style.Padding;
        Vector2 textMax = max - style.Padding;
        drawList.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            textMin,
            ImGui.GetColorU32(options.TitleColor ?? ThemeColors.TextDefault),
            normalizedText,
            wrapWidth,
            new Vector4(textMin.X, textMin.Y, textMax.X, textMax.Y));
    }

    private static ResolvedStyle ResolveTextStyle(float measuredTextWidth, IntonerTooltipOptions options)
    {
        float wrapWidth = ImGui.GetFontSize()
            * (options.WrapWidthEms is > 0f ? options.WrapWidthEms.Value : DefaultWrapWidthEms);
        return ResolveContentStyle(MathF.Min(measuredTextWidth, wrapWidth), options);
    }

    private static ResolvedStyle ResolveContentStyle(float measuredContentWidth, IntonerTooltipOptions options)
    {
        ResolvedStyle style = ResolveStyle(options);

        if (options.Width is > 0f)
        {
            return style;
        }

        // imgui truncates window sizes, so keep fractional scaling from reducing the measured content width
        float width = MathF.Ceiling(MathF.Max(1f, measuredContentWidth) + style.Padding.X * 2f);
        return style with { Width = MathF.Min(width, style.MaxWidth) };
    }

    private static ResolvedStyle ResolveStyle(IntonerTooltipOptions options)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float viewportWidth = MathF.Max(1f, ImGui.GetMainViewport().WorkSize.X - (24f * scale));
        Vector4 accent = options.Accent ?? ThemeColors.AccentPrimary;
        Vector4 background = options.Background ?? ResolveBackground();
        float maxWidth = MathF.Min(
            (options.MaxWidth is > 0f ? options.MaxWidth.Value : DefaultMaxWidth) * scale,
            viewportWidth);
        float requestedWidth = (options.Width is > 0f ? options.Width.Value : DefaultRichWidth) * scale;
        float width = MathF.Min(requestedWidth, maxWidth);
        Vector2 padding = (options.Padding ?? new Vector2(DefaultPadding)) * scale;
        padding = new Vector2(MathF.Round(padding.X), MathF.Round(padding.Y));
        return new ResolvedStyle(
            background,
            options.Border ?? Vector4.Lerp(ThemeColors.Border, accent, 0.28f) with { W = 0.58f },
            padding,
            (options.ItemSpacing ?? new Vector2(6f, 6f)) * scale,
            width,
            maxWidth,
            (options.Rounding ?? DefaultRounding) * scale,
            (options.BorderWidth ?? DefaultBorderWidth) * scale);
    }

    internal static Vector4 ResolveBackground()
        => Vector4.Lerp(ThemeColors.WindowBg, ThemeColors.ButtonDefault, 0.48f) with { W = 0.99f };

    internal static Vector4 ResolveHeaderBackground(Vector4 background)
        => Vector4.Lerp(background, ThemeColors.TextDefault, 0.04f);

    private static void ApplySizeConstraints(ResolvedStyle style)
        => ImGui.SetNextWindowSizeConstraints(new Vector2(style.Width, 0f), new Vector2(style.Width, float.MaxValue));

    [StructLayout(LayoutKind.Auto)]
    public readonly ref struct StyleScope
    {
        private readonly ImRaii.StyleDisposable? _style;
        private readonly ImRaii.ColorDisposable? _colors;

        internal StyleScope(Vector4 background, Vector4 border, Vector2 padding, Vector2 spacing, float rounding, float borderWidth)
        {
            _style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, rounding)
                .Push(ImGuiStyleVar.PopupRounding, rounding)
                .Push(ImGuiStyleVar.WindowBorderSize, borderWidth)
                .Push(ImGuiStyleVar.PopupBorderSize, borderWidth)
                .Push(ImGuiStyleVar.WindowPadding, padding)
                .Push(ImGuiStyleVar.ItemSpacing, spacing)
                .Push(ImGuiStyleVar.CellPadding, IntonerTooltipContent.TableCellPadding);
            _colors = ImRaii.PushColor(ImGuiCol.PopupBg, background)
                .Push(ImGuiCol.Border, border)
                .Push(ImGuiCol.Text, ThemeColors.TextDefault)
                .Push(ImGuiCol.TextDisabled, Vector4.Lerp(ThemeColors.TextDisabledDefault, ThemeColors.TextDefault, 0.42f));
        }

        public void Dispose()
        {
            _colors?.Dispose();
            _style?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Auto)]
    public ref struct SuppressionScope
    {
        private bool _active;

        internal SuppressionScope(bool suppress)
        {
            _active = suppress;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            EndSuppression();
        }

        private static void EndSuppression()
            => --_suppressionDepth;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ResolvedStyle(
        Vector4 Background,
        Vector4 Border,
        Vector2 Padding,
        Vector2 ItemSpacing,
        float Width,
        float MaxWidth,
        float Rounding,
        float BorderWidth);
}
