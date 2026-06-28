using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorCard
{
    public static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, Action content)
        => DrawPanelCard(id, background, border, rounding, padding, null, content);

    public static void DrawPanelCard(string id, Vector4 background, Vector4 border, float rounding, Vector2 padding, float? minHeight, Action content)
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

            if (minHeight.HasValue)
            {
                var currentHeight = ImGui.GetItemRectMax().Y - startPos.Y;
                if (currentHeight < minHeight.Value)
                {
                    ImGui.Dummy(new Vector2(0f, minHeight.Value - currentHeight));
                }
            }

            var rectMin = startPos;
            var rectMax = new Vector2(startPos.X + availableWidth, ImGui.GetItemRectMax().Y);
            var borderThickness = MathF.Max(1f, ImGui.GetStyle().ChildBorderSize);

            drawList.ChannelsSetCurrent(0);
            drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(background), rounding);
            drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, borderThickness);
            drawList.ChannelsMerge();
        }
    }

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
        Action? drawAfterSubtitle = null)
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
        DrawIconTitleBlock(
            icon,
            title,
            subtitle,
            accent,
            alignTitleToFramePadding,
            wrapSubtitle,
            drawAfterTitle,
            drawAfterSubtitle);

        if (drawActions is not null)
        {
            ImGui.TableNextColumn();
            drawActions();
        }
    }

    public static void DrawIconTitleBlock(
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Vector4 accent,
        bool alignTitleToFramePadding = false,
        bool wrapSubtitle = false,
        Action? drawAfterTitle = null,
        Action? drawAfterSubtitle = null)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, accent))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            if (alignTitleToFramePadding)
            {
                ImGui.AlignTextToFramePadding();
            }

            ImGui.TextUnformatted(title);
            drawAfterTitle?.Invoke();

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                if (wrapSubtitle)
                {
                    using var wrap = ImRaiiScope.TextWrapPos();
                    ImGui.TextDisabled(subtitle);
                }
                else
                {
                    ImGui.TextDisabled(subtitle);
                }
            }

            drawAfterSubtitle?.Invoke();
        }
    }
}

