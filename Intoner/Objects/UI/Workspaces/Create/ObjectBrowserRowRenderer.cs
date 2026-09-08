using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed class ObjectBrowserRowRenderer
{
    private readonly EditorListCard  _listCard;
    private readonly UiSharedService _uiSharedService;

    public ObjectBrowserRowRenderer(EditorListCard listCard, UiSharedService uiSharedService)
    {
        _listCard        = listCard;
        _uiSharedService = uiSharedService;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ObjectBrowserRow
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required bool Selected { get; init; }
        public EditorBadge? TrailingBadge { get; init; }
        public IReadOnlyList<EditorBadge>? Badges { get; init; }
        public uint? ItemIconId { get; init; }
        public FontAwesomeIcon? TitleIcon { get; init; }
        public string? TitleIconTooltip { get; init; }
        public Vector4? TitleIconColor { get; init; }
        public bool Disabled { get; init; }
    }

    internal EditorListCard.Interaction DrawObjectBrowserRow(
        ObjectBrowserRow row,
        float height,
        Action onSelect,
        float leftInset = 0f,
        float rightInset = 0f)
    {
        Vector2 startPosition = ImGui.GetCursorPos();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - leftInset - rightInset);
        ImGui.SetCursorPosX(startPosition.X + leftInset);

        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            $"objectBrowserEntry:{row.Id}",
            row.Selected,
            new Vector2(width, height));
        if (interaction.Clicked && !row.Disabled)
        {
            onSelect();
        }

        Vector2 endPosition = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPosition.X, endPosition.Y));

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 min = interaction.Min;
        Vector2 max = interaction.Max;
        Vector4 accent = ThemeColors.AccentPrimary;
        float contentAlpha = row.Disabled ? 0.62f : 1f;
        Vector4 text = ThemeColors.Text with { W = ThemeColors.Text.W * contentAlpha };
        Vector4 disabledText = ThemeColors.TextDisabled with { W = 0.88f * contentAlpha };
        float padX = EditorLayout.Scaled(10f);
        float padY = EditorLayout.Scaled(8f);
        float itemIconSize = EditorLayout.Scaled(36f);
        float itemIconSpacing = EditorLayout.Scaled(9f);

        _listCard.DrawChrome(drawList, min, max, row.Selected, interaction.Hovered, accent);

        float contentRight = max.X - padX;
        if (row.TrailingBadge is not null || row.Badges is { Count: > 0 })
        {
            contentRight = EditorBadgeRenderer.DrawRightAligned(
                drawList,
                row.Badges,
                row.TrailingBadge,
                contentRight,
                min.Y + padY,
                row.Selected,
                contentAlpha) - EditorLayout.Scaled(6f);
        }

        float contentMinX = min.X + padX;
        if (row.ItemIconId is > 0)
        {
            Vector2 iconMin = new(contentMinX, min.Y + MathF.Max(0f, ((max.Y - min.Y) - itemIconSize) * 0.5f));
            Vector2 iconMax = iconMin + new Vector2(itemIconSize);
            float iconRounding = EditorLayout.Scaled(6f);
            Vector4 iconFill = row.Selected
                ? accent with { W = 0.20f * contentAlpha }
                : ThemeColors.WindowBg with { W = 0.28f * contentAlpha };
            Vector4 iconBorder = row.Selected
                ? accent with { W = 0.52f * contentAlpha }
                : ThemeColors.Border with { W = 0.28f * contentAlpha };
            drawList.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(iconFill), iconRounding);
            drawList.AddRect(iconMin, iconMax, ImGui.GetColorU32(iconBorder), iconRounding);

            if (TryGetObjectItemIcon(row.ItemIconId.Value, out IDalamudTextureWrap? itemIcon) && itemIcon is not null)
            {
                drawList.AddImage(
                    itemIcon.Handle,
                    iconMin,
                    iconMax,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(Vector4.One with { W = contentAlpha }));
            }

            contentMinX = iconMax.X + itemIconSpacing;
        }

        EditorIcon.Metrics titleIconMetrics = default;
        float titleIconSpacing = 0f;
        if (row.TitleIcon is { } titleIcon)
        {
            titleIconSpacing = EditorLayout.Scaled(7f);
            titleIconMetrics = EditorIcon.Measure(titleIcon);
        }

        float titleWidth = MathF.Max(
            1f,
            contentRight - contentMinX - titleIconMetrics.Size.X - titleIconSpacing);
        string title = EditorTextUtility.ClipTextToWidth(row.Title, titleWidth);
        Vector2 titlePosition = new(contentMinX, min.Y + padY);
        drawList.AddText(titlePosition, ImGui.GetColorU32(text), title);

        if (row.TitleIcon is { } renderedTitleIcon)
        {
            float renderedTitleWidth = ImGui.CalcTextSize(title).X;
            Vector2 titleIconPosition = new(titlePosition.X + renderedTitleWidth + titleIconSpacing, titlePosition.Y);
            Vector4 iconColor = row.TitleIconColor ?? accent with { W = row.Selected ? 0.92f : 0.76f };
            iconColor.W *= contentAlpha;
            EditorIcon.Draw(drawList, renderedTitleIcon, titleIconMetrics, titleIconPosition, iconColor);

            if (!string.IsNullOrWhiteSpace(row.TitleIconTooltip))
            {
                Vector2 titleIconMax = titleIconPosition + titleIconMetrics.Size;
                if (IntonerTooltip.IsAreaHovered(titleIconPosition, titleIconMax))
                {
                    IntonerTooltip.DrawDescription(
                        renderedTitleIcon,
                        row.TitleIconTooltip,
                        options: new IntonerTooltipOptions { Accent = iconColor });
                }
            }
        }

        float detailY = min.Y + padY + ImGui.GetTextLineHeight() + EditorLayout.Scaled(4f);
        drawList.AddText(
            new Vector2(contentMinX, detailY),
            ImGui.GetColorU32(disabledText),
            EditorTextUtility.ClipTextToWidth(row.Detail, MathF.Max(1f, contentRight - contentMinX)));
        return interaction;
    }

    internal bool TryGetObjectItemIcon(uint iconId, out IDalamudTextureWrap? wrap)
    {
        wrap = null;
        try
        {
            return _uiSharedService.TryGetIcon(iconId, out wrap) && wrap is not null;
        }
        catch
        {
            wrap = null;
            return false;
        }
    }
}
