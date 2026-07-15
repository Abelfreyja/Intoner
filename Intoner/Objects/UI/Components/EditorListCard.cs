using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class EditorListCard(EdgeGlowRenderer edgeGlowRenderer)
{
    public readonly record struct Interaction(bool Clicked, bool Hovered, Vector2 Min, Vector2 Max);

    public static float MinimumTextWidth => 40f * ImGuiHelpers.GlobalScale;

    public bool Draw(
        string id,
        string title,
        string detail,
        string badgeText,
        string? badgeTooltip,
        bool selected,
        Vector4 accent,
        float height,
        Action drawContextMenu)
    {
        Vector2 startPos = ImGui.GetCursorPos();
        float insetX = 4f * ImGuiHelpers.GlobalScale;
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (insetX * 2f));

        ImGui.SetCursorPosX(startPos.X + insetX);
        Interaction interaction = DrawInteraction(id, selected, new Vector2(width, height));
        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##{id}:context"))
        {
            if (contextMenu)
            {
                drawContextMenu();
            }
        }

        Vector2 endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 min = interaction.Min;
        Vector2 max = interaction.Max;
        float scale = ImGuiHelpers.GlobalScale;
        float padX = 10f * scale;
        float padY = 8f * scale;

        DrawChrome(drawList, min, max, selected, interaction.Hovered, accent);

        float badgePaddingX = 7f * scale;
        float badgePaddingY = 3f * scale;
        Vector2 badgeTextSize = ImGui.CalcTextSize(badgeText);
        Vector2 badgeMin = new(max.X - badgeTextSize.X - (badgePaddingX * 2f) - padX, min.Y + padY);
        Vector2 badgeMax = new(max.X - padX, badgeMin.Y + badgeTextSize.Y + (badgePaddingY * 2f));
        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(accent with { W = selected ? 0.36f : 0.22f }), 999f);
        drawList.AddText(new Vector2(badgeMin.X + badgePaddingX, badgeMin.Y + badgePaddingY), ImGui.GetColorU32(EditorColors.Text), badgeText);

        if (!string.IsNullOrWhiteSpace(badgeTooltip) && EditorInputUtility.IsMouseInside(badgeMin, badgeMax))
        {
            UiSharedService.DrawAccentTooltipText(badgeTooltip, accent, wrapEms: 35f);
        }

        float textWidth = MathF.Max(MinimumTextWidth, badgeMin.X - min.X - (padX * 2f));
        drawList.AddText(
            new Vector2(min.X + padX, min.Y + padY),
            ImGui.GetColorU32(EditorColors.Text),
            EditorTextUtility.ClipTextToWidth(title, textWidth));

        float metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * scale);
        drawList.AddText(
            new Vector2(min.X + padX, metaY),
            ImGui.GetColorU32(EditorColors.TextDisabled with { W = 0.88f }),
            EditorTextUtility.ClipTextToWidth(detail, textWidth));

        return interaction.Clicked;
    }

    public static Interaction DrawInteraction(
        string id,
        bool selected,
        Vector2 size,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        using var header = ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero);
        using var hovered = ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero);
        using var active = ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero);

        bool clicked = ImGui.Selectable($"##{id}", selected, flags, size);
        return new Interaction(
            clicked,
            ImGui.IsItemHovered(),
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax());
    }

    public void DrawChrome(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool hovered,
        Vector4 accent)
    {
        Vector4 fill = selected
            ? accent with { W = 0.17f }
            : hovered
                ? accent with { W = 0.08f }
                : EditorColors.ButtonDefault with { W = 0.22f };
        Vector4 border = selected
            ? accent with { W = 0.85f }
            : hovered
                ? accent with { W = 0.58f }
                : EditorColors.Border with { W = 0.38f };

        DrawFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = selected ? 0.95f : hovered ? 0.72f : 0.55f },
            8f * ImGuiHelpers.GlobalScale,
            hovered && !selected);
    }

    public void DrawFrame(
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
        float scale = ImGuiHelpers.GlobalScale;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding, cornerFlags);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            rounding,
            cornerFlags,
            scale);
        drawList.AddRectFilled(
            min,
            new Vector2(min.X + (3f * scale), max.Y),
            ImGui.GetColorU32(rail),
            0f);

        if (showEdgeGlow)
        {
            edgeGlowRenderer.DrawRect(min, max, rounding, CreateEdgeGlowStyle(cornerFlags));
        }
    }

    private static EdgeGlowStyle CreateEdgeGlowStyle(ImDrawFlags cornerFlags)
        => new()
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
