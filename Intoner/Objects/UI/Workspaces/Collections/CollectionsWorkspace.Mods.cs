using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Dependencies;
using Intoner.Services.Dependencies;
using Intoner.Services.Input;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Components.CollectionStatusUi;

namespace Intoner.Objects.UI;

internal sealed partial class CollectionsWorkspace
{
    private void DrawObjectCollectionModsSection(ObjectCollectionSnapshot collection, float width, float height)
    {
        DependencyStatus penumbraStatus = _penumbra.Status;
        string filter = _objectCollectionAssignedModFilter.Trim();
        int visibleModCount = CountVisibleObjectCollectionMods(collection, filter);
        string subtitle = penumbraStatus.IsAvailable
            ? BuildAssignedModsSubtitle(collection.Record.Entries.Count)
            : penumbraStatus.Message;
        _collectionPanel.DrawObjectCollectionPanel(
            "objectCollectionsMods",
            FontAwesomeIcon.Cubes,
            "Penumbra Mods",
            subtitle,
            ThemeColors.AccentPrimary,
            width,
            height,
            layout => DrawObjectCollectionModsHeaderActions(collection, penumbraStatus, visibleModCount, layout),
            listHeight => DrawObjectCollectionAssignedModsList(collection, filter, visibleModCount, listHeight));
    }

    private float DrawObjectCollectionModsHeaderActions(
        ObjectCollectionSnapshot collection,
        DependencyStatus penumbraStatus,
        int visibleModCount,
        CollectionPanel.ObjectCollectionPanelHeaderLayout layout)
    {
        Vector4 accent = ThemeColors.AccentPrimary;
        float rightInset = EditorLayout.Scaled(8f);
        float controlGap = EditorLayout.Scaled(7f);
        float buttonSize = EditorLayout.Scaled(32f);
        float buttonX = layout.Max.X - rightInset - buttonSize;
        float textRight = CollectionPanel.DrawObjectCollectionPanelSearch(
            "objectCollectionAssignedModFilter",
            "Filter assigned mods",
            ref _objectCollectionAssignedModFilter,
            visibleModCount,
            collection.Record.Entries.Count,
            layout,
            buttonX - controlGap);

        ImGui.SetCursorScreenPos(new Vector2(buttonX, layout.Min.Y + ((layout.Height - buttonSize) * 0.5f)));
        bool openAddModPopup;
        using (ImRaii.Disabled(!penumbraStatus.IsAvailable))
        {
            openAddModPopup = EditorIconButton.DrawAccent(
                "objectCollectionAddMod",
                FontAwesomeIcon.Plus,
                "Add installed Penumbra mods",
                accent,
                buttonSize);
        }

        Vector2 addButtonMin = ImGui.GetItemRectMin();
        Vector2 addButtonMax = ImGui.GetItemRectMax();
        if (!penumbraStatus.IsAvailable
         && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            IntonerTooltip.DrawText(
                penumbraStatus.Message,
                DependencyUi.ResolvePresentation(penumbraStatus.State).Accent,
                wrapWidthEms: 35f);
        }

        if (openAddModPopup && penumbraStatus.IsAvailable)
        {
            QueueObjectCollectionAddModPopup(collection.Record.CollectionId, addButtonMin, addButtonMax);
        }

        return textRight;
    }

    private void DrawObjectCollectionAssignedModsList(
        ObjectCollectionSnapshot collection,
        string filter,
        int visibleModCount,
        float height)
    {
        if (visibleModCount == 0)
        {
            string message = collection.Record.Entries.Count == 0
                ? "No Penumbra mods are assigned to this collection."
                : "No assigned mods match the current filter.";
            EditorEmptyState.Draw(message, height);
            return;
        }

        float rowHeight = EditorLayout.Scaled(CollectionPanel.CollectionModStyle.RowHeight);
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        int visiblePosition = 0;
        bool changed = false;
        for (var index = 0; index < collection.Record.Entries.Count; ++index)
        {
            ObjectCollectionModSettings entry = collection.Record.Entries[index];
            if (!ObjectCollectionModMatchesFilter(entry, filter))
            {
                continue;
            }

            ++visiblePosition;
            using var disabled = ImRaii.Disabled(changed);
            changed |= DrawObjectCollectionAssignedModRow(
                    collection,
                    entry,
                    index,
                    rowHeight,
                    visiblePosition == 1,
                    visiblePosition == visibleModCount);
        }
    }

    private static int CountVisibleObjectCollectionMods(ObjectCollectionSnapshot collection, string filter)
    {
        if (filter.Length == 0)
        {
            return collection.Record.Entries.Count;
        }

        return collection.Record.Entries.Count(entry => ObjectCollectionModMatchesFilter(entry, filter));
    }

    private static bool ObjectCollectionModMatchesFilter(ObjectCollectionModSettings entry, string filter)
        => filter.Length == 0
        || entry.ModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.ModDirectory.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private bool DrawObjectCollectionAssignedModRow(
        ObjectCollectionSnapshot collection,
        ObjectCollectionModSettings entry,
        int index,
        float height,
        bool isFirst,
        bool isLast)
    {
        bool expanded = IsObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
        string rowKey = CollectionModSettingsEditor.BuildObjectCollectionModRowKey(collection.Record.CollectionId, entry);
        Vector2 startPos = ImGui.GetCursorPos();
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        EditorListCard.Interaction interaction = EditorListCard.DrawInteraction(
            $"objectCollectionModEntry:{rowKey}",
            false,
            new Vector2(width, height),
            ImGuiSelectableFlags.AllowItemOverlap);
        ObjectCollectionModSettingsView? settingsView = expanded || ImGui.IsItemVisible()
            ? _objectModDataSource.GetModSettings(entry)
            : null;
        DrawObjectCollectionModContextMenu($"##objectCollectionModContext:{rowKey}", entry.ModDirectory);

        Vector2 cursorAfterRow = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, cursorAfterRow.Y));

        Vector2 min = interaction.Min;
        Vector2 max = interaction.Max;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector4 accent = entry.Enabled
            ? ThemeColors.AccentPrimary
            : ThemeColors.AccentGrey;
        Vector4 fill = interaction.Hovered && entry.Enabled
            ? CollectionPanel.CollectionModStyle.RowHover
            : CollectionPanel.CollectionModStyle.Row;
        if (!entry.Enabled)
        {
            fill.W *= 0.70f;
        }

        ImDrawFlags corners = isLast && !expanded
            ? ImDrawFlags.RoundCornersBottom
            : ImDrawFlags.RoundCornersNone;
        Vector4 border = (interaction.Hovered, entry.Enabled) switch
        {
            (true, true)  => accent with { W = 0.58f },
            (true, false) => accent with { W = 0.38f },
            (false, true) => ThemeColors.Border with { W = 0.38f },
            _             => ThemeColors.Border with { W = 0.24f },
        };
        _listCard.DrawFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = expanded ? 0.92f : 0.58f },
            EditorLayout.Scaled(6f),
            interaction.Hovered,
            corners);
        if (!isFirst)
        {
            drawList.AddLine(
                min,
                new Vector2(max.X, min.Y),
                ImGui.GetColorU32(CollectionPanel.ObjectCollectionPanelStyle.Divider),
                EditorLayout.Scaled(1f));
        }

        float buttonSize = EditorLayout.Scaled(CollectionPanel.CollectionModStyle.ActionSize);
        float controlGap = EditorLayout.Scaled(7f);
        float actionGap = EditorSegmentedControl.Spacing;
        float priorityWidth = EditorLayout.Scaled(CollectionPanel.CollectionModStyle.PriorityWidth);
        float rowHeight = max.Y - min.Y;
        float buttonY = min.Y + ((rowHeight - buttonSize) * 0.5f);
        float padX = EditorLayout.Scaled(6f);
        Vector2 removePos = new(max.X - padX - buttonSize, buttonY);
        Vector2 enabledPos = new(removePos.X - actionGap - buttonSize, buttonY);
        Vector2 priorityPos = new(enabledPos.X - controlGap - priorityWidth, buttonY);
        string modLabel = entry.ModName.Length > 0 ? entry.ModName : entry.ModDirectory;
        FontAwesomeIcon disclosureIcon = expanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        EditorIcon.Metrics disclosureMetrics = EditorIcon.Measure(disclosureIcon, 0.76f);
        EditorIcon.Metrics modIconMetrics = EditorIcon.Measure(FontAwesomeIcon.Cubes, 0.84f);

        float textLineHeight = ImGui.GetTextLineHeight();
        float contentStartY = min.Y + ((rowHeight - textLineHeight) * 0.5f);
        Vector2 disclosurePos = new(
            min.X + EditorLayout.Scaled(7f),
            min.Y + ((rowHeight - disclosureMetrics.Size.Y) * 0.5f));
        Vector2 iconPos = new(
            min.X + EditorLayout.Scaled(36f),
            min.Y + ((rowHeight - modIconMetrics.Size.Y) * 0.5f));
        float labelX = iconPos.X + modIconMetrics.Size.X + EditorLayout.Scaled(8f);
        int settingCount = settingsView?.ResolveState == ObjectCollectionResolveState.Ready
            ? settingsView.Groups.Count
            : 0;
        int changedCount = settingsView is not null ? CountObjectCollectionModSettingOverrides(settingsView) : 0;
        float textRight = priorityPos.X - EditorLayout.Scaled(9f);
        if (width >= EditorLayout.Scaled(610f) && settingCount > 0)
        {
            textRight = DrawObjectCollectionModBadges(
                drawList,
                textRight,
                min.Y + ((rowHeight - EditorBadgeRenderer.Height) * 0.5f),
                settingCount,
                changedCount,
                entry.Enabled,
                accent) - EditorLayout.Scaled(8f);
        }

        float labelWidth = MathF.Max(EditorListCard.MinimumTextWidth, textRight - labelX);

        EditorIcon.Draw(
            drawList,
            disclosureIcon,
            disclosureMetrics,
            disclosurePos,
            accent with { W = entry.Enabled ? 0.84f : 0.52f });
        EditorIcon.Draw(
            drawList,
            FontAwesomeIcon.Cubes,
            modIconMetrics,
            iconPos,
            accent with { W = entry.Enabled ? 0.92f : 0.60f });

        drawList.AddText(
            new Vector2(labelX, contentStartY),
            ImGui.GetColorU32(ThemeColors.Text with { W = entry.Enabled ? 1f : 0.72f }),
            EditorTextUtility.ClipTextToWidth(modLabel, labelWidth));

        if (IntonerTooltip.IsAreaHovered(new Vector2(labelX, min.Y), new Vector2(textRight, max.Y))
            && !string.IsNullOrWhiteSpace(entry.ModDirectory))
        {
            IntonerTooltip.DrawText(entry.ModDirectory, accent, 35f);
        }

        int priority = entry.Priority;
        bool changed = false;
        if (DrawObjectCollectionModPriority(
                $"objectCollectionModPriority:{rowKey}",
                ref priority,
                priorityPos,
                new Vector2(priorityWidth, buttonSize),
                entry.Enabled))
        {
            _modSettings.UpdateObjectCollectionEntry(collection, index, entry with { Priority = priority });
            changed = true;
        }

        using var afterPriority = ImRaii.Disabled(changed);
        ImGui.SetCursorScreenPos(enabledPos);
        if (EditorSegmentedControl.DrawCompactActionSegment(
                $"objectCollectionModEnabled:{rowKey}",
                entry.Enabled ? FontAwesomeIcon.Check : FontAwesomeIcon.Ban,
                enabled: true,
                entry.Enabled ? "Disable mod for this collection" : "Enable mod for this collection",
                entry.Enabled ? ThemeColors.AccentGreen : ThemeColors.AccentGrey,
                new Vector2(buttonSize),
                0,
                2))
        {
            _modSettings.UpdateObjectCollectionEntry(collection, index, entry with { Enabled = !entry.Enabled });
            changed = true;
        }

        using var afterEnabled = ImRaii.Disabled(changed);
        const KeyboardModifiers removeModifier = KeyboardModifiers.Control;
        bool removeEnabled = UiKeyboard.AreModifiersDown(removeModifier);
        ImGui.SetCursorScreenPos(removePos);
        bool remove = EditorSegmentedControl.DrawCompactActionSegment(
                $"objectCollectionModRemove:{rowKey}",
                FontAwesomeIcon.Trash,
                removeEnabled,
                removeEnabled
                    ? "Remove mod from this collection"
                    : $"Hold {KeyboardGestureFormatter.Format(removeModifier)} to remove mod from this collection",
                ThemeColors.DimRed,
                new Vector2(buttonSize),
                1,
                2,
                disabledIconColor: ThemeColors.DimRed with { W = 0.62f });
        changed |= remove;

        if (!changed && interaction.Clicked && !MouseInAnyObjectCollectionModRowControl(
                priorityPos,
                priorityPos + new Vector2(priorityWidth, buttonSize),
                enabledPos,
                removePos,
                buttonSize))
        {
            ToggleObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
            expanded = IsObjectCollectionModRowExpanded(collection.Record.CollectionId, entry);
        }

        ImGui.SetCursorPos(cursorAfterRow);
        using var afterRemove = ImRaii.Disabled(changed);
        if (expanded)
        {
            if (settingsView is null)
            {
                settingsView = _objectModDataSource.GetModSettings(entry);
                changedCount = CountObjectCollectionModSettingOverrides(settingsView);
            }

            changed |= _modSettings.DrawObjectCollectionModSettingsPanel(collection, entry, index, settingsView, changedCount);
        }

        if (remove)
        {
            RemoveObjectCollectionEntry(collection, index);
        }

        return changed;
    }

    private void DrawObjectCollectionModContextMenu(string id, string modDirectory)
    {
        using EditorContextMenu.PopupScope menu = EditorContextMenu.BeginForLastItem(id);
        if (!menu)
        {
            return;
        }

        bool enabled = _penumbra.CanOpenWindow && !string.IsNullOrWhiteSpace(modDirectory);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ExternalLinkAlt, "Open in Penumbra", enabled: enabled))
        {
            _penumbra.TryOpenWindow(modDirectory);
        }
    }

    private float DrawObjectCollectionModBadges(
        ImDrawListPtr drawList,
        float right,
        float top,
        int settingCount,
        int changedCount,
        bool enabled,
        Vector4 accent)
    {
        EditorBadge settingsBadge = EditorBadge.Count(
            FontAwesomeIcon.SlidersH,
            settingCount,
            "setting",
            "settings",
            ThemeColors.TextDisabled);
        _collectionModBadgeBuffer.Clear();
        _collectionModBadgeBuffer.Add(settingsBadge);
        if (changedCount > 0)
        {
            _collectionModBadgeBuffer.Add(EditorBadge.Label($"{changedCount} changed", color: accent));
        }

        return EditorBadgeRenderer.DrawRightAligned(
            drawList,
            _collectionModBadgeBuffer,
            null,
            right,
            top,
            selected: false,
            contentAlpha: enabled ? 1f : 0.58f,
            panelSurface: true);
    }

    private static bool DrawObjectCollectionModPriority(
        string id,
        ref int priority,
        Vector2 position,
        Vector2 size,
        bool enabled)
    {
        const float labelWidth = 48f;
        float gap = EditorLayout.Scaled(5f);
        float scaledLabelWidth = EditorLayout.Scaled(labelWidth);
        float controlWidth = MathF.Max(EditorLayout.Scaled(82f), size.X - scaledLabelWidth - gap);
        Vector2 labelSize = ImGui.CalcTextSize("Priority");
        ImGui.GetWindowDrawList().AddText(
            new Vector2(
                position.X,
                position.Y + ((size.Y - labelSize.Y) * 0.5f)),
            ImGui.GetColorU32(enabled ? ThemeColors.TextDisabled : ThemeColors.TextDisabled with { W = 0.52f }),
            "Priority");

        ImGui.SetCursorScreenPos(new Vector2(position.X + scaledLabelWidth + gap, position.Y));
        return EditorIntegerStepper.Draw(
            id,
            ref priority,
            new Vector2(controlWidth, size.Y),
            ThemeColors.AccentPrimary,
            enabled);
    }

    private static int CountObjectCollectionModSettingOverrides(ObjectCollectionModSettingsView settingsView)
    {
        int count = 0;
        foreach (ObjectCollectionModSettingsGroup group in settingsView.Groups)
        {
            if (group.HasOverride)
            {
                ++count;
            }
        }

        return count;
    }

    private bool IsObjectCollectionModRowExpanded(string collectionId, ObjectCollectionModSettings entry)
        => _expandedCollectionModRows.Contains(CollectionModSettingsEditor.BuildObjectCollectionModRowKey(collectionId, entry));

    private void ToggleObjectCollectionModRowExpanded(string collectionId, ObjectCollectionModSettings entry)
    {
        string key = CollectionModSettingsEditor.BuildObjectCollectionModRowKey(collectionId, entry);
        if (!_expandedCollectionModRows.Add(key))
        {
            _expandedCollectionModRows.Remove(key);
        }
    }

    private void ForgetObjectCollectionModRowState(string collectionId, ObjectCollectionModSettings entry)
    {
        string key = CollectionModSettingsEditor.BuildObjectCollectionModRowKey(collectionId, entry);
        _expandedCollectionModRows.Remove(key);
        _modSettings.ForgetState(key);
    }

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
