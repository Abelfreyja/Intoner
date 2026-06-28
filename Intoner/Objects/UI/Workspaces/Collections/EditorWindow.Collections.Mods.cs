using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Components.CollectionStatusUi;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawObjectCollectionModsSection(ObjectCollectionSnapshot collection, float minHeight)
    {
        var padding = ScaledVector(8f, 6f);
        var background = EditorColors.ButtonDefault with { W = 0.20f };
        var rounding = Scaled(6f);
        DrawPanelCard(
            "objectCollectionsModsSection",
            background,
            EditorColors.AccentPurple with { W = 0.14f },
            rounding,
            padding,
            minHeight,
            () =>
            {
                var accent = EditorColors.AccentPurple;
                var addButtonEdge = ResolveActionIconButtonEdge(FontAwesomeIcon.Plus);
                var contentStartY = ImGui.GetCursorPosY();
                var emptyStateNote = collection.Record.Entries.Count == 0
                    ? "No Penumbra mods are assigned to this collection yet."
                    : null;

                DrawCardHeader(
                    "objectCollectionsModsHeader",
                    FontAwesomeIcon.Cubes,
                    "Penumbra Mods",
                    BuildAssignedModsSubtitle(collection.Record.Entries.Count),
                    accent,
                    () =>
                    {
                        bool openAddModPopup = DrawAccentIconButton("objectCollectionAddMod", FontAwesomeIcon.Plus, "Add installed Penumbra mods", accent, addButtonEdge);
                        var addButtonMin = ImGui.GetItemRectMin();
                        var addButtonMax = ImGui.GetItemRectMax();
                        if (openAddModPopup)
                        {
                            QueueObjectCollectionAddModPopup(collection.Record.CollectionId, addButtonMin, addButtonMax);
                        }
                    },
                    addButtonEdge,
                    alignTitleToFramePadding: true,
                    drawAfterTitle: () =>
                    {
                        if (string.IsNullOrWhiteSpace(emptyStateNote))
                        {
                            return;
                        }

                        ImGui.SameLine(0f, Scaled(8f));
                        DrawObjectCollectionHeaderNote(
                            "objectCollectionsModsHeaderNote",
                            emptyStateNote,
                            EditorColors.TextDisabled);
                    });

                ImGuiHelpers.ScaledDummy(4f);

                var innerHeight = ResolveScrollableCardInnerHeight(minHeight, padding, contentStartY);

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##objectCollectionsModsScroll",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (!child)
                {
                    return;
                }

                DrawObjectCollectionAssignedModsList(collection);
            });
    }

    private void DrawObjectCollectionAssignedModsList(ObjectCollectionSnapshot collection)
    {
        if (collection.Record.Entries.Count == 0)
        {
            return;
        }

        var rowHeight = MathF.Max(Scaled(34f), ImGui.GetTextLineHeight() + Scaled(12f));
        var rowGap = Scaled(4f);
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        for (var index = 0; index < collection.Record.Entries.Count; ++index)
        {
            ObjectCollectionModSettings entry = collection.Record.Entries[index];
            if (DrawObjectCollectionAssignedModRow(collection, entry, index, rowHeight))
            {
                return;
            }

            if (index + 1 < collection.Record.Entries.Count)
            {
                ImGui.Dummy(new Vector2(0f, rowGap));
            }
        }
    }

    private bool DrawObjectCollectionAssignedModRow(
        ObjectCollectionSnapshot collection,
        ObjectCollectionModSettings entry,
        int index,
        float height)
    {
        bool expanded = IsObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
        var startPos = ImGui.GetCursorPos();
        var insetX = Scaled(3f);
        var width = Positive(ImGui.GetContentRegionAvail().X - (insetX * 2f));

        ImGui.SetCursorPosX(startPos.X + insetX);
        ListEntryCardInteraction interaction = DrawListEntryCardInteraction(
            $"objectCollectionModEntry:{collection.Record.CollectionId}:{index}",
            false,
            new Vector2(width, height),
            ImGuiSelectableFlags.AllowItemOverlap);

        var cursorAfterRow = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, cursorAfterRow.Y));

        var min = interaction.Min;
        var max = interaction.Max;
        var drawList = ImGui.GetWindowDrawList();
        var accent = entry.Enabled
            ? EditorColors.AccentPurple
            : EditorColors.AccentGrey;
        var fill = EditorColors.ButtonDefault with { W = entry.Enabled ? 0.22f : 0.15f };
        var border = accent with { W = interaction.Hovered ? 0.58f : entry.Enabled ? 0.42f : 0.26f };
        var rounding = Scaled(7f);
        var padX = Scaled(8f);

        DrawListEntryCardFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = entry.Enabled ? 0.70f : 0.42f },
            rounding,
            interaction.Hovered);

        var buttonSize = ResolveActionIconButtonEdge(FontAwesomeIcon.Check, FontAwesomeIcon.Ban, FontAwesomeIcon.Trash);
        var controlGap = Scaled(6f);
        var priorityWidth = Scaled(74f);
        var rowHeight = max.Y - min.Y;
        var framePadding = ImGui.GetStyle().FramePadding;
        var priorityPaddingY = MathF.Max(framePadding.Y, (buttonSize - ImGui.GetTextLineHeight()) * 0.5f);
        var priorityHeight = ImGui.GetTextLineHeight() + (priorityPaddingY * 2f);
        var buttonY = min.Y + ((rowHeight - buttonSize) * 0.5f);
        var inputY = min.Y + ((rowHeight - priorityHeight) * 0.5f);
        var removePos = new Vector2(max.X - padX - buttonSize, buttonY);
        var enabledPos = new Vector2(removePos.X - controlGap - buttonSize, buttonY);
        var priorityPos = new Vector2(enabledPos.X - controlGap - priorityWidth, inputY);
        var textRight = priorityPos.X - Scaled(10f);

        var modLabel = entry.ModName.Length > 0 ? entry.ModName : entry.ModDirectory;
        var rowLabel = $"{modLabel} | {BuildObjectCollectionModOverrideLabel(entry.Settings.Count)}";
        var disclosureText = (expanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight).ToIconString();
        var iconText = FontAwesomeIcon.Cubes.ToIconString();
        Vector2 disclosureSize;
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            disclosureSize = ImGui.CalcTextSize(disclosureText);
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var textLineHeight = ImGui.GetTextLineHeight();
        var contentStartY = min.Y + ((rowHeight - textLineHeight) * 0.5f);
        var disclosurePos = new Vector2(min.X + padX, min.Y + ((rowHeight - disclosureSize.Y) * 0.5f));
        var iconPos = new Vector2(
            disclosurePos.X + disclosureSize.X + Scaled(8f),
            min.Y + ((rowHeight - iconSize.Y) * 0.5f));
        var labelX = iconPos.X + iconSize.X + Scaled(10f);
        var labelWidth = MathF.Max(ResolveMinimumCardTextWidth(), textRight - labelX);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                disclosurePos,
                ImGui.GetColorU32(accent with { W = entry.Enabled ? 0.84f : 0.52f }),
                disclosureText);
            drawList.AddText(iconPos, ImGui.GetColorU32(accent with { W = entry.Enabled ? 0.92f : 0.60f }), iconText);
        }

        drawList.AddText(
            new Vector2(labelX, contentStartY),
            ImGui.GetColorU32(EditorColors.Text with { W = entry.Enabled ? 1f : 0.72f }),
            ClipTextToWidth(rowLabel, labelWidth));

        if (EditorInputUtility.IsMouseInside(new Vector2(labelX, min.Y), new Vector2(textRight, max.Y))
            && !string.IsNullOrWhiteSpace(entry.ModDirectory))
        {
            UiSharedService.DrawAccentTooltipText(entry.ModDirectory, accent, wrapEms: 35f);
        }

        int priority = entry.Priority;
        ImGui.SetCursorScreenPos(priorityPos);
        ImGui.SetNextItemWidth(priorityWidth);
        using var priorityFramePadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(framePadding.X, priorityPaddingY));
        if (ImGui.InputInt($"##objectCollectionModPriority:{collection.Record.CollectionId}:{index}", ref priority))
        {
            UpdateObjectCollectionEntry(collection, index, entry with { Priority = priority });
            ImGui.SetCursorPos(cursorAfterRow);
            return true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Priority");
        }

        ImGui.SetCursorScreenPos(enabledPos);
        if (DrawAccentIconButton(
                $"objectCollectionModEnabled:{collection.Record.CollectionId}:{index}",
                entry.Enabled ? FontAwesomeIcon.Check : FontAwesomeIcon.Ban,
                entry.Enabled ? "Disable mod for this collection" : "Enable mod for this collection",
                entry.Enabled ? EditorColors.AccentPurple : EditorColors.AccentGrey,
                buttonSize))
        {
            UpdateObjectCollectionEntry(collection, index, entry with { Enabled = !entry.Enabled });
            ImGui.SetCursorPos(cursorAfterRow);
            return true;
        }

        ImGui.SetCursorScreenPos(removePos);
        if (DrawAccentIconButton(
                $"objectCollectionModRemove:{collection.Record.CollectionId}:{index}",
                FontAwesomeIcon.Trash,
                "Remove mod from this collection",
                EditorColors.DimRed,
                buttonSize))
        {
            RemoveObjectCollectionEntry(collection, index);
            ImGui.SetCursorPos(cursorAfterRow);
            return true;
        }

        if (interaction.Clicked && !MouseInAnyObjectCollectionModRowControl(priorityPos, priorityPos + new Vector2(priorityWidth, priorityHeight), enabledPos, removePos, buttonSize))
        {
            ToggleObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
            expanded = IsObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
        }

        ImGui.SetCursorPos(cursorAfterRow);
        if (expanded)
        {
            ImGui.Dummy(new Vector2(0f, Scaled(4f)));
            if (DrawObjectCollectionModSettingsPanel(collection, entry, index))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildObjectCollectionModOverrideLabel(int overrideCount)
        => overrideCount == 0
            ? "default settings"
            : overrideCount == 1
                ? "1 override"
                : $"{overrideCount} overrides";

    private bool IsObjectCollectionModRowExpanded(string collectionId, ObjectCollectionModSettings entry)
        => _expandedCollectionModRows.Contains(BuildObjectCollectionModRowKey(collectionId, entry));

    private void ToggleObjectCollectionModRowExpanded(string collectionId, ObjectCollectionModSettings entry)
    {
        string key = BuildObjectCollectionModRowKey(collectionId, entry);
        if (!_expandedCollectionModRows.Add(key))
        {
            _expandedCollectionModRows.Remove(key);
        }
    }

    private static string BuildObjectCollectionModRowKey(string collectionId, ObjectCollectionModSettings entry)
        => $"{ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId)}:{ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory)}";

    private static bool MouseInAnyObjectCollectionModRowControl(
        Vector2 priorityMin,
        Vector2 priorityMax,
        Vector2 enabledPos,
        Vector2 removePos,
        float buttonSize)
    {
        Vector2 mousePos = ImGui.GetMousePos();
        return IsPointInsideRect(mousePos, priorityMin, priorityMax)
            || IsPointInsideRect(mousePos, enabledPos, enabledPos + new Vector2(buttonSize, buttonSize))
            || IsPointInsideRect(mousePos, removePos, removePos + new Vector2(buttonSize, buttonSize));
    }

    private static bool IsPointInsideRect(Vector2 point, Vector2 min, Vector2 max)
        => point.X >= min.X
            && point.X <= max.X
            && point.Y >= min.Y
            && point.Y <= max.Y;
}
