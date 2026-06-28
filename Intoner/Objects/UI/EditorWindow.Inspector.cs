using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;
using System.Text.Json;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private sealed record PlacedObjectFolderGroup(
        string FolderKey,
        string DisplayLabel,
        string ColorValue,
        IReadOnlyList<ObjectSnapshot> AllObjects,
        IReadOnlyList<ObjectSnapshot> VisibleObjects);
    private sealed record PlacedObjectListState(IReadOnlyList<ObjectSnapshot> UngroupedObjects, IReadOnlyList<PlacedObjectFolderGroup> FolderGroups);
    private readonly record struct PlacedObjectRowGeometry(Vector2 Min, Vector2 Max)
    {
        public float CenterY => Min.Y + ((Max.Y - Min.Y) * 0.5f);
    }

    private static readonly IReadOnlyList<string> FolderColorSwatches =
    [
        string.Empty,
        ..EditorColors.FolderSwatches,
    ];

    private void DrawObjectListPanel(IReadOnlyList<ObjectSnapshot> objects, IReadOnlySet<Guid> activeObjectIds)
    {
        DrawPlacedObjectsHero(objects, activeObjectIds);
        DrawPlacedObjectsListCard(objects, activeObjectIds);
        DrawFolderEditorPopup();
    }

    private void DrawInspectorPanel(IReadOnlyList<ObjectSnapshot> objects, IReadOnlySet<Guid> activeObjectIds)
    {
        var selectedObjectId = _editorSelection.PrimaryObjectId;
        var selected = selectedObjectId.HasValue
            ? objects.FirstOrDefault(entry => entry.Id == selectedObjectId.Value)
            : null;
        var selectedActive = selected is not null && activeObjectIds.Contains(selected.Id);

        DrawChildPanel(
            "##objectInspectorPanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () =>
            {
                DrawInspectorHero(selected, selectedActive);
                DrawInspectorDetailsCard(selected, selectedActive);
            });
    }

    private void DrawInspectorHero(ObjectSnapshot? selected, bool selectedActive)
    {
        DrawPanelCard(
            "inspector-hero",
            EditorColors.ButtonDefault with { W = 0.30f },
            EditorColors.AccentPurple with { W = 0.24f },
            8f * ImGuiHelpers.GlobalScale,
            new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale),
            () =>
            {
                using var table = ImRaii.Table("##objectInspectorHeroHeader", 1, ImGuiTableFlags.SizingStretchSame);
                if (!table)
                {
                    return;
                }

                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.AccentPurple))
                {
                    ImGui.TextUnformatted(FontAwesomeIcon.SlidersH.ToIconString());
                }

                ImGui.SameLine();
                using (ImRaii.Group())
                {
                    ImGui.TextUnformatted("Edit Selected");
                    ImGui.TextDisabled(selected is null
                        ? "No selection"
                        : _editorSelection.Count == 1
                            ? $"{selected.Kind} | {selected.Name}"
                            : $"{_editorSelection.Count} selected | primary {selected.Name}");
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawSelectedObjectActions(selected, selectedActive);

            });
    }

    private void DrawInspectorDetailsCard(ObjectSnapshot? selected, bool selectedActive)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        DrawPanelCard(
            "inspector-details",
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                if (selected is null)
                {
                    DrawPlacedObjectsEmptyState("Select an object to inspect and edit it.", innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##inspectorDetailsScroll",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (child)
                {
                    DrawInspectorSelectedContent(selected, selectedActive);
                }
            });
    }

    private void DrawInspectorSelectedContent(ObjectSnapshot snapshot, bool selectedActive)
    {
        ImGui.TextUnformatted(snapshot.Name);
        ImGui.TextDisabled($"{snapshot.Kind} | created {snapshot.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        if (snapshot.CreatedIn.IsValid)
        {
            ImGui.TextDisabled(BuildObjectCreationContextLabel(snapshot.CreatedIn));
            UiSharedService.AttachToolTip(BuildObjectCreationContextTooltip(snapshot.CreatedIn));
        }
        ImGuiHelpers.ScaledDummy(6f);

        if (DrawHousingPlacementInspector(snapshot))
        {
            ImGuiHelpers.ScaledDummy(6f);
        }

        DrawSelectedObjectState(snapshot, selectedActive);
        ImGuiHelpers.ScaledDummy(6f);

        DrawCommonInspector(snapshot);
        ImGuiHelpers.ScaledDummy(6f);

        switch (snapshot.Kind)
        {
            case ObjectKind.BgObject:
                DrawBgObjectInspector(snapshot);
                break;
            case ObjectKind.Furniture:
                DrawFurnitureInspector(snapshot);
                break;
            case ObjectKind.Vfx:
                DrawVfxInspector(snapshot);
                break;
            case ObjectKind.Light:
                DrawLightInspector(snapshot);
                break;
        }

        ImGuiHelpers.ScaledDummy(6f);
        if (ImGui.CollapsingHeader("Raw Snapshot"))
        {
            foreach (var line in JsonSerializer.Serialize(snapshot, JsonOptions).Split('\n'))
            {
                ImGui.TextUnformatted(line);
            }
        }
    }

    private void DrawPlacedObjectsHero(IReadOnlyList<ObjectSnapshot> objects, IReadOnlySet<Guid> activeObjectIds)
    {
        var objectCount = objects.Count;
        var activeCount = objects.Count(snapshot => activeObjectIds.Contains(snapshot.Id));
        var lockedCount = objects.Count(static snapshot => snapshot.Locked);
        var folderCount = _sceneView.GetPlacedFolders().Count;
        var countsByKind = objects
            .GroupBy(static snapshot => snapshot.Kind)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var currentFilter = _objectFilter;
        var currentKindFilter = _objectKindFilter;
        var countLabel = BuildPlacedObjectCountLabel(objectCount, activeCount, lockedCount);
        if (folderCount > 0)
        {
            var folderLabel = folderCount == 1 ? "1 folder" : $"{folderCount} folders";
            countLabel = $"{countLabel} | {folderLabel}";
        }

        DrawPanelCard(
            "placed-objects-hero",
            EditorColors.ButtonDefault with { W = 0.30f },
            EditorColors.AccentPurple with { W = 0.24f },
            8f * ImGuiHelpers.GlobalScale,
            new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale),
            () =>
            {
                using (var table = ImRaii.Table("##placedObjectsHeroHeader", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    if (table)
                    {
                        ImGui.TableSetupColumn("Info", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed);

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.AccentPurple))
                        {
                            ImGui.TextUnformatted(FontAwesomeIcon.FolderOpen.ToIconString());
                        }

                        ImGui.SameLine();
                        using (ImRaii.Group())
                        {
                            ImGui.TextUnformatted("Placed Objects");
                            ImGui.TextDisabled(countLabel);
                        }

                        ImGui.TableNextColumn();
                        DrawImportClipboardButton();
                        ImGui.SameLine();
                        DrawCreateFolderButton();
                        ImGui.SameLine();
                        DrawClearAllButton(_sceneView.HasPersistedObjects());
                    }
                }

                ImGui.SetNextItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X - (34f * ImGuiHelpers.GlobalScale)));
                ImGui.InputTextWithHint("##objectListFilter", "search placed objects", ref currentFilter, 128);

                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##clearPlacedObjectsFilter", new Vector2(28f * ImGuiHelpers.GlobalScale, 0f)))
                    {
                        currentFilter = string.Empty;
                    }
                }

                currentKindFilter = DrawPlacedObjectKindFilters(currentKindFilter, countsByKind);
            });

        _objectFilter = currentFilter;
        _objectKindFilter = currentKindFilter;
    }

    private ObjectKind? DrawPlacedObjectKindFilters(ObjectKind? currentFilter, IReadOnlyDictionary<ObjectKind, int> countsByKind)
    {
        var filterValue = currentFilter?.ToString() ?? string.Empty;
        var filterCounts = Enum.GetValues<ObjectKind>()
            .Select(kind => new ObjectCatalogFilterCount(kind.ToString(), countsByKind.GetValueOrDefault(kind)))
            .ToList();
        var nextFilterValue = DrawCatalogFilterButtons("placed_objects", filterValue, filterCounts);
        return Enum.TryParse<ObjectKind>(nextFilterValue, out var nextFilter)
            ? nextFilter
            : null;
    }

    private void SyncPlacedFolderCollapseState(IReadOnlyList<ObjectSnapshot> objects, IReadOnlyList<string> placedFolders)
    {
        var knownFolders = ObjectFolderUtility.OrderFolders(
            placedFolders.Concat(objects
                .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.FolderPath))
                .Select(static snapshot => snapshot.FolderPath)));
        var knownFolderSet = new HashSet<string>(knownFolders, StringComparer.OrdinalIgnoreCase);
        _collapsedPlacedFolders.RemoveWhere(folder => !knownFolderSet.Contains(folder));
    }

    private bool IsPlacedFolderCollapsed(string folderPath)
        => _collapsedPlacedFolders.Contains(folderPath);

    private void TogglePlacedFolderCollapsed(string folderPath)
    {
        if (!_collapsedPlacedFolders.Add(folderPath))
        {
            _collapsedPlacedFolders.Remove(folderPath);
        }
    }

    private void DrawPlacedObjectsListCard(IReadOnlyList<ObjectSnapshot> objects, IReadOnlySet<Guid> activeObjectIds)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var itemSpacingY = 2f * ImGuiHelpers.GlobalScale;
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - (itemSpacingY * 2f));
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        DrawPanelCard(
            "placed-objects-list",
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                var placedFolders = _sceneView.GetPlacedFolders();
                var placedFolderColors = _objectFolderService.GetSceneFolderColors();
                SyncPlacedFolderCollapseState(objects, placedFolders);
                var listState = BuildPlacedObjectListState(
                    objects,
                    placedFolders,
                    placedFolderColors,
                    activeObjectIds,
                    _objectFilter,
                    _objectKindFilter);
                var hasAnyPlacedEntries = objects.Count > 0 || placedFolders.Count > 0;

                if (listState.FolderGroups.Count == 0 && listState.UngroupedObjects.Count == 0)
                {
                    DrawPlacedObjectsEmptyState(
                        !hasAnyPlacedEntries
                            ? "Create or import an object to start organizing the scene."
                            : "No placed objects or folders match the current filter.",
                        innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##placedObjectEntries",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (child)
                {
                    var topInset = 2f * ImGuiHelpers.GlobalScale;
                    ImGui.Dummy(new Vector2(0f, topInset));

                    var itemHeight = MathF.Max(52f * ImGuiHelpers.GlobalScale, (ImGui.GetTextLineHeight() * 2f) + (20f * ImGuiHelpers.GlobalScale));
                    var folderRowHeight = MathF.Max(44f * ImGuiHelpers.GlobalScale, (ImGui.GetTextLineHeight() * 2f) + (14f * ImGuiHelpers.GlobalScale));
                    using var zeroItemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
                    for (var i = 0; i < listState.UngroupedObjects.Count; ++i)
                    {
                        var snapshot = listState.UngroupedObjects[i];
                        var isActive = activeObjectIds.Contains(snapshot.Id);
                        _ = DrawPlacedObjectCard(
                            snapshot,
                            isActive,
                            _editorSelection.Contains(snapshot.Id),
                            () =>
                            {
                                if (snapshot.Locked)
                                {
                                    return;
                                }

                                HandleSelectionChanged(_editorSelection.TrySelect(snapshot.Id, ImGui.GetIO().KeyCtrl));
                            },
                            placedFolders,
                            itemHeight);

                        if (i + 1 < listState.UngroupedObjects.Count)
                        {
                            ImGui.Dummy(new Vector2(0f, itemSpacingY));
                        }
                    }

                    if (listState.UngroupedObjects.Count > 0 && listState.FolderGroups.Count > 0)
                    {
                        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
                    }

                    for (var groupIndex = 0; groupIndex < listState.FolderGroups.Count; ++groupIndex)
                    {
                        var group = listState.FolderGroups[groupIndex];
                        var collapsed = IsPlacedFolderCollapsed(group.FolderKey);
                        DrawPlacedObjectFolderRow(group, activeObjectIds, folderRowHeight, collapsed);

                        if (!collapsed && group.VisibleObjects.Count > 0)
                        {
                            DrawPlacedObjectFolderChildren(group, activeObjectIds, placedFolders, itemHeight, itemSpacingY);
                        }

                        if (groupIndex + 1 < listState.FolderGroups.Count)
                        {
                            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
                        }
                    }
                }

            });
    }

    private static PlacedObjectListState BuildPlacedObjectListState(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
        IReadOnlySet<Guid> activeObjectIds,
        string filter,
        ObjectKind? kindFilter)
    {
        var ungroupedObjects = objects
            .Where(static snapshot => string.IsNullOrWhiteSpace(snapshot.FolderPath))
            .Where(snapshot => MatchesObjectFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter, kindFilter))
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
        var groupedObjects = objects
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.FolderPath))
            .GroupBy(static snapshot => snapshot.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var orderedFolders = ObjectFolderUtility.OrderFolders(
            folders.Concat(objects
                .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.FolderPath))
                .Select(static snapshot => snapshot.FolderPath)));

        List<PlacedObjectFolderGroup> folderGroups = [];
        foreach (var folder in orderedFolders)
        {
            var allFolderObjects = groupedObjects.GetValueOrDefault(folder) ?? [];
            var kindMatchedObjects = allFolderObjects
                .Where(snapshot => MatchesObjectKindFilter(snapshot, kindFilter))
                .ToList();
            var folderMatchesSearch = string.IsNullOrWhiteSpace(filter)
                || folder.Contains(filter, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<ObjectSnapshot> visibleObjects = folderMatchesSearch
                ? kindMatchedObjects
                : kindMatchedObjects
                    .Where(snapshot => MatchesObjectSearchFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter))
                    .ToList();
            var includeEmptyFolder = !kindFilter.HasValue && folderMatchesSearch;
            if (visibleObjects.Count == 0 && !includeEmptyFolder)
            {
                continue;
            }

            folderGroups.Add(new PlacedObjectFolderGroup(
                folder,
                ResolveFolderDisplayLabel(folder),
                ObjectFolderUtility.GetFolderColorValue(folderColors, folder),
                allFolderObjects,
                visibleObjects));
        }

        return new PlacedObjectListState(ungroupedObjects, folderGroups);
    }

    private bool DrawCreateFolderButton()
    {
        if (!DrawAccentIconButton("objectCreateFolder", FontAwesomeIcon.Folder, "Create Folder", EditorColors.AccentPurple))
        {
            return false;
        }

        QueueCreateFolderEditor();
        return true;
    }

    private void QueueCreateFolderEditor()
    {
        _folderEditorMode = FolderEditorMode.Create;
        _folderEditorSourcePath = string.Empty;
        _folderEditorInput = string.Empty;
        _openFolderEditorPopupNextFrame = true;
    }

    private void QueueRenameFolderEditor(string folderPath)
    {
        _folderEditorMode = FolderEditorMode.Rename;
        _folderEditorSourcePath = folderPath;
        _folderEditorInput = folderPath;
        _openFolderEditorPopupNextFrame = true;
    }

    private void DrawFolderEditorPopup()
    {
        if (_openFolderEditorPopupNextFrame)
        {
            ImGui.OpenPopup(FolderEditorPopupId);
            _openFolderEditorPopupNextFrame = false;
        }

        using var popup = ImRaii.Popup(FolderEditorPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        var title = _folderEditorMode == FolderEditorMode.Create
            ? "Create Folder"
            : "Rename Folder";
        var actionLabel = _folderEditorMode == FolderEditorMode.Create
            ? "Create Folder"
            : "Rename Folder";

        ImGui.TextUnformatted(title);
        ImGui.Separator();
        if (_folderEditorMode == FolderEditorMode.Rename)
        {
            ImGui.TextDisabled($"Current: {ResolveFolderDisplayLabel(_folderEditorSourcePath)}");
            ImGuiHelpers.ScaledDummy(4f);
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(MathF.Max(240f * ImGuiHelpers.GlobalScale, 1f));
        ImGui.InputTextWithHint("##folderEditorInput", "folder name", ref _folderEditorInput, 256);

        var canApply = !string.IsNullOrWhiteSpace(ObjectFolderUtility.SanitizeFolderPath(_folderEditorInput));
        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button(actionLabel) && canApply)
            {
                var applied = _folderEditorMode == FolderEditorMode.Create
                    ? TryCreateFolderWithHistory(_folderEditorInput)
                    : TryRenameFolderWithHistory(_folderEditorSourcePath, _folderEditorInput);
                if (applied)
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawPlacedObjectFolderRow(PlacedObjectFolderGroup group, IReadOnlySet<Guid> activeObjectIds, float height, bool collapsed)
    {
        var activeCount = group.AllObjects.Count(snapshot => activeObjectIds.Contains(snapshot.Id));
        var allVisible = group.AllObjects.Count > 0 && group.AllObjects.All(static snapshot => snapshot.Visible);
        var allLocked = group.AllObjects.Count > 0 && group.AllObjects.All(static snapshot => snapshot.Locked);
        var startPos = ImGui.GetCursorPos();
        var insetX = 4f * ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (insetX * 2f));
        var countLabel = BuildPlacedObjectCountLabel(group.AllObjects.Count, activeCount, group.AllObjects.Count(static snapshot => snapshot.Locked));

        ImGui.SetCursorPosX(startPos.X + insetX);
        ListEntryCardInteraction interaction = DrawListEntryCardInteraction(
            $"placedFolderEntry:{group.FolderKey}",
            false,
            new Vector2(width, height),
            ImGuiSelectableFlags.AllowItemOverlap);

        using (var folderPopup = ImRaii.ContextPopupItem($"##placedFolderContext:{group.FolderKey}"))
        {
            if (folderPopup)
            {
                if (DrawContextMenuItem(FontAwesomeIcon.Edit, "Rename Folder"))
                {
                    QueueRenameFolderEditor(group.FolderKey);
                }

                if (DrawContextMenuItem(FontAwesomeIcon.Unlink, "Dissolve Folder"))
                {
                    _ = TryDissolveFolderWithHistory(group.FolderKey);
                }

                ImGui.Separator();
                ImGui.TextDisabled("Folder Color");
                var currentColorValue = ObjectFolderUtility.SanitizeFolderColorValue(group.ColorValue);
                for (var i = 0; i < FolderColorSwatches.Count; ++i)
                {
                    if (i > 0)
                    {
                        ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
                    }

                    var swatchColorValue = FolderColorSwatches[i];
                    if (DrawFolderColorSwatch(
                            $"placedFolderColor:{group.FolderKey}:{i}",
                            swatchColorValue,
                            string.Equals(currentColorValue, swatchColorValue, StringComparison.OrdinalIgnoreCase))
                        && TrySetFolderColorWithHistory(group.FolderKey, swatchColorValue))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        var accent = ResolveFolderAccentColor(group.ColorValue);
        var min = interaction.Min;
        var max = interaction.Max;
        var fill = EditorColors.ButtonDefault with { W = 0.20f };
        var border = accent with { W = interaction.Hovered ? 0.54f : 0.28f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;
        var padX = 10f * ImGuiHelpers.GlobalScale;
        var textLineHeight = ImGui.GetTextLineHeight();
        var textGapY = 4f * ImGuiHelpers.GlobalScale;

        DrawListEntryCardFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = 0.54f },
            rounding,
            interaction.Hovered);

        var cursorAfterRow = ImGui.GetCursorPos();
        var buttonSize = MathF.Max(
            ResolveSquareIconButtonMetrics(FontAwesomeIcon.Eye.ToIconString()).Edge,
            MathF.Max(
                ResolveSquareIconButtonMetrics(FontAwesomeIcon.EyeSlash.ToIconString()).Edge,
                MathF.Max(
                    ResolveSquareIconButtonMetrics(FontAwesomeIcon.Lock.ToIconString()).Edge,
                    ResolveSquareIconButtonMetrics(FontAwesomeIcon.Unlock.ToIconString()).Edge)));
        var buttonGap = 6f * ImGuiHelpers.GlobalScale;
        var rowHeight = max.Y - min.Y;
        var buttonY = min.Y + ((rowHeight - buttonSize) * 0.5f);
        var buttonRight = max.X - padX;
        var lockButtonPos = new Vector2(buttonRight - buttonSize, buttonY);
        var visibilityButtonPos = new Vector2(lockButtonPos.X - buttonGap - buttonSize, buttonY);
        if (interaction.Clicked)
        {
            var mousePos = ImGui.GetMousePos();
            var clickedVisibilityButton = mousePos.X >= visibilityButtonPos.X
                && mousePos.X <= visibilityButtonPos.X + buttonSize
                && mousePos.Y >= visibilityButtonPos.Y
                && mousePos.Y <= visibilityButtonPos.Y + buttonSize;
            var clickedLockButton = mousePos.X >= lockButtonPos.X
                && mousePos.X <= lockButtonPos.X + buttonSize
                && mousePos.Y >= lockButtonPos.Y
                && mousePos.Y <= lockButtonPos.Y + buttonSize;
            if (!clickedVisibilityButton && !clickedLockButton)
            {
                TogglePlacedFolderCollapsed(group.FolderKey);
                collapsed = IsPlacedFolderCollapsed(group.FolderKey);
            }
        }

        ImGui.SetCursorScreenPos(visibilityButtonPos);
        using (ImRaii.Disabled(group.AllObjects.Count == 0))
        {
            if (DrawIconButton(
                    $"placedFolderVisibility:{group.FolderKey}",
                    allVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                    allVisible ? "Hide folder objects" : "Show folder objects",
                    selected: allVisible,
                    edgeOverride: buttonSize))
            {
                var nextVisible = !allVisible;
                var targetObjects = group.AllObjects
                    .Where(snapshot => snapshot.Visible != nextVisible)
                    .ToList();
                if (targetObjects.Count > 0)
                {
                    _ = TryApplySelectedSnapshotUpdateWithHistory(
                        ObjectHistoryKind.Visibility,
                        nextVisible ? "Show Objects" : "Hide Objects",
                        targetObjects,
                        snapshot => snapshot with { Visible = nextVisible });
                }
            }
        }

        ImGui.SetCursorScreenPos(lockButtonPos);
        using (ImRaii.Disabled(group.AllObjects.Count == 0))
        {
            if (DrawIconButton(
                    $"placedFolderLock:{group.FolderKey}",
                    allLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock,
                    allLocked ? "Unlock folder objects" : "Lock folder objects",
                    selected: allLocked,
                    edgeOverride: buttonSize))
            {
                var nextLocked = !allLocked;
                var targetObjects = group.AllObjects
                    .Where(snapshot => snapshot.Locked != nextLocked)
                    .ToList();
                if (targetObjects.Count > 0)
                {
                    _ = TryApplySelectedSnapshotUpdateWithHistory(
                        ObjectHistoryKind.Organization,
                        nextLocked ? "Lock Objects" : "Unlock Objects",
                        targetObjects,
                        snapshot => snapshot with { Locked = nextLocked });
                }
            }
        }

        ImGui.SetCursorPos(cursorAfterRow);

        var disclosureText = (collapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronDown).ToIconString();
        var folderIconText = collapsed ? FontAwesomeIcon.Folder.ToIconString() : FontAwesomeIcon.FolderOpen.ToIconString();
        var disclosureSize = Vector2.Zero;
        var folderIconSize = Vector2.Zero;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            disclosureSize = ImGui.CalcTextSize(disclosureText);
            folderIconSize = ImGui.CalcTextSize(folderIconText);
        }

        var disclosurePos = new Vector2(
            min.X + padX,
            min.Y + ((rowHeight - disclosureSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                disclosurePos,
                ImGui.GetColorU32(accent with { W = 0.84f }),
                disclosureText);
        }

        var folderIconPos = new Vector2(
            disclosurePos.X + disclosureSize.X + (8f * ImGuiHelpers.GlobalScale),
            min.Y + ((rowHeight - folderIconSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                folderIconPos,
                ImGui.GetColorU32(accent with { W = 0.92f }),
                folderIconText);
        }

        var contentBlockHeight = (textLineHeight * 2f) + textGapY;
        var contentStartY = min.Y + ((rowHeight - contentBlockHeight) * 0.5f);
        var titleY = contentStartY;
        var subtitleY = titleY + textLineHeight + textGapY;
        var labelX = folderIconPos.X + folderIconSize.X + (10f * ImGuiHelpers.GlobalScale);
        var contentRight = visibilityButtonPos.X - (10f * ImGuiHelpers.GlobalScale);
        var labelWidth = MathF.Max(ResolveMinimumCardTextWidth(), contentRight - labelX);
        var textColor = EditorColors.Text;
        var disabledText = EditorColors.TextDisabled;
        drawList.AddText(
            new Vector2(labelX, titleY),
            ImGui.GetColorU32(textColor),
            ClipTextToWidth(group.DisplayLabel, labelWidth));
        drawList.AddText(
            new Vector2(labelX, subtitleY),
            ImGui.GetColorU32(disabledText with { W = 0.90f }),
            ClipTextToWidth(countLabel, labelWidth));
    }

    private void DrawPlacedObjectFolderChildren(
        PlacedObjectFolderGroup group,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<string> placedFolders,
        float itemHeight,
        float itemSpacingY)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var childInset = 28f * scale;
        var childRightInset = 4f * scale;
        var lineX = ImGui.GetCursorScreenPos().X + (18f * scale);
        float? firstChildLineStartY = null;
        var lastChildCenterY = 0f;
        var drawList = ImGui.GetWindowDrawList();
        var treeLineColor = ResolveFolderAccentColor(group.ColorValue) with { W = 0.76f };

        ImGui.Dummy(new Vector2(0f, 2f * scale));
        for (var i = 0; i < group.VisibleObjects.Count; ++i)
        {
            var snapshot = group.VisibleObjects[i];
            var isActive = activeObjectIds.Contains(snapshot.Id);
            var rowGeometry = DrawPlacedObjectCard(
                snapshot,
                isActive,
                _editorSelection.Contains(snapshot.Id),
                () =>
                {
                    if (snapshot.Locked)
                    {
                        return;
                    }

                    HandleSelectionChanged(_editorSelection.TrySelect(snapshot.Id, ImGui.GetIO().KeyCtrl));
                },
                placedFolders,
                itemHeight,
                childInset,
                childRightInset);
            firstChildLineStartY ??= rowGeometry.Min.Y - (2f * scale);
            lastChildCenterY = rowGeometry.CenterY;

            drawList.AddLine(
                new Vector2(lineX, rowGeometry.CenterY),
                new Vector2(lineX + (9f * scale), rowGeometry.CenterY),
                ImGui.GetColorU32(treeLineColor),
                1f * scale);

            if (i + 1 < group.VisibleObjects.Count)
            {
                ImGui.Dummy(new Vector2(0f, itemSpacingY));
            }
        }

        if (firstChildLineStartY.HasValue)
        {
            drawList.AddLine(
                new Vector2(lineX, firstChildLineStartY.Value),
                new Vector2(lineX, lastChildCenterY),
                ImGui.GetColorU32(treeLineColor),
                1f * scale);
        }
    }

    private static string BuildObjectCreationContextLabel(ObjectCreationContext createdIn)
    {
        var worldLabel = !string.IsNullOrWhiteSpace(createdIn.WorldName)
            ? createdIn.WorldName
            : createdIn.WorldId != 0
                ? $"World #{createdIn.WorldId}"
                : "Unknown";
        var territoryLabel = !string.IsNullOrWhiteSpace(createdIn.TerritoryName)
            ? createdIn.TerritoryName
            : createdIn.TerritoryId != 0
                ? $"Territory #{createdIn.TerritoryId}"
                : "Unknown";

        var segments = new List<string>
        {
            $"server {worldLabel}",
            $"zone {territoryLabel}",
        };

        if (createdIn.WardId != 0)
        {
            segments.Add($"ward #{createdIn.WardId}");
        }

        if (createdIn.DivisionId == 2)
        {
            segments.Add("subdivision");
        }

        if (createdIn.HouseId == 100)
        {
            segments.Add("apartment");
        }
        else if (createdIn.HouseId != 0)
        {
            segments.Add($"house #{createdIn.HouseId}");
        }

        if (createdIn.RoomId != 0)
        {
            segments.Add($"room #{createdIn.RoomId}");
        }

        return string.Join(" | ", segments);
    }

    private static string BuildObjectCreationContextTooltip(ObjectCreationContext createdIn)
    {
        var worldLabel = !string.IsNullOrWhiteSpace(createdIn.WorldName)
            ? createdIn.WorldName
            : "Unknown";
        var territoryLabel = !string.IsNullOrWhiteSpace(createdIn.TerritoryName)
            ? createdIn.TerritoryName
            : "Unknown";
        var lines = new List<string>
        {
            $"server: {worldLabel}",
            $"zone: {territoryLabel}",
        };

        if (createdIn.DivisionId != 0)
        {
            lines.Add(createdIn.DivisionId == 2
                ? "division: subdivision"
                : "division: main");
        }

        if (createdIn.WardId != 0)
        {
            lines.Add($"ward: #{createdIn.WardId}");
        }

        if (createdIn.HouseId == 100)
        {
            lines.Add("housing: apartment");
        }
        else if (createdIn.HouseId != 0)
        {
            lines.Add($"house: #{createdIn.HouseId}");
        }

        if (createdIn.RoomId != 0)
        {
            lines.Add($"room: #{createdIn.RoomId}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void DrawPlacedObjectsEmptyState(string text, float height)
    {
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (1f * ImGuiHelpers.GlobalScale));
        var accent = EditorColors.AccentPurple;
        var start = ImGui.GetCursorScreenPos();
        var displayHeight = MathF.Max(120f * ImGuiHelpers.GlobalScale, height);
        var textColor = EditorColors.Text;
        var disabledText = EditorColors.TextDisabled;
        var icon = FontAwesomeIcon.Search.ToIconString();
        var iconSize = ImGui.CalcTextSize(icon);
        var wrapWidth = width - (36f * ImGuiHelpers.GlobalScale);
        var messageLines = WrapCenteredTextLines(text, wrapWidth);
        var lineHeight = ImGui.GetTextLineHeight();
        var lineSpacing = 2f * ImGuiHelpers.GlobalScale;

        ImGui.InvisibleButton("##placedObjectsEmptyState", new Vector2(width, displayHeight));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.GetWindowDrawList().AddText(
                UiBuilder.IconFont,
                UiBuilder.IconFont.FontSize,
                new Vector2(start.X + ((width - iconSize.X) * 0.5f), start.Y + (26f * ImGuiHelpers.GlobalScale)),
                ImGui.GetColorU32(accent with { W = 0.8f }),
                icon);
        }

        for (var i = 0; i < messageLines.Count; ++i)
        {
            var line = messageLines[i];
            var lineSize = ImGui.CalcTextSize(line);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(
                    start.X + ((width - lineSize.X) * 0.5f),
                    start.Y + (58f * ImGuiHelpers.GlobalScale) + (i * (lineHeight + lineSpacing))),
                ImGui.GetColorU32(disabledText with { W = textColor.W }),
                line);
        }
    }

    private static IReadOnlyList<string> WrapCenteredTextLines(string text, float wrapWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var currentLine = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = string.IsNullOrEmpty(currentLine)
                    ? word
                    : $"{currentLine} {word}";

                if (!string.IsNullOrEmpty(currentLine) && ImGui.CalcTextSize(candidate).X > wrapWidth)
                {
                    lines.Add(currentLine);
                    currentLine = word;
                    continue;
                }

                currentLine = candidate;
            }

            if (!string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    private PlacedObjectRowGeometry DrawPlacedObjectCard(
        ObjectSnapshot snapshot,
        bool isActive,
        bool selected,
        Action onSelect,
        IReadOnlyList<string> placedFolders,
        float height,
        float leftInset = -1f,
        float rightInset = -1f)
    {
        var startPos = ImGui.GetCursorPos();
        var resolvedLeftInset = leftInset >= 0f
            ? leftInset
            : 4f * ImGuiHelpers.GlobalScale;
        var resolvedRightInset = rightInset >= 0f
            ? rightInset
            : 4f * ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - resolvedLeftInset - resolvedRightInset);
        var accent = EditorColors.AccentPurple;
        var stateBadgeText = isActive ? "Active" : "Inactive";
        var stateBadgeColor = isActive ? EditorColors.AccentGreen : EditorColors.AccentBlue;
        var kindBadgeText = snapshot.Kind.ToString();
        var contentAlpha = snapshot.Locked
            ? (isActive ? 0.78f : 0.62f)
            : isActive ? 1f : 0.72f;

        ImGui.SetCursorPosX(startPos.X + resolvedLeftInset);
        ListEntryCardInteraction interaction = DrawListEntryCardInteraction(
            $"placedObjectEntry:{snapshot.Id}",
            selected,
            new Vector2(width, height));
        if (interaction.Clicked && !snapshot.Locked)
        {
            onSelect();
        }

        using (var objectPopup = ImRaii.ContextPopupItem($"##placedObjectContext:{snapshot.Id}"))
        {
            if (objectPopup)
            {
                using (var assignFolderSubMenu = BeginContextSubMenu($"placedObjectAssignFolder:{snapshot.Id}", FontAwesomeIcon.FolderOpen, "Assign Folder"))
                {
                    if (assignFolderSubMenu)
                    {
                        if (DrawContextMenuItem(FontAwesomeIcon.TimesCircle, "Ungrouped", string.IsNullOrWhiteSpace(snapshot.FolderPath)))
                        {
                            _ = TrySetObjectFolderWithHistory(snapshot, string.Empty);
                        }

                        foreach (var folderPath in placedFolders)
                        {
                            var folderSelected = string.Equals(snapshot.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
                            if (DrawContextMenuItem(FontAwesomeIcon.Folder, folderPath, folderSelected))
                            {
                                _ = TrySetObjectFolderWithHistory(snapshot, folderPath);
                            }
                        }
                    }
                }

                if (DrawContextMenuItem(snapshot.Visible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, snapshot.Visible ? "Hide Object" : "Show Object"))
                {
                    _ = TrySetObjectVisibleWithHistory(snapshot, !snapshot.Visible);
                }

                if (DrawContextMenuItem(snapshot.Locked ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock, snapshot.Locked ? "Unlock Object" : "Lock Object"))
                {
                    _ = TrySetObjectLockedWithHistory(snapshot, !snapshot.Locked);
                }

                if (DrawContextMenuItem(FontAwesomeIcon.Copy, "Duplicate"))
                {
                    _ = TryDuplicateSelectedObjects([snapshot]);
                }

                if (DrawContextMenuItem(FontAwesomeIcon.Trash, "Delete"))
                {
                    _ = TryRemoveSelectedObjects([snapshot]);
                }

                if (DrawContextMenuItem(FontAwesomeIcon.Clipboard, "Export To Clipboard"))
                {
                    ExportObjectToClipboard(snapshot);
                }
            }
        }

        var endPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(startPos.X, endPos.Y));

        var min = interaction.Min;
        var max = interaction.Max;
        var drawList = ImGui.GetWindowDrawList();
        var fill = selected
            ? accent with { W = isActive ? 0.17f : 0.13f }
            : interaction.Hovered
                ? accent with { W = isActive ? 0.08f : 0.05f }
                : EditorColors.ButtonDefault with { W = isActive ? 0.22f : 0.16f };
        var border = selected
            ? accent with { W = isActive ? 0.85f : 0.72f }
            : interaction.Hovered
                ? accent with { W = isActive ? 0.58f : 0.42f }
                : EditorColors.Border with { W = isActive ? 0.38f : 0.28f };
        var text = EditorColors.Text with { W = EditorColors.Text.W * contentAlpha };
        var disabledText = EditorColors.TextDisabled with { W = EditorColors.TextDisabled.W * contentAlpha };
        var rounding = 8f * ImGuiHelpers.GlobalScale;
        var padX = 10f * ImGuiHelpers.GlobalScale;
        var padY = 8f * ImGuiHelpers.GlobalScale;

        DrawListEntryCardFrame(
            drawList,
            min,
            max,
            fill,
            border,
            accent with { W = selected ? 0.95f : 0.55f },
            rounding,
            interaction.Hovered && !selected);

        var badgePaddingX = 7f * ImGuiHelpers.GlobalScale;
        var badgePaddingY = 3f * ImGuiHelpers.GlobalScale;
        var badgeGap = 6f * ImGuiHelpers.GlobalScale;
        var badgeRight = max.X - padX;

        var kindBadgeTextSize = ImGui.CalcTextSize(kindBadgeText);
        var kindBadgeMin = new Vector2(badgeRight - kindBadgeTextSize.X - (badgePaddingX * 2f), min.Y + padY);
        var kindBadgeMax = new Vector2(badgeRight, kindBadgeMin.Y + kindBadgeTextSize.Y + (badgePaddingY * 2f));
        drawList.AddRectFilled(kindBadgeMin, kindBadgeMax, ImGui.GetColorU32(accent with { W = selected ? 0.36f : 0.22f }), 999f);
        drawList.AddText(new Vector2(kindBadgeMin.X + badgePaddingX, kindBadgeMin.Y + badgePaddingY), ImGui.GetColorU32(text), kindBadgeText);
        badgeRight = kindBadgeMin.X - badgeGap;

        var stateBadgeTextSize = ImGui.CalcTextSize(stateBadgeText);
        var stateBadgeMin = new Vector2(badgeRight - stateBadgeTextSize.X - (badgePaddingX * 2f), kindBadgeMin.Y);
        var stateBadgeMax = new Vector2(badgeRight, kindBadgeMax.Y);
        drawList.AddRectFilled(stateBadgeMin, stateBadgeMax, ImGui.GetColorU32(stateBadgeColor with { W = selected ? 0.28f : 0.20f }), 999f);
        drawList.AddRect(stateBadgeMin, stateBadgeMax, ImGui.GetColorU32(stateBadgeColor with { W = selected ? 0.75f : 0.52f }), 999f);
        drawList.AddText(new Vector2(stateBadgeMin.X + badgePaddingX, stateBadgeMin.Y + badgePaddingY), ImGui.GetColorU32(stateBadgeColor with { W = text.W }), stateBadgeText);
        badgeRight = stateBadgeMin.X - badgeGap;

        Vector2? lockBadgeMin = null;
        Vector2? lockBadgeMax = null;
        if (snapshot.Locked)
        {
            var lockBadgeWidth = kindBadgeMax.Y - kindBadgeMin.Y;
            var lockBadgeRounding = 5f * ImGuiHelpers.GlobalScale;
            lockBadgeMin = new Vector2(badgeRight - lockBadgeWidth, kindBadgeMin.Y);
            lockBadgeMax = new Vector2(badgeRight, kindBadgeMax.Y);
            drawList.AddRectFilled(lockBadgeMin.Value, lockBadgeMax.Value, ImGui.GetColorU32(EditorColors.DimRed with { W = selected ? 0.28f : 0.18f }), lockBadgeRounding);
            drawList.AddRect(lockBadgeMin.Value, lockBadgeMax.Value, ImGui.GetColorU32(EditorColors.DimRed with { W = selected ? 0.72f : 0.52f }), lockBadgeRounding);

            using var lockFont = ImRaii.PushFont(UiBuilder.IconFont);
            var lockIcon = FontAwesomeIcon.Lock.ToIconString();
            var lockIconSize = ImGui.CalcTextSize(lockIcon);
            var lockIconPos = new Vector2(
                lockBadgeMin.Value.X + ((lockBadgeWidth - lockIconSize.X) * 0.5f),
                lockBadgeMin.Value.Y + ((lockBadgeWidth - lockIconSize.Y) * 0.5f));
            drawList.AddText(lockIconPos, ImGui.GetColorU32(EditorColors.DimRed with { W = text.W }), lockIcon);
            badgeRight = lockBadgeMin.Value.X - badgeGap;
        }

        var nameStartX = min.X + padX;
        var contentRight = badgeRight;
        var metaWidth = MathF.Max(ResolveMinimumCardTextWidth(), contentRight - nameStartX);
        var nameWidth = metaWidth;
        if (TryGetPlacedObjectColorBadge(snapshot, out var colorBadge))
        {
            var colorBadgeGap = 8f * ImGuiHelpers.GlobalScale;
            var swatchSize = MathF.Max(12f * ImGuiHelpers.GlobalScale, ImGui.GetTextLineHeight() - (2f * ImGuiHelpers.GlobalScale));
            var swatchGap = 6f * ImGuiHelpers.GlobalScale;
            var labelMaxWidth = 120f * ImGuiHelpers.GlobalScale;
            var desiredLabelWidth = MathF.Min(ImGui.CalcTextSize(colorBadge.Label).X, labelMaxWidth);
            var desiredBadgeWidth = swatchSize + swatchGap + desiredLabelWidth;
            nameWidth = MathF.Max(ResolveMinimumCardTextWidth(), metaWidth - colorBadgeGap - desiredBadgeWidth);

            var renderedName = ClipTextToWidth(snapshot.Name, nameWidth);
            drawList.AddText(new Vector2(nameStartX, min.Y + padY), ImGui.GetColorU32(text), renderedName);

            var badgeLeft = nameStartX + ImGui.CalcTextSize(renderedName).X + colorBadgeGap;
            var swatchMin = new Vector2(badgeLeft, min.Y + padY + MathF.Max(0f, (ImGui.GetTextLineHeight() - swatchSize) * 0.5f));
            var swatchMax = swatchMin + new Vector2(swatchSize, swatchSize);
            drawList.AddRectFilled(swatchMin, swatchMax, ImGui.GetColorU32(colorBadge.PreviewColor), 3f * ImGuiHelpers.GlobalScale);
            drawList.AddRect(swatchMin, swatchMax, ImGui.GetColorU32(accent with { W = selected ? 0.78f : 0.58f }), 3f * ImGuiHelpers.GlobalScale);

            var labelMaxX = contentRight;
            var availableLabelWidth = MathF.Max(0f, labelMaxX - (swatchMax.X + swatchGap));
            if (availableLabelWidth > 0f)
            {
                var labelText = ClipTextToWidth(colorBadge.Label, availableLabelWidth);
                var labelPos = new Vector2(swatchMax.X + swatchGap, min.Y + padY);
                drawList.AddText(labelPos, ImGui.GetColorU32(disabledText with { W = 0.94f }), labelText);

                var hoverMin = swatchMin;
                var hoverMax = new Vector2(labelMaxX, MathF.Max(swatchMax.Y, labelPos.Y + ImGui.GetTextLineHeight()));
                if (EditorInputUtility.IsMouseInside(hoverMin, hoverMax))
                {
                    UiSharedService.DrawAccentTooltipText(colorBadge.Tooltip, accent, wrapEms: 35f);
                }
            }
        }
        else
        {
            drawList.AddText(new Vector2(nameStartX, min.Y + padY), ImGui.GetColorU32(text), ClipTextToWidth(snapshot.Name, nameWidth));
        }

        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + (4f * ImGuiHelpers.GlobalScale);
        drawList.AddText(
            new Vector2(min.X + padX, metaY),
            ImGui.GetColorU32(disabledText with { W = 0.88f }),
            ClipTextToWidth(BuildObjectListMeta(snapshot, isActive), metaWidth));

        if (snapshot.Locked && EditorInputUtility.IsMouseInside(min, max))
        {
            UiSharedService.DrawAccentTooltipText(
                "locked objects cannot be selected or edited until they are unlocked",
                EditorColors.AccentYellow,
                wrapEms: 35f);
        }
        else if (lockBadgeMin.HasValue
                 && lockBadgeMax.HasValue
                 && EditorInputUtility.IsMouseInside(lockBadgeMin.Value, lockBadgeMax.Value))
        {
            UiSharedService.DrawAccentTooltipText("locked", EditorColors.AccentYellow, wrapEms: 35f);
        }

        return new PlacedObjectRowGeometry(min, max);
    }

    private bool TrySetObjectFolderWithHistory(ObjectSnapshot snapshot, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.Equals(snapshot.FolderPath, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryApplySelectedSnapshotUpdateWithHistory(
            ObjectHistoryKind.Organization,
            string.IsNullOrEmpty(sanitizedFolderPath) ? "Ungroup Object" : "Set Object Folder",
            [snapshot],
            entry => entry with { FolderPath = sanitizedFolderPath });
    }

    private bool TrySetObjectVisibleWithHistory(ObjectSnapshot snapshot, bool visible)
    {
        if (snapshot.Visible == visible)
        {
            return false;
        }

        return TryApplySelectedSnapshotUpdateWithHistory(
            ObjectHistoryKind.Visibility,
            visible ? "Show Object" : "Hide Object",
            [snapshot],
            entry => entry with { Visible = visible });
    }

    private bool TrySetObjectLockedWithHistory(ObjectSnapshot snapshot, bool locked)
    {
        if (snapshot.Locked == locked)
        {
            return false;
        }

        return TryApplySelectedSnapshotUpdateWithHistory(
            ObjectHistoryKind.Organization,
            locked ? "Lock Object" : "Unlock Object",
            [snapshot],
            entry => entry with { Locked = locked });
    }

    private void ExportObjectToClipboard(ObjectSnapshot snapshot)
        => _clipboardExportService.CopySnapshot(snapshot);

    private bool TryGetPlacedObjectColorBadge(ObjectSnapshot snapshot, out PlacedObjectColorBadge badge)
    {
        switch (snapshot.Model)
        {
            case BgObjectModel bgObjectModel when !IsApproximatelyDefault(bgObjectModel.DyeColor, Vector4.One):
                badge = new PlacedObjectColorBadge(
                    EditorColors.Color(
                        Math.Clamp(bgObjectModel.DyeColor.X, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Y, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Z, 0f, 1f),
                        1f),
                    FormatColorLabel(bgObjectModel.DyeColor),
                    $"Dye Color: {FormatColorLabel(bgObjectModel.DyeColor)}");
                return true;

            case FurnitureModel furnitureModel when furnitureModel.Color.UseCustomColor:
                var customColorLabel = FormatByteColorLabel(furnitureModel.Color.CustomColor);
                badge = new PlacedObjectColorBadge(
                    EditorColors.Color(
                        Math.Clamp(furnitureModel.Color.CustomColor.X, 0f, 1f),
                        Math.Clamp(furnitureModel.Color.CustomColor.Y, 0f, 1f),
                        Math.Clamp(furnitureModel.Color.CustomColor.Z, 0f, 1f),
                        1f),
                    customColorLabel,
                    $"Custom Color: {customColorLabel}");
                return true;

            case FurnitureModel furnitureModel when furnitureModel.Color.StainId != 0:
                var stain = FindFurnitureStain(furnitureModel.Color.StainId);
                var finish = stain.IsMetallic ? "glossy" : "matte";
                badge = new PlacedObjectColorBadge(
                    stain.PreviewColor,
                    $"{stain.Id:000} | {stain.Name}",
                    $"Stain {stain.Id:000}: {stain.Name} ({finish})");
                return true;

            case LightModel lightModel when !IsApproximatelyDefault(lightModel.Color, new Vector3(20f, 20f, 20f)):
                var lightColor = Vector3.SquareRoot(lightModel.Color / 6f);
                badge = new PlacedObjectColorBadge(
                    EditorColors.Color(
                        Math.Clamp(lightColor.X, 0f, 1f),
                        Math.Clamp(lightColor.Y, 0f, 1f),
                        Math.Clamp(lightColor.Z, 0f, 1f),
                        1f),
                    FormatColorLabel(lightColor),
                    $"Light Color: {FormatColorLabel(lightColor)}");
                return true;
        }

        badge = default;
        return false;
    }

    private void DrawSelectedObjectActions(ObjectSnapshot? selected, bool selectedActive)
    {
        DrawExportSelectedButton(selected);
        ImGui.SameLine();
        DrawDuplicateSelectedButton();
        ImGui.SameLine();
        DrawMoveSelectedToPlayerButton(selectedActive);
        ImGui.SameLine();
        DrawToggleLockSelectedButton();
        ImGui.SameLine();
        DrawRemoveSelectedButton();
    }

    private void DrawSelectedObjectState(ObjectSnapshot snapshot, bool selectedActive)
    {
        using var selectedStateTable = CompactSettingsTable("selectedObjectState");
        if (!selectedStateTable)
        {
            return;
        }

        DrawStateRow("Id", snapshot.Id.ToString());
        DrawStateRow("State", selectedActive ? "Active" : "Inactive");
        DrawStateRow("Kind", snapshot.Kind.ToString());
        DrawStateRow("Layout", ResolveObjectLayoutLabel(snapshot));
        DrawStateRow("Folder", ResolveFolderDisplayLabel(snapshot.FolderPath));
        DrawStateRow("Locked", FormatYesNo(snapshot.Locked));
        DrawStateRow("Visible", FormatYesNo(snapshot.Visible));

    }

    private string ResolveObjectLayoutLabel(ObjectSnapshot snapshot)
    {
        if (!snapshot.LayoutId.HasValue)
        {
            return "<none>";
        }

        var layout = _layoutManager.GetLayouts()
            .FirstOrDefault(entry => entry.Id == snapshot.LayoutId.Value);
        return layout?.Name ?? snapshot.LayoutId.Value.ToString();
    }

    private static string BuildObjectCollectionOptionLabel(ObjectCollectionSnapshot collection)
        => ObjectStringUtility.TrimOrFallback(collection.Record.Name, collection.Record.CollectionId);

    private static void DrawStateRow(string label, string value)
    {
        DrawCompactSettingsLabelCell(label);
        ImGui.SetNextItemWidth(-1f);
        ImGui.TextUnformatted(value);
    }

    private void DrawCommonInspector(ObjectSnapshot snapshot)
    {
        using var commonInspectorTable = CompactSettingsTable("commonInspector");
        if (!commonInspectorTable)
        {
            return;
        }

        var folderPath = snapshot.FolderPath;
        DrawCompactSettingsLabelCell("Folder");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##commonFolderPath", "ungrouped", ref folderPath, 256))
        {
            ApplyInspectorSnapshotEdit(
                "CommonFolderPath",
                ObjectHistoryKind.Organization,
                "Set Object Folder",
                snapshot,
                snapshot with { FolderPath = folderPath });
        }

        DrawObjectCollectionSelectionRow(snapshot);

        var locked = snapshot.Locked;
        if (DrawCheckboxRow("commonLocked", "Locked", ref locked))
        {
            ApplyInspectorSnapshotEdit(
                "CommonLocked",
                ObjectHistoryKind.Organization,
                locked ? "Lock Object" : "Unlock Object",
                snapshot,
                snapshot with { Locked = locked },
                recordImmediately: true);
        }

        var name = snapshot.Name;
        DrawCompactSettingsLabelCell("Name");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##commonName", ref name, 128))
        {
            ApplyInspectorSnapshotEdit("CommonName", ObjectHistoryKind.Organization, "Rename Object", snapshot, snapshot with { Name = name });
        }

        var visible = snapshot.Visible;
        if (DrawCheckboxRow("commonVisible", "Visible", ref visible))
        {
            ApplyInspectorSnapshotEdit("CommonVisible", ObjectHistoryKind.Visibility, "Set Object Visibility", snapshot, snapshot with { Visible = visible }, recordImmediately: true);
        }

        var position = snapshot.Transform.Position;
        if (DrawPositionClipboardRow("commonPosition", ref position))
        {
            ApplyInspectorPositionEdit("CommonPosition", "Move Object", ObjectHistoryKind.Move, snapshot, position);
        }

        var rotation = snapshot.Transform.RotationDegrees;
        if (DrawRotationClipboardRow("commonRotation", ref rotation))
        {
            ApplyInspectorRotationEdit("CommonRotation", "Rotate Object", ObjectHistoryKind.Transform, snapshot, rotation);
        }

        if (CanEditInspectorScale(snapshot))
        {
            var scale = snapshot.Transform.Scale;
            if (DrawScaleClipboardRow("commonScale", ref scale))
            {
                ApplyInspectorScaleEdit("CommonScale", "Scale Object", ObjectHistoryKind.Transform, snapshot, scale);
            }
        }
    }

    private static bool CanEditInspectorScale(ObjectSnapshot snapshot)
        => snapshot.Model is BgObjectModel or FurnitureModel or VfxModel;

    private void DrawObjectCollectionSelectionRow(ObjectSnapshot snapshot)
    {
        var collections = _objectCollectionManager.GetCollections();
        var currentCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
        ObjectCollectionSnapshot currentCollection = default!;
        var currentCollectionExists = currentCollectionId.Length > 0
            && TryResolveObjectCollectionById(collections, currentCollectionId, out currentCollection);
        var previewLabel = currentCollectionId.Length == 0
            ? "<none>"
            : currentCollectionExists
                ? BuildObjectCollectionOptionLabel(currentCollection)
                : $"<missing: {currentCollectionId}>";
        DrawCompactSettingsLabelCell("Collection");
        using var combo = ImRaii.Combo("##commonCollection", previewLabel);
        if (!combo)
        {
            return;
        }

        if (currentCollectionId.Length > 0 && !currentCollectionExists)
        {
            using (ImRaii.Disabled())
            {
                ImGui.Selectable($"{previewLabel}##missingObjectCollection", true);
            }

            ImGui.SetItemDefaultFocus();
            ImGui.Separator();
        }

        var noCollectionSelected = currentCollectionId.Length == 0;
        if (ImGui.Selectable("<none>##objectCollectionNone", noCollectionSelected))
        {
            ApplyInspectorObjectCollectionEdit(snapshot, string.Empty);
        }

        if (noCollectionSelected)
        {
            ImGui.SetItemDefaultFocus();
        }

        foreach (var collection in collections)
        {
            var collectionId = collection.Record.CollectionId;
            var selected = string.Equals(collectionId, currentCollectionId, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{BuildObjectCollectionOptionLabel(collection)}##objectCollectionOption:{collectionId}", selected))
            {
                ApplyInspectorObjectCollectionEdit(snapshot, collectionId);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(collectionId);
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
    }

    private void ApplyInspectorObjectCollectionEdit(ObjectSnapshot snapshot, string collectionId)
    {
        var normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (string.Equals(snapshot.CollectionId, normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyInspectorSnapshotEdit(
            "CommonCollection",
            ObjectHistoryKind.Appearance,
            normalizedCollectionId.Length == 0 ? "Clear Object Collection" : "Set Object Collection",
            snapshot,
            snapshot with { CollectionId = normalizedCollectionId },
            recordImmediately: true);
    }

    private void DrawBgObjectInspector(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;
        using var bgObjectInspectorTable = CompactSettingsTable("bgObjectInspector");
        if (!bgObjectInspectorTable)
        {
            return;
        }

        var modelPath = bgObjectModel.ModelPath;
        DrawCompactSettingsLabelCell("Model Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##bgObjectModelPath", ref modelPath, 512))
        {
            ApplyInspectorSnapshotEdit(
                "BgObjectModelPath",
                ObjectHistoryKind.Appearance,
                "Change BgObject Model Path",
                snapshot,
                snapshot with
                {
                    Model = bgObjectModel with { ModelPath = modelPath },
                });
        }

        var transparency = bgObjectModel.Transparency;
        if (DrawSliderFloatRow("bgObjectTransparency", "Transparency", ref transparency, 0f, 1f, "%.3f"))
        {
            ApplyInspectorSnapshotEdit(
                "BgObjectTransparency",
                ObjectHistoryKind.Appearance,
                "Change BgObject Transparency",
                snapshot,
                snapshot with
                {
                    Model = bgObjectModel with { Transparency = transparency },
                });
        }

        var coveredFromRain = bgObjectModel.IsCoveredFromRain;
        if (DrawCheckboxRow("bgObjectCoveredFromRain", "Covered From Rain", ref coveredFromRain))
        {
            ApplyInspectorSnapshotEdit(
                "BgObjectCoveredFromRain",
                ObjectHistoryKind.Appearance,
                "Change Rain Coverage",
                snapshot,
                snapshot with
                {
                    Model = bgObjectModel with { IsCoveredFromRain = coveredFromRain },
                },
                recordImmediately: true);
        }

        DrawCompactSettingsLabelCell("Dye Color");
        var dyeColor = bgObjectModel.DyeColor;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.ColorEdit4("##bgObjectDyeColor", ref dyeColor, ImGuiColorEditFlags.Float))
        {
            ApplyInspectorSnapshotEdit(
                "BgObjectDyeColor",
                ObjectHistoryKind.Appearance,
                "Change BgObject Dye Color",
                snapshot,
                snapshot with
                {
                    Model = bgObjectModel with { DyeColor = dyeColor },
                });
        }

    }

    private void DrawFurnitureInspector(ObjectSnapshot snapshot)
    {
        var furnitureModel = (FurnitureModel)snapshot.Model;
        var furnitureColor = furnitureModel.Color;
        using var furnitureInspectorTable = CompactSettingsTable("furnitureInspector");
        if (!furnitureInspectorTable)
        {
            return;
        }

        var sharedGroupPath = furnitureModel.SharedGroupPath;
        DrawCompactSettingsLabelCell("Shared Group Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##furnitureSharedGroupPath", ref sharedGroupPath, 512))
        {
            ApplyInspectorSnapshotEdit(
                "FurnitureSharedGroupPath",
                ObjectHistoryKind.Appearance,
                "Change Furniture Shared Group Path",
                snapshot,
                snapshot with
                {
                    Model = ResolveFurnitureCatalogVariant(furnitureModel with { SharedGroupPath = sharedGroupPath }),
                });
        }

        var transparency = furnitureModel.Transparency;
        if (DrawSliderFloatRow("furnitureTransparency", "Transparency", ref transparency, 0f, 1f, "%.3f"))
        {
            ApplyInspectorSnapshotEdit(
                "FurnitureTransparency",
                ObjectHistoryKind.Appearance,
                "Change Furniture Transparency",
                snapshot,
                snapshot with
                {
                    Model = furnitureModel with { Transparency = transparency },
                });
        }

        var outlineColor = furnitureModel.OutlineColor;
        if (DrawEnumRow("furnitureOutlineColor", "Outline Color", outlineColor, DrawOutlineColorLabel, ref outlineColor))
        {
            ApplyInspectorSnapshotEdit(
                "FurnitureOutlineColor",
                ObjectHistoryKind.Appearance,
                "Change Furniture Outline Color",
                snapshot,
                snapshot with
                {
                    Model = furnitureModel with { OutlineColor = outlineColor },
                },
                recordImmediately: true);
        }

        var stainId = furnitureColor.StainId;
        var useCustomColor = furnitureColor.UseCustomColor;
        var customColor = furnitureColor.CustomColor;
        if (DrawFurnitureColorEditor(
            "Color",
            "inspector",
            ref useCustomColor,
            ref stainId,
            ref customColor,
            ref _inspectorFurnitureStainFilter,
            (editId, title, nextUseCustomColor, nextStainId, nextCustomColor, recordImmediately) =>
                ApplyInspectorSnapshotEdit(
                    editId,
                    ObjectHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with
                    {
                        Model = furnitureModel with
                        {
                            Color = furnitureColor with
                            {
                                StainId = nextStainId,
                                UseCustomColor = nextUseCustomColor,
                                CustomColor = nextCustomColor,
                            },
                        },
                    },
                    recordImmediately)))
        {
        }
    }

    private void DrawLightInspector(ObjectSnapshot snapshot)
    {
        var lightModel = (LightModel)snapshot.Model;
        _ = DrawLightModelEditor(
            "inspector",
            ref lightModel,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                ApplyInspectorSnapshotEdit(
                    editId,
                    ObjectHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with { Model = updatedModel },
                    recordImmediately));
    }

    private void DrawVfxInspector(ObjectSnapshot snapshot)
    {
        var vfxModel = (VfxModel)snapshot.Model;
        using var vfxInspectorTable = CompactSettingsTable("vfxInspector");
        if (!vfxInspectorTable)
        {
            return;
        }

        var vfxPath = vfxModel.VfxPath;
        DrawCompactSettingsLabelCell("VFX Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##vfxPath", ref vfxPath, 512))
        {
            ApplyInspectorSnapshotEdit(
                "VfxPath",
                ObjectHistoryKind.Appearance,
                "Change VFX Path",
                snapshot,
                snapshot with
                {
                    Model = vfxModel with { VfxPath = vfxPath },
                });
        }

        DrawCompactSettingsLabelCell("Tint");
        var color = vfxModel.Color;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.ColorEdit4("##vfxColor", ref color, ImGuiColorEditFlags.Float))
        {
            ApplyInspectorSnapshotEdit(
                "VfxColor",
                ObjectHistoryKind.Appearance,
                "Change VFX Tint",
                snapshot,
                snapshot with
                {
                    Model = vfxModel with { Color = color },
                });
        }
    }

    private bool DrawLightModelEditor(
        string id,
        ref LightModel model,
        bool includeLightType = true,
        Action<string, string, LightModel, bool>? onChanged = null)
    {
        var changed = false;
        var lightFlags = model.Flags;
        var lightShape = model.Shape;
        var lightShadow = model.Shadow;

        var lightType = model.LightType;
        using var lightModelTable = CompactSettingsTable($"lightModel_{id}");
        if (lightModelTable)
        {
            if (includeLightType && DrawEnumRow($"LightType_{id}", "Light Type", lightType, DrawLightTypeLabel, ref lightType))
            {
                model = model with { LightType = lightType };
                changed = true;
                onChanged?.Invoke("LightType", "Change Light Type", model, true);
            }

            var falloffType = model.FalloffType;
            if (DrawEnumRow($"FalloffType_{id}", "Falloff Type", falloffType, DrawFalloffTypeLabel, ref falloffType))
            {
                model = model with { FalloffType = falloffType };
                changed = true;
                onChanged?.Invoke("LightFalloffType", "Change Light Falloff Type", model, true);
            }

            var reflection = lightFlags.EnableMaterialReflection;
            if (DrawCheckboxRow($"MaterialReflection_{id}", "Material Reflection", ref reflection))
            {
                lightFlags = lightFlags with { EnableMaterialReflection = reflection };
                model = model with { Flags = lightFlags };
                changed = true;
                onChanged?.Invoke("LightMaterialReflection", "Toggle Light Material Reflection", model, true);
            }

            var dynamicLighting = lightFlags.EnableDynamicLighting;
            if (DrawCheckboxRow($"DynamicLighting_{id}", "Dynamic Lighting", ref dynamicLighting))
            {
                lightFlags = lightFlags with { EnableDynamicLighting = dynamicLighting };
                model = model with { Flags = lightFlags };
                changed = true;
                onChanged?.Invoke("LightDynamicLighting", "Toggle Light Dynamic Lighting", model, true);
            }

            var charaShadow = lightFlags.EnableCharacterShadow;
            if (DrawCheckboxRow($"CharacterShadow_{id}", "Character Shadow", ref charaShadow))
            {
                lightFlags = lightFlags with { EnableCharacterShadow = charaShadow };
                model = model with { Flags = lightFlags };
                changed = true;
                onChanged?.Invoke("LightCharacterShadow", "Toggle Light Character Shadow", model, true);
            }

            var objectShadow = lightFlags.EnableObjectShadow;
            if (DrawCheckboxRow($"ObjectShadow_{id}", "Object Shadow", ref objectShadow))
            {
                lightFlags = lightFlags with { EnableObjectShadow = objectShadow };
                model = model with { Flags = lightFlags };
                changed = true;
                onChanged?.Invoke("LightObjectShadow", "Toggle Light Object Shadow", model, true);
            }

            DrawCompactSettingsLabelCell("Color");
            var rawColor = model.Color;
            var lightColor = Vector3.SquareRoot(rawColor / 6f);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit3($"##Color_{id}", ref lightColor, ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float))
            {
                rawColor = lightColor * lightColor * 6f;
                model = model with { Color = rawColor };
                changed = true;
                onChanged?.Invoke("LightColor", "Change Light Color", model, false);
            }

            var intensity = model.Intensity;
            if (DrawDragFloatRow($"Intensity_{id}", "Intensity", ref intensity, 0.05f, 0f, 100f, "%.3f"))
            {
                model = model with { Intensity = intensity };
                changed = true;
                onChanged?.Invoke("LightIntensity", "Change Light Intensity", model, false);
            }

            var range = lightShape.Range;
            if (DrawDragFloatRow($"Range_{id}", "Range", ref range, 0.1f, 0.01f, 900f, "%.3f"))
            {
                lightShape = lightShape with { Range = range };
                model = model with { Shape = lightShape };
                changed = true;
                onChanged?.Invoke("LightRange", "Change Light Range", model, false);
            }

            var falloff = lightShape.Falloff;
            if (DrawDragFloatRow($"Falloff_{id}", "Falloff", ref falloff, 0.01f, 0f, 1000f, "%.3f"))
            {
                lightShape = lightShape with { Falloff = falloff };
                model = model with { Shape = lightShape };
                changed = true;
                onChanged?.Invoke("LightFalloff", "Change Light Falloff", model, false);
            }

            if (lightType == LightType.SpotLight)
            {
                var lightAngle = lightShape.LightAngle;
                if (DrawSliderFloatRow($"LightAngle_{id}", "Light Angle", ref lightAngle, 0f, 180f, "%.0f Degrees"))
                {
                    lightShape = lightShape with { LightAngle = lightAngle };
                    model = model with { Shape = lightShape };
                    changed = true;
                    onChanged?.Invoke("LightAngle", "Change Light Angle", model, false);
                }
            }

            if (lightType == LightType.SpotLight || lightType == LightType.FlatLight)
            {
                var falloffAngle = lightShape.FalloffAngle;
                if (DrawSliderFloatRow($"FalloffAngle_{id}", "Falloff Angle", ref falloffAngle, 0f, 180f, "%.0f Degrees"))
                {
                    lightShape = lightShape with { FalloffAngle = falloffAngle };
                    model = model with { Shape = lightShape };
                    changed = true;
                    onChanged?.Invoke("LightFalloffAngle", "Change Light Falloff Angle", model, false);
                }
            }

            if (lightType == LightType.FlatLight)
            {
                var flatAngles = lightShape.AngleDegrees;
                DrawCompactSettingsLabelCell("Flat Angle");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.SliderFloat2($"##FlatAngle_{id}", ref flatAngles, -90f, 90f, "%.0f Degrees"))
                {
                    lightShape = lightShape with { AngleDegrees = flatAngles };
                    model = model with { Shape = lightShape };
                    changed = true;
                    onChanged?.Invoke("LightFlatAngle", "Change Light Flat Angle", model, false);
                }
            }

            var shadowRange = lightShadow.CharacterShadowRange;
            if (DrawDragFloatRow($"ShadowRange_{id}", "Character Shadow Range", ref shadowRange, 0.25f, 0f, 1000f, "%.3f"))
            {
                lightShadow = lightShadow with { CharacterShadowRange = shadowRange };
                model = model with { Shadow = lightShadow };
                changed = true;
                onChanged?.Invoke("LightShadowRange", "Change Light Character Shadow Range", model, false);
            }

            var shadowNear = lightShadow.ShadowPlaneNear;
            if (DrawDragFloatRow($"ShadowNear_{id}", "Shadow Near", ref shadowNear, 0.001f, 0.001f, 100f, "%.3f"))
            {
                lightShadow = lightShadow with { ShadowPlaneNear = shadowNear };
                model = model with { Shadow = lightShadow };
                changed = true;
                onChanged?.Invoke("LightShadowNear", "Change Light Shadow Near", model, false);
            }

            var shadowFar = lightShadow.ShadowPlaneFar;
            if (DrawDragFloatRow($"ShadowFar_{id}", "Shadow Far", ref shadowFar, 0.05f, 0.01f, 1000f, "%.3f"))
            {
                lightShadow = lightShadow with { ShadowPlaneFar = shadowFar };
                model = model with { Shadow = lightShadow };
                changed = true;
                onChanged?.Invoke("LightShadowFar", "Change Light Shadow Far", model, false);
            }

        }

        return changed;
    }

    private static string DrawLightTypeLabel(LightType lightType)
        => lightType.ToString();

    private static string DrawOutlineColorLabel(ObjectOutlineColor outlineColor)
        => outlineColor.ToString();

    private static string DrawFalloffTypeLabel(LightFalloffType falloffType)
        => falloffType.ToString();

    private static bool IsApproximatelyDefault(Vector4 value, Vector4 expected, float epsilon = 0.001f)
        => ObjectMathUtility.IsNearlyEqual(value, expected, epsilon);

    private static bool IsApproximatelyDefault(Vector3 value, Vector3 expected, float epsilon = 0.001f)
        => ObjectMathUtility.IsNearlyEqual(value, expected, epsilon);

    private static string FormatColorLabel(Vector4 color)
        => $"{color.X:0.##}, {color.Y:0.##}, {color.Z:0.##}";

    private static string FormatColorLabel(Vector3 color)
        => $"{color.X:0.##}, {color.Y:0.##}, {color.Z:0.##}";

    private static string FormatByteColorLabel(Vector4 color)
        => $"{ObjectColorUtility.ToRoundedByteComponent(color.X)}, {ObjectColorUtility.ToRoundedByteComponent(color.Y)}, {ObjectColorUtility.ToRoundedByteComponent(color.Z)}";

    private static bool MatchesObjectFilter(
        ObjectSnapshot snapshot,
        bool isActive,
        string filter,
        ObjectKind? kindFilter)
    {
        if (!MatchesObjectKindFilter(snapshot, kindFilter))
        {
            return false;
        }

        return MatchesObjectSearchFilter(snapshot, isActive, filter);
    }

    private static bool MatchesObjectKindFilter(ObjectSnapshot snapshot, ObjectKind? kindFilter)
        => !kindFilter.HasValue || snapshot.Kind == kindFilter.Value;

    private static bool MatchesObjectSearchFilter(ObjectSnapshot snapshot, bool isActive, string filter)
        => string.IsNullOrWhiteSpace(filter)
            || snapshot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || snapshot.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || BuildObjectListDetail(snapshot, isActive).Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string BuildObjectListDetail(ObjectSnapshot snapshot, bool isActive)
        => string.Join(
            " | ",
            BuildObjectListMetaSegments(snapshot, isActive)
                .Append(ObjectSnapshotUtility.GetAssetName(snapshot)));

    private static string BuildObjectListMeta(ObjectSnapshot snapshot, bool isActive)
        => string.Join(" | ", BuildObjectListMetaSegments(snapshot, isActive));

    private static IEnumerable<string> BuildObjectListMetaSegments(ObjectSnapshot snapshot, bool isActive)
    {
        yield return isActive ? "active" : "inactive";
        yield return snapshot.Visible ? "visible" : "hidden";

        if (snapshot.Locked)
        {
            yield return "locked";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FolderPath))
        {
            yield return $"folder {snapshot.FolderPath}";
        }
    }

    private static string BuildPlacedObjectCountLabel(int objectCount, int activeCount, int lockedCount)
    {
        var placedLabel = objectCount == 1 ? "1 placed" : $"{objectCount} placed";
        var activeLabel = activeCount == 1 ? "1 active" : $"{activeCount} active";
        var lockedLabel = lockedCount == 1 ? "1 locked" : $"{lockedCount} locked";
        return $"{placedLabel} | {activeLabel} | {lockedLabel}";
    }

    private static Vector4 ResolveFolderAccentColor(string colorValue)
    {
        return ObjectFolderUtility.TryParseFolderColorValue(colorValue, out var color)
            ? color
            : ResolveDefaultFolderAccentColor();
    }

    private static bool DrawFolderColorSwatch(string id, string colorValue, bool selected)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(18f * scale, 12f * scale);
        var clicked = ImGui.InvisibleButton(id, size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var fill = string.IsNullOrEmpty(colorValue)
            ? EditorColors.ButtonDefault with { W = 0.96f }
            : ResolveFolderAccentColor(colorValue) with { W = 0.96f };
        var border = selected
            ? EditorColors.Text
            : EditorColors.Border with { W = 0.78f };
        var rounding = 3f * scale;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, selected ? 2f * scale : 1f * scale);
        if (string.IsNullOrEmpty(colorValue))
        {
            drawList.AddLine(
                new Vector2(min.X + (3f * scale), max.Y - (3f * scale)),
                new Vector2(max.X - (3f * scale), min.Y + (3f * scale)),
                ImGui.GetColorU32(ResolveDefaultFolderAccentColor()),
                1.25f * scale);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ResolveFolderColorTooltip(colorValue));
        }

        return clicked;
    }

    private static Vector4 ResolveDefaultFolderAccentColor()
        => ObjectFolderUtility.TryParseFolderColorValue(EditorColors.FolderPurple, out var color)
            ? color
            : EditorColors.Color(0.6784f, 0.5412f, 0.9608f, 1f);

    private static string ResolveFolderColorTooltip(string colorValue)
    {
        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        return string.IsNullOrEmpty(sanitizedColorValue)
            ? "Default color"
            : $"Color {sanitizedColorValue}";
    }

    private static string ResolveFolderDisplayLabel(string folderPath)
        => string.IsNullOrWhiteSpace(folderPath)
            ? "Ungrouped"
            : folderPath;
}
