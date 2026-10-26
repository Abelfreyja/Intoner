using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal sealed class EditorColumnPanel
{
    private readonly EditorOverlayLayer _editorOverlayLayer;
    internal const int FilterMaxLength = 128;

    public EditorColumnPanel(EditorOverlayLayer editorOverlayLayer)
    {
        _editorOverlayLayer = editorOverlayLayer;
    }

    internal static class PanelStyle
    {
        public const float HeaderHeight = 48f;
        public const float IconInset = 11f;
        public const float IconTextGap = 8f;
        public const float TitleInset = 7f;
        public const float SubtitleGap = 2f;
        public const float Rounding = 6f;

        public static Vector4 Panel => ThemeColors.ButtonDefault with { W = 0.20f };
        public static Vector4 Header => ThemeColors.ButtonDefault with { W = 0.20f };
        public static Vector4 Border => Vector4.Lerp(ThemeColors.Border, ThemeColors.AccentPrimary, 0.20f)
            with { W = 0.62f };
        public static Vector4 Divider => Vector4.Lerp(ThemeColors.Separator, ThemeColors.AccentPrimary, 0.12f)
            with { W = 0.52f };
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct HeaderLayout(
        Vector2 Min,
        Vector2 Max,
        float TextStartX,
        float Height);

    internal static float DrawHeaderBadges(
        HeaderLayout layout,
        string title,
        IReadOnlyList<EditorBadge> badges,
        float right,
        IReadOnlyList<EditorBadge>? compactBadges = null)
    {
        float gap = EditorLayout.Scaled(8f);
        float available = right - gap - layout.TextStartX;
        float titleWidth = ImGui.CalcTextSize(title).X;
        float badgeWidth = badges.Sum(EditorBadgeRenderer.MeasureWidth);

        if (compactBadges is not null && titleWidth + gap + badgeWidth > available)
        {
            badges = compactBadges;
            badgeWidth = badges.Sum(EditorBadgeRenderer.MeasureWidth);
        }

        float badgeLeft = layout.TextStartX + MathF.Min(titleWidth, MathF.Max(1f, available - gap - badgeWidth)) + gap;
        EditorBadgeRenderer.DrawAt(ImGui.GetWindowDrawList(), badges, badgeLeft,
            layout.Min.Y + (layout.Height - EditorBadgeRenderer.Height) * 0.5f,
            maxRight: right - gap, panelSurface: true);
        return badgeLeft;
    }

    internal static float DrawSearch(
        string id,
        string hint,
        ref string filter,
        int visibleCount,
        int totalCount,
        HeaderLayout layout,
        float right)
    {
        float left = MathF.Max(layout.TextStartX + EditorLayout.Scaled(190f), right - EditorLayout.Scaled(260f));
        float width = right - left;
        if (width < EditorLayout.Scaled(120f))
        {
            if (filter.Length == 0)
            {
                return right;
            }

            float buttonSize = EditorLayout.Scaled(32f);
            float buttonLeft = right - buttonSize;
            ImGui.SetCursorScreenPos(new Vector2(buttonLeft, layout.Min.Y + ((layout.Height - buttonSize) * 0.5f)));
            if (EditorIconButton.DrawAccent(
                    id,
                    FontAwesomeIcon.Times,
                    $"Clear filter: {filter}",
                    ThemeColors.AccentPrimary,
                    buttonSize))
            {
                filter = string.Empty;
            }

            return buttonLeft;
        }

        float height = EditorLayout.Scaled(32f);
        string status = filter.Length > 0 ? $"{visibleCount}/{totalCount}" : string.Empty;
        ImGui.SetCursorScreenPos(new Vector2(left, layout.Min.Y + ((layout.Height - height) * 0.5f)));
        _ = EditorSearchField.Draw(
            id,
            ref filter,
            new EditorSearchFieldOptions(
                hint,
                ThemeColors.AccentPrimary,
                StatusText: status,
                MaxLength: FilterMaxLength,
                Width: width,
                Height: height));
        return left;
    }

    internal void DrawPanel(
        string id,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Vector4 accent,
        float width,
        float height,
        Func<HeaderLayout, float> drawHeaderActions,
        Action<float> drawContent,
        Func<float, float>? drawNavigation = null,
        float contentHorizontalInset = 0f)
    {
        Vector2 startCursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, height);
        float rounding = EditorLayout.Scaled(PanelStyle.Rounding);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(PanelStyle.Panel), rounding);

        bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
        float headerHeight = EditorLayout.Scaled(hasSubtitle ? PanelStyle.HeaderHeight : 38f);
        Vector2 headerMax = new(max.X, min.Y + headerHeight);
        drawList.AddRectFilled(min, headerMax, ImGui.GetColorU32(PanelStyle.Header));
        drawList.AddLine(
            new Vector2(min.X, headerMax.Y),
            headerMax,
            ImGui.GetColorU32(PanelStyle.Divider),
            EditorLayout.Scaled(1f));

        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon, 0.92f);
        float iconX = min.X + EditorLayout.Scaled(PanelStyle.IconInset);
        float textX = iconX + iconMetrics.Size.X + EditorLayout.Scaled(PanelStyle.IconTextGap);
        float titleY = min.Y + (hasSubtitle
            ? EditorLayout.Scaled(PanelStyle.TitleInset)
            : (headerHeight - ImGui.GetTextLineHeight()) * 0.5f);
        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            new Vector2(iconX, min.Y + ((headerHeight - iconMetrics.Size.Y) * 0.5f)),
            accent);
        float textRight = drawHeaderActions(new HeaderLayout(min, headerMax, textX, headerHeight));
        float textWidth = MathF.Max(1f, MathF.Round(textRight - textX - EditorLayout.Scaled(PanelStyle.IconTextGap)));
        drawList.AddText(
            new Vector2(textX, titleY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(title, textWidth));

        if (hasSubtitle)
        {
            drawList.AddText(
                new Vector2(textX, titleY + ImGui.GetTextLineHeight() + EditorLayout.Scaled(PanelStyle.SubtitleGap)),
                ImGui.GetColorU32(ThemeColors.TextDisabled),
                EditorTextUtility.ClipTextToWidth(subtitle, textWidth));
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, headerMax.Y));
        float navigationHeight = drawNavigation?.Invoke(width) ?? 0f;
        float contentHeight = MathF.Max(0f, height - headerHeight - navigationHeight);
        if (contentHeight > 0f)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X + contentHorizontalInset, headerMax.Y + navigationHeight));
            using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
            using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            using var child = EditorScrollList.Begin(
                $"##{id}Scroll",
                new Vector2(EditorLayout.Positive(width - contentHorizontalInset * 2f), contentHeight),
                _editorOverlayLayer.CreateScrollPanelOptions(PanelStyle.Panel, rounding, accent));
            if (child)
            {
                drawContent(contentHeight);
            }
        }

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(PanelStyle.Border),
            rounding,
            ImDrawFlags.None,
            EditorLayout.Scaled(1f));
        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + height));
    }
}
