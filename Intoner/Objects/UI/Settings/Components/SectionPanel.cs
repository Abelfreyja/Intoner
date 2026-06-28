using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class SectionPanel
{
    public static void DrawResults(SettingsView view, DrawContext drawContext, float height)
    {
        var background = EditorColors.ButtonDefault with { W = 0.18f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = ObjectScrollList.Begin(
            "##objectSettingsResults",
            new Vector2(0f, height),
            ObjectScrollListOptions.Panel(background, PanelRounding, EditorColors.AccentPurple) with
            {
                OverlayTarget = drawContext.OverlayTarget,
            });
        if (!child)
        {
            return;
        }

        if (view.SelectedResult.Sections.Count == 0)
        {
            DrawEmptyResults();
            return;
        }

        for (var index = 0; index < view.SelectedResult.Sections.Count; ++index)
        {
            DrawSection(view.SelectedResult.Sections[index], drawContext);
        }
    }

    private static void DrawSection(SectionResult result, DrawContext drawContext)
    {
        Vector4 accent = ResolveTabAccent(result.Section.Tab);

        DrawPanelCard(
            $"objectSettingsSection{result.Section.Id}",
            EditorColors.ButtonDefault with { W = 0.22f },
            accent with { W = 0.20f },
            PanelRounding,
            PanelPadding,
            () =>
            {
                DrawSectionTitle(result.Section, result.Entries.Count, accent);
                ImGuiHelpers.ScaledDummy(6f);
                DrawSectionDivider(accent);
                ImGuiHelpers.ScaledDummy(6f);
                DrawSectionBody(result, drawContext, accent);
            });
    }

    private static void DrawSectionTitle(SettingsSection section, int entryCount, Vector4 accent)
    {
        DrawSectionIcon(section.Icon, accent);
        ImGui.SameLine(0f, Scaled(8f));
        using var group = ImRaii.Group();
        ImGui.TextUnformatted(section.Title);
        if (ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip(section.Description);
        }

        ImGui.SameLine(0f, Scaled(8f));
        DrawTextBadge(entryCount == 1 ? "1 setting" : $"{entryCount} settings", accent);

        ImGui.TextDisabled(section.Description);
    }

    private static void DrawSectionDivider(Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = new(min.X + Positive(ImGui.GetContentRegionAvail().X), min.Y);
        ImGui.GetWindowDrawList().AddLine(min, max, ImGui.GetColorU32(accent with { W = 0.22f }), MathF.Max(1f, scale));
        ImGui.Dummy(new Vector2(0f, scale));
    }

    private static void DrawSectionBody(SectionResult result, DrawContext drawContext, Vector4 accent)
    {
        var prominentControl = result.Section.Entries.Count == 1;
        var tableFlags = prominentControl ? SingleSettingTableFlags : SettingsTableFlags;
        using var table = ImRaii.Table($"##objectSettingsTable{result.Section.Id}", 2, tableFlags);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthFixed, Scaled(ResolveControlColumnWidth(result.Entries)));
        foreach (ISettingEntry entry in result.Entries)
        {
            entry.DrawRow(drawContext, accent, prominentControl);
        }
    }

    private static float ResolveControlColumnWidth(IReadOnlyList<ISettingEntry> entries)
    {
        var width = DefaultControlWidth;
        foreach (ISettingEntry entry in entries)
        {
            if (entry.Layout.ControlColumnWidth is { } columnWidth)
            {
                width = MathF.Max(width, columnWidth);
            }
        }

        return width;
    }

    private static void DrawEmptyResults()
    {
        DrawPanelCard(
            "objectSettingsEmptyResults",
            EditorColors.ButtonDefault with { W = 0.18f },
            EditorColors.Border with { W = 0.20f },
            PanelRounding,
            PanelPadding,
            () =>
            {
                DrawIcon(FontAwesomeIcon.Search, EditorColors.TextDisabled);
                ImGui.SameLine(0f, Scaled(8f));
                ImGui.TextDisabled("No object settings match the current search.");
            });
    }

    private const ImGuiTableFlags SettingsTableFlags =
        ImGuiTableFlags.SizingStretchProp
      | ImGuiTableFlags.RowBg
      | ImGuiTableFlags.BordersInnerH
      | ImGuiTableFlags.NoPadOuterX
      | ImGuiTableFlags.NoSavedSettings;

    private const ImGuiTableFlags SingleSettingTableFlags =
        ImGuiTableFlags.SizingStretchProp
      | ImGuiTableFlags.NoPadOuterX
      | ImGuiTableFlags.NoSavedSettings;
}

