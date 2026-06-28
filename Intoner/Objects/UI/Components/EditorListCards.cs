using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private readonly record struct ListEntryCardInteraction(bool Clicked, bool Hovered, Vector2 Min, Vector2 Max);

    private bool DrawObjectListEntryCard(
        string id,
        string title,
        string detail,
        string badgeText,
        string? badgeTooltip,
        bool selected,
        Vector4 accent,
        float height)
    {
        var startPos = ImGui.GetCursorPos();
        var insetX = Scaled(4f);
        var width = Positive(ImGui.GetContentRegionAvail().X - (insetX * 2f));

        ImGui.SetCursorPosX(startPos.X + insetX);
        ListEntryCardInteraction interaction = DrawListEntryCardInteraction(id, selected, new Vector2(width, height));

        var endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));

        var drawList = ImGui.GetWindowDrawList();
        var min = interaction.Min;
        var max = interaction.Max;
        var text = EditorColors.Text;
        var padX = Scaled(10f);
        var padY = Scaled(8f);

        DrawListEntryCardChrome(drawList, min, max, selected, interaction.Hovered, accent);

        var badgePaddingX = Scaled(7f);
        var badgePaddingY = Scaled(3f);
        var badgeTextSize = ImGui.CalcTextSize(badgeText);
        var badgeMin = new Vector2(max.X - badgeTextSize.X - (badgePaddingX * 2f) - padX, min.Y + padY);
        var badgeMax = new Vector2(max.X - padX, badgeMin.Y + badgeTextSize.Y + (badgePaddingY * 2f));
        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(accent with { W = selected ? 0.36f : 0.22f }), 999f);
        drawList.AddText(new Vector2(badgeMin.X + badgePaddingX, badgeMin.Y + badgePaddingY), ImGui.GetColorU32(text), badgeText);

        if (!string.IsNullOrWhiteSpace(badgeTooltip) && EditorInputUtility.IsMouseInside(badgeMin, badgeMax))
        {
            UiSharedService.DrawAccentTooltipText(badgeTooltip, accent, wrapEms: 35f);
        }

        var textWidth = MathF.Max(ResolveMinimumCardTextWidth(), badgeMin.X - min.X - (padX * 2f));
        drawList.AddText(new Vector2(min.X + padX, min.Y + padY), ImGui.GetColorU32(text), ClipTextToWidth(title, textWidth));

        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + Scaled(4f);
        drawList.AddText(
            new Vector2(min.X + padX, metaY),
            ImGui.GetColorU32(EditorColors.TextDisabled with { W = 0.88f }),
            ClipTextToWidth(detail, textWidth));

        return interaction.Clicked;
    }

    private static ListEntryCardInteraction DrawListEntryCardInteraction(
        string id,
        bool selected,
        Vector2 size,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        using var header = ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero);
        using var hovered = ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero);
        using var active = ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero);

        var clicked = ImGui.Selectable(
            $"##{id}",
            selected,
            flags,
            size);

        return new(
            clicked,
            ImGui.IsItemHovered(),
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax());
    }

    private void DrawListEntryCardChrome(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool hovered,
        Vector4 accent)
    {
        var fill = selected
            ? accent with { W = 0.17f }
            : hovered
                ? accent with { W = 0.08f }
                : EditorColors.ButtonDefault with { W = 0.22f };
        var border = selected
            ? accent with { W = 0.85f }
            : hovered
                ? accent with { W = 0.58f }
                : EditorColors.Border with { W = 0.38f };
        var rounding = Scaled(8f);

        DrawListEntryCardFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = selected ? 0.95f : hovered ? 0.72f : 0.55f },
            rounding,
            hovered && !selected);
    }

    private void DrawListEntryCardFrame(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 fill,
        Vector4 border,
        Vector4 rail,
        float rounding,
        bool showEdgeGlow)
    {
        const ImDrawFlags cornerFlags = ImDrawFlags.RoundCornersRight;
        var railWidth = Scaled(3f);

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding, cornerFlags);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            rounding,
            cornerFlags,
            Scaled(1f));
        drawList.AddRectFilled(
            min,
            new Vector2(min.X + railWidth, max.Y),
            ImGui.GetColorU32(rail),
            0f);

        if (showEdgeGlow)
        {
            DrawListEntryEdgeGlow(min, max, rounding, cornerFlags);
        }
    }

    private void DrawListEntryEdgeGlow(Vector2 min, Vector2 max, float rounding, ImDrawFlags cornerFlags)
        => _edgeGlowRenderer.DrawRect(
            min,
            max,
            rounding,
            CreateListEntryEdgeGlowStyle(cornerFlags));

    private static EdgeGlowStyle CreateListEntryEdgeGlowStyle(ImDrawFlags cornerFlags)
        => new EdgeGlowStyle
        {
            Mode = EdgeGlowMode.FullBorder,
            ColorVariant = EdgeGlowColorVariant.Colorful,
            Theme = EdgeGlowTheme.Dark,
            BorderInset = 0.35f,
            BorderWidth = 1.15f,
            Duration = 2.8f,
            Strength = 0.70f,
            Brightness = 1.18f,
            Saturation = 1.14f,
            HueRange = 14f,
            StrokeOpacity = 0.46f,
            InnerOpacity = 0.22f,
            BloomOpacity = 0.34f,
            InnerShadowAlpha = 0.03f,
            RenderScale = 0.62f,
            FullBorderInnerReachScale = 1.20f,
            FullBorderSweepScale = 0.82f,
            CornerFlags = cornerFlags,
            ClipToRect = true,
            ClipPadding = 5f,
        };
}
