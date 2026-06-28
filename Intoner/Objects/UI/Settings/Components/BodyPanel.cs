using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class BodyPanel
{
    public static SettingsTab? Draw(SettingsView view, DrawContext drawContext, SettingsTab? selectedTab)
    {
        SettingsTab? nextSelectedTab = selectedTab;
        Vector2 cardSize = new(
            Positive(ImGui.GetContentRegionAvail().X),
            Positive(ImGui.GetContentRegionAvail().Y));
        Vector2 cardMin = ImGui.GetCursorScreenPos();
        DrawFrame(cardMin, cardSize);

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, CompactPanelPadding);
        using var child = ImRaii.Child(
            "##objectSettingsBodyFrame",
            cardSize,
            false,
            ImGuiWindowFlags.NoScrollbar
          | ImGuiWindowFlags.NoScrollWithMouse
          | ImGuiWindowFlags.NoBackground
          | ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (child)
        {
            nextSelectedTab = DrawLayout(view, drawContext, nextSelectedTab, Positive(ImGui.GetContentRegionAvail().Y));
        }

        return nextSelectedTab;
    }

    private static SettingsTab? DrawLayout(
        SettingsView view,
        DrawContext drawContext,
        SettingsTab? selectedTab,
        float contentHeight)
    {
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var table = ImRaii.Table("##objectSettingsLayout", BodyColumnCount, LayoutTableFlags, new Vector2(0f, contentHeight));
        if (!table)
        {
            return selectedTab;
        }

        SetupBodyColumns();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        SettingsTab? nextSelectedTab = CategoryRail.Draw(view, selectedTab);

        ImGui.TableNextColumn();
        DrawDivider(contentHeight);

        ImGui.TableNextColumn();
        SectionPanel.DrawResults(view, drawContext, contentHeight);

        return nextSelectedTab;
    }

    private static void DrawFrame(Vector2 min, Vector2 size)
    {
        Vector2 max = min + size;
        var borderThickness = MathF.Max(1f, ImGui.GetStyle().ChildBorderSize);
        var borderInset = borderThickness * 0.5f;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(EditorColors.ButtonDefault with { W = 0.18f }),
            PanelRounding);
        drawList.AddRect(
            min + new Vector2(borderInset),
            max - new Vector2(borderInset),
            ImGui.GetColorU32(EditorColors.AccentPurple with { W = 0.16f }),
            PanelRounding,
            ImDrawFlags.None,
            borderThickness);
    }

    private static void DrawDivider(float height)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        float x = min.X + (Scaled(BodyDividerColumnWidth) * 0.5f);
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(x, min.Y),
            new Vector2(x, min.Y + height),
            ImGui.GetColorU32(EditorColors.Border with { W = 0.34f }),
            MathF.Max(1f, Scaled(1f)));
        ImGui.Dummy(new Vector2(Scaled(BodyDividerColumnWidth), height));
    }

    private const ImGuiTableFlags LayoutTableFlags =
        ImGuiTableFlags.SizingStretchProp
      | ImGuiTableFlags.NoPadInnerX
      | ImGuiTableFlags.NoPadOuterX
      | ImGuiTableFlags.NoSavedSettings;
}

