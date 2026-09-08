using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Services.EdgeGlow;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class EditorListCard(EdgeGlowRenderer edgeGlowRenderer)
{
    public readonly record struct Interaction(bool Clicked, bool RightClicked, bool Hovered, Vector2 Min, Vector2 Max);

    public static float MinimumTextWidth => 40f * ImGuiHelpers.GlobalScale;

    public bool Draw(
        string id,
        string title,
        IReadOnlyList<EditorBadge> badges,
        bool selected,
        Vector4 accent,
        float height,
        Action drawContextMenu,
        bool panelBadgeSurface = false)
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

        float textWidth = MathF.Max(MinimumTextWidth, max.X - min.X - (padX * 2f));
        drawList.AddText(
            new Vector2(min.X + padX, min.Y + padY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(title, textWidth));

        float metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * scale);
        EditorBadgeRenderer.DrawAt(
            drawList,
            badges,
            min.X + padX,
            metaY,
            selected,
            maxRight: max.X - padX,
            panelSurface: panelBadgeSurface);

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
            ImGui.IsItemClicked(ImGuiMouseButton.Right),
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
        Vector4 fill = (selected, hovered) switch
        {
            (true, _)     => accent with { W = 0.17f },
            (false, true) => accent with { W = 0.08f },
            _             => ThemeColors.ButtonDefault with { W = 0.22f },
        };
        Vector4 border = (selected, hovered) switch
        {
            (true, _)     => accent with { W = 0.85f },
            (false, true) => accent with { W = 0.58f },
            _             => ThemeColors.Border with { W = 0.38f },
        };
        float railAlpha = (selected, hovered) switch
        {
            (true, _)     => 0.95f,
            (false, true) => 0.72f,
            _             => 0.55f,
        };

        DrawFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = railAlpha },
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
        bool showEdgeGlow,
        ImDrawFlags cornerFlags = ImDrawFlags.RoundCornersRight)
    {
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
