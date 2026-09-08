using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorCard
{
    public static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, Action content)
        => DrawPanelCard(id, background, border, rounding, padding, null, ImDrawFlags.RoundCornersAll, null, 0f, content, false);

    public static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, float? minHeight, Action content)
        => DrawPanelCard(id, background, border, rounding, padding, minHeight, ImDrawFlags.RoundCornersAll, null, 0f, content, false);

    public static void DrawPanelCard(
        string id,
        Vector4 background,
        Vector4 border,
        float rounding,
        Vector2 padding,
        float? minHeight,
        ImDrawFlags cornerFlags,
        Vector4? rail,
        float railWidth,
        Action content,
        bool fixedHeight = false)
    {
        using (ImRaii.PushId(id))
        {
            var startPos = ImGui.GetCursorScreenPos();
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var contentWidth = MathF.Max(1f, availableWidth - (padding.X * 2f));
            var drawList = ImGui.GetWindowDrawList();

            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);

            using (ImRaii.Group())
            {
                ImGui.Dummy(new Vector2(0f, padding.Y));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding.X);
                using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
                {
                    using var table = ImRaii.Table("##cardContent", 1, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadInnerX | ImGuiTableFlags.NoPadOuterX, new Vector2(contentWidth, 0f));
                    if (table)
                    {
                        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 1f);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        content();
                    }
                }

                ImGui.Dummy(new Vector2(0f, padding.Y));
            }

            float contentHeight = ImGui.GetItemRectMax().Y - startPos.Y;
            float cardHeight = contentHeight;
            if (minHeight.HasValue)
            {
                cardHeight = fixedHeight
                    ? minHeight.Value
                    : MathF.Max(contentHeight, minHeight.Value);
            }

            if (fixedHeight || cardHeight > contentHeight)
            {
                ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + cardHeight));
                ImGui.Dummy(Vector2.Zero);
            }

            var rectMin = startPos;
            var rectMax = new Vector2(startPos.X + availableWidth, startPos.Y + cardHeight);
            var borderThickness = MathF.Max(1f, ImGui.GetStyle().ChildBorderSize);

            drawList.ChannelsSetCurrent(0);
            drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(background), rounding, cornerFlags);
            drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(border), rounding, cornerFlags, borderThickness);
            if (rail is { } railColor && railWidth > 0f)
            {
                drawList.AddRectFilled(
                    rectMin,
                    new Vector2(rectMin.X + railWidth, rectMax.Y),
                    ImGui.GetColorU32(railColor));
            }

            drawList.ChannelsMerge();
        }
    }

    public static void DrawFixedPanelCard(
        string id,
        Vector4 background,
        Vector4 border,
        float rounding,
        Vector2 padding,
        float height,
        Action content)
        => DrawPanelCard(
            id,
            background,
            border,
            rounding,
            padding,
            height,
            ImDrawFlags.RoundCornersAll,
            null,
            0f,
            content,
            true);

    public static void DrawCardHeader(
        string id,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Vector4 accent,
        Action? drawActions = null,
        float actionWidth = 0f,
        bool alignTitleToFramePadding = false,
        bool wrapSubtitle = false,
        Action? drawAfterTitle = null,
        Action? drawAfterSubtitle = null,
        bool wrapTitle = false)
    {
        var columnCount = drawActions is null ? 1 : 2;
        var tableFlags = drawActions is null
            ? ImGuiTableFlags.SizingStretchSame
            : ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table($"##{id}", columnCount, tableFlags);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn(drawActions is null ? "Content" : "Info", ImGuiTableColumnFlags.WidthStretch);
        if (drawActions is not null)
        {
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        EditorIconText.Draw(
            icon,
            title,
            subtitle,
            accent,
            new EditorIconTextOptions
            {
                AlignTitleToFramePadding = alignTitleToFramePadding,
                WrapTitle = wrapTitle,
                WrapSubtitle = wrapSubtitle,
                DrawAfterTitle = drawAfterTitle,
                DrawAfterSubtitle = drawAfterSubtitle,
            });

        if (drawActions is not null)
        {
            ImGui.TableNextColumn();
            drawActions();
        }
    }

    public static void DrawCardHeader(
        string id,
        FontAwesomeIcon icon,
        string title,
        IReadOnlyList<EditorBadge> badges,
        Vector4 accent,
        Action? drawActions = null,
        float actionWidth = 0f,
        bool alignTitleToFramePadding = false,
        Action? drawAfterBadges = null,
        bool wrapTitle = false)
        => DrawCardHeader(
            id,
            icon,
            title,
            string.Empty,
            accent,
            drawActions,
            actionWidth,
            alignTitleToFramePadding,
            drawAfterTitle: () => EditorBadgeRenderer.DrawInline(badges),
            drawAfterSubtitle: drawAfterBadges,
            wrapTitle: wrapTitle);
}

