using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class Toolbar
{
    public static bool Draw(SettingsView view, ref string searchText, int totalEntryCount)
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
        string statusText = view.Query.HasTokens
            ? $"{view.AllResult.EntryCount}/{totalEntryCount}"
            : $"{totalEntryCount} settings";
        bool changed = DrawSearch(
            "objectSettingsSearch",
            ref searchText,
            new EditorSearchFieldOptions(
                "Search object settings",
                ThemeColors.AccentPrimary,
                statusText,
                view.Query.HasTokens && view.AllResult.EntryCount == 0));

        ImGui.TableNextColumn();
        return changed;
    }

    private static bool DrawSearch(string id, ref string searchText, EditorSearchFieldOptions options)
    {
        float offsetY = MathF.Max(0f, (Scaled(HeaderHeight) - EditorSearchField.Height) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        return EditorSearchField.Draw(id, ref searchText, options);
    }

    private static void DrawTitle(float height)
    {
        float offsetY = MathF.Max(0f, (height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
        EditorIcon.DrawInline(FontAwesomeIcon.Cog, ThemeColors.AccentPrimary);
        ImGui.SameLine(0f, Scaled(8f));
        ImGui.TextUnformatted("Settings");
    }

    private const ImGuiTableFlags ToolbarTableFlags =
        ImGuiTableFlags.SizingStretchProp
      | ImGuiTableFlags.NoPadInnerX
      | ImGuiTableFlags.NoPadOuterX
      | ImGuiTableFlags.NoSavedSettings;
}

