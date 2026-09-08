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
    public static void DrawResults(SettingsView view, IUiOverlayTarget? overlayTarget, float height)
    {
        var background = ThemeColors.ButtonDefault with { W = 0.18f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = EditorScrollList.Begin(
            "##objectSettingsResults",
            new Vector2(0f, height),
            EditorScrollListOptions.Panel(background, PanelRounding, ThemeColors.AccentPrimary) with
            {
                CornerFlags = ImDrawFlags.RoundCornersRight,
                OverlayTarget = overlayTarget,
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
            DrawSection(view.SelectedResult.Sections[index]);
        }
    }

    private static void DrawSection(SectionResult result)
    {
        Vector4 accent = result.Tab.Accent;

        DrawPanelCard(
            $"objectSettingsSection{result.Section.Id}",
            ThemeColors.ButtonDefault with { W = 0.22f },
            accent with { W = 0.20f },
            PanelRounding,
            PanelPadding,
            () =>
            {
                DrawSectionTitle(result.Section, result.Entries.Count, accent);
                ImGuiHelpers.ScaledDummy(6f);
                DrawSectionDivider(accent);
                ImGuiHelpers.ScaledDummy(6f);
                DrawSectionBody(result, accent);
            });
    }

    private static void DrawSectionTitle(SettingsSection section, int entryCount, Vector4 accent)
    {
        EditorIcon.DrawInline(section.Icon, accent);
        ImGui.SameLine(0f, Scaled(8f));
        using var group = ImRaii.Group();
        ImGui.TextUnformatted(section.Title);
        if (ImGui.IsItemHovered())
        {
            IntonerTooltip.Attach(
                section.Icon,
                section.Title,
                section.Description,
                new IntonerTooltipOptions { Accent = accent });
        }

        if (section.ShowEntryCount)
        {
            ImGui.SameLine(0f, Scaled(8f));
            EditorBadgeRenderer.Draw(EditorBadge.Label(
                entryCount == 1
                    ? $"1 {section.EntryLabel}"
                    : $"{entryCount} {section.EntryPluralLabel}",
                color: accent));
        }

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

    private static void DrawSectionBody(SectionResult result, Vector4 accent)
    {
        bool prominentControl = result.Entries.Count == 1;
        int entryIndex = 0;
        int blockIndex = 0;
        while (entryIndex < result.Entries.Count)
        {
            if (blockIndex > 0)
            {
                DrawEntryDivider(accent);
            }

            ISettingEntry entry = result.Entries[entryIndex];
            if (entry.Layout.FullWidth)
            {
                DrawFullWidthEntry(result.Section.Id, entry, entryIndex, accent, prominentControl);
                ++entryIndex;
            }
            else
            {
                int blockStart = entryIndex;
                while (entryIndex < result.Entries.Count && !result.Entries[entryIndex].Layout.FullWidth)
                {
                    ++entryIndex;
                }

                DrawSettingBlock(
                    result.Section.Id,
                    result.Entries,
                    blockStart,
                    entryIndex,
                    blockIndex,
                    accent,
                    prominentControl);
            }

            ++blockIndex;
        }
    }

    private static void DrawSettingBlock(
        string sectionId,
        IReadOnlyList<ISettingEntry> entries,
        int startIndex,
        int endIndex,
        int blockIndex,
        Vector4 accent,
        bool prominentControl)
    {
        var tableFlags = prominentControl ? SingleSettingTableFlags : SettingsTableFlags;
        using var table = ImRaii.Table($"##objectSettingsTable{sectionId}:{blockIndex}", 2, tableFlags);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthFixed, Scaled(ResolveControlColumnWidth(entries)));
        for (int index = startIndex; index < endIndex; ++index)
        {
            entries[index].DrawRow(accent, prominentControl);
        }
    }

    private static void DrawFullWidthEntry(
        string sectionId,
        ISettingEntry entry,
        int entryIndex,
        Vector4 accent,
        bool prominentControl)
    {
        using var table = ImRaii.Table(
            $"##objectSettingsFullWidth{sectionId}:{entryIndex}",
            1,
            SingleSettingTableFlags);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch);
        entry.DrawRow(accent, prominentControl);
    }

    private static void DrawEntryDivider(Vector4 accent)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = new(min.X + Positive(ImGui.GetContentRegionAvail().X), min.Y);
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.GetWindowDrawList().AddLine(min, max, ImGui.GetColorU32(accent with { W = 0.16f }), MathF.Max(1f, scale));
        ImGui.Dummy(new Vector2(0f, 5f * scale));
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
            ThemeColors.ButtonDefault with { W = 0.18f },
            ThemeColors.Border with { W = 0.20f },
            PanelRounding,
            PanelPadding,
            () =>
            {
                EditorIcon.DrawInline(FontAwesomeIcon.Search, ThemeColors.TextDisabled);
                ImGui.SameLine(0f, Scaled(8f));
                ImGui.TextDisabled("No settings match the current search.");
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

