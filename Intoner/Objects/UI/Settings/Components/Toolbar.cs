using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class Toolbar
{
    public static bool Draw(SettingsView view, ref string searchText, int totalSettingCount)
    {
        string nextSearchText = searchText;
        bool changed = DrawContent(view, ref nextSearchText, totalSettingCount);

        searchText = nextSearchText;
        return changed;
    }

    private static bool DrawContent(SettingsView view, ref string searchText, int totalSettingCount)
    {
        float headerHeight = Scaled(HeaderHeight);
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var table = ImRaii.Table("##objectSettingsToolbarContent", HeaderColumnCount, ToolbarTableFlags, new Vector2(0f, headerHeight));
        if (!table)
        {
            return false;
        }

        SetupHeaderColumns();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        DrawTitle(headerHeight);

        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(Scaled(BodyDividerColumnWidth), headerHeight));

        ImGui.TableNextColumn();
        bool changed = DrawSearch(
            "objectSettingsSearch",
            ref searchText,
            new SearchFieldStatus(view.Query.HasTokens, view.AllResult.EntryCount, totalSettingCount),
            EditorColors.AccentPurple);

        ImGui.TableNextColumn();
        return changed;
    }

    private static bool DrawSearch(string id, ref string searchText, SearchFieldStatus status, Vector4 accent)
    {
        float offsetY = MathF.Max(0f, (Scaled(HeaderHeight) - SearchField.DrawHeight) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        return SearchField.Draw(id, ref searchText, status, accent);
    }

    private static void DrawTitle(float height)
    {
        float offsetY = MathF.Max(0f, (height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        DrawSectionIcon(FontAwesomeIcon.Cog, EditorColors.AccentPurple);
        ImGui.SameLine(0f, Scaled(8f));
        ImGui.TextUnformatted("Object Settings");
    }

    private const ImGuiTableFlags ToolbarTableFlags =
        ImGuiTableFlags.SizingStretchProp
      | ImGuiTableFlags.NoPadInnerX
      | ImGuiTableFlags.NoPadOuterX
      | ImGuiTableFlags.NoSavedSettings;
}

