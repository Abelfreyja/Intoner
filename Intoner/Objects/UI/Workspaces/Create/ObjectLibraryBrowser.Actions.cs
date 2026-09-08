using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Intoner.Objects.Library;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;

namespace Intoner.Objects.UI;

internal sealed partial class ObjectLibraryBrowser
{
    private void DrawLibraryEntryContextMenu(ObjectLibraryEntry entry)
    {
        IReadOnlyList<Guid> targetIds = _browserSelection.LibrarySelection.Contains(entry.Id)
            ? _browserSelection.LibrarySelection.SelectedItemIds
            : [entry.Id];
        bool singleEntry = targetIds.Count == 1;
        bool containsPrefabMember = ObjectLibraryQuery.ContainsPrefabEntries(
            _objectLibrary.Current,
            targetIds.ToHashSet());
        EditorContextMenu.DrawFirstSectionLabel(
            singleEntry ? entry.Name : $"{targetIds.Count} presets selected");

        if (singleEntry
         && EditorContextMenu.DrawItem(
                FontAwesomeIcon.Play,
                ObjectEditorCatalog.ResolvePlacementActionLabel(entry.Preset.Kind),
                enabled: _createActions.CanPlaceObjectPreset(entry.Preset)))
        {
            _ = _createActions.TryPlaceObjectPreset(entry.Preset);
        }

        if (singleEntry)
        {
            DrawCopyAssetPathAction(entry.Preset);
        }

        EditorContextMenu.DrawSeparator();
        DrawLibraryFolderAssignmentMenu(targetIds, !containsPrefabMember);

        if (singleEntry && EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Preset"))
        {
            OpenRenameLibraryEntryDialog(entry);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                singleEntry ? "Remove from Library" : $"Remove {targetIds.Count} from Library",
                enabled: !containsPrefabMember,
                color: ThemeColors.DimRed,
                tooltip: containsPrefabMember ? "Dissolve the prefab before removing its members." : null)
         && _objectLibrary.TryRemoveEntries(targetIds))
        {
            _ = _browserSelection.LibrarySelection.TryClear();
        }
    }

    private void DrawLibraryGroupContextMenu(LibraryBrowserView.Group group)
    {
        ObjectLibraryGroup libraryGroup = group.Source;
        ObjectLibraryPrefab? prefab = libraryGroup as ObjectLibraryPrefab;
        string groupType = prefab is null ? "Folder" : "Prefab";
        EditorContextMenu.DrawFirstSectionLabel(libraryGroup.Name);
        if (prefab is not null)
        {
            bool canPlacePrefab = CanPlaceLibraryPrefab(group);
            using EditorContextMenu.SubMenuScope placeMenu = EditorContextMenu.BeginSubMenu(
                $"objectLibraryPlacePrefab:{libraryGroup.Id}",
                FontAwesomeIcon.Play,
                "Place Prefab",
                enabled: canPlacePrefab);
            if (placeMenu)
            {
                if (EditorContextMenu.DrawItem(FontAwesomeIcon.User, "Place at Your Position"))
                {
                    _ = TryPlaceLibraryPrefab(group, atPlayer: true);
                }

                if (EditorContextMenu.DrawItem(
                        FontAwesomeIcon.Globe,
                        "Place at World Position",
                        enabled: canPlacePrefab && CanRestoreLibraryPrefabAtWorldPosition(group)))
                {
                    _ = TryPlaceLibraryPrefab(group, atPlayer: false);
                }
            }
        }
        else
        {
            _ = EditorContextMenu.DrawItem(
                FontAwesomeIcon.Play,
                "Place Prefab",
                enabled: false,
                tooltip: "Save a folder from Placed + Edit to create a prefab.");
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Copy, $"Copy {groupType}"))
        {
            _ = _objectClipboard.CopyLibraryGroup(libraryGroup);
        }

        EditorContextMenu.DrawSeparator();

        DrawLibraryGroupFolderMenu(libraryGroup);
        if (prefab is null
         && EditorContextMenu.DrawItem(FontAwesomeIcon.FolderPlus, "New Folder Here..."))
        {
            OpenCreateLibraryFolderDialog(parentFolderId: libraryGroup.Id);
        }

        EditorContextMenu.DrawSeparator();

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Check,
                "Select All Children",
                enabled: group.AllEntries.Count > 0))
        {
            _ = _browserSelection.LibrarySelection.TryReplaceSelection(group.AllEntries.Select(static entry => entry.Id));
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, $"Rename {groupType}"))
        {
            OpenRenameLibraryGroupDialog(libraryGroup);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Unlink,
                $"Dissolve {groupType}",
                color: ThemeColors.DimRed))
        {
            bool dissolved = prefab is null
                ? _objectLibrary.TryDissolveFolder(libraryGroup.Id)
                : _objectLibrary.TryDissolvePrefab(libraryGroup.Id);
            if (dissolved)
            {
                _collapsedLibraryGroups.Remove(libraryGroup.Id);
            }
        }

        EditorContextMenu.DrawSectionLabel($"{groupType} Color");
        if (FolderColorPicker.Draw($"objectLibraryGroupColor:{libraryGroup.Id}", libraryGroup.Color) is { } color
         && (prefab is null
             ? _objectLibrary.TrySetFolderColor(libraryGroup.Id, color)
             : _objectLibrary.TrySetPrefabColor(libraryGroup.Id, color)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawLibraryGroupFolderMenu(ObjectLibraryGroup group)
    {
        using EditorContextMenu.SubMenuScope folderMenu = EditorContextMenu.BeginSubMenu(
            $"objectLibraryMoveGroup:{group.Id}",
            FontAwesomeIcon.FolderOpen,
            "Move to Folder");
        if (!folderMenu)
        {
            return;
        }

        ObjectLibrarySnapshot library = _objectLibrary.Current;
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);
        IReadOnlyList<ObjectLibraryFolder> folders = OrderLibraryFolders(library.Folders)
            .Where(folder => folder.Id != group.Id
                && (group is not ObjectLibraryFolder || !hierarchy.IsDescendantOf(folder.Id, group.Id)))
            .ToArray();
        FolderSelectionMenu.Draw(
            folders,
            !group.ParentFolderId.HasValue,
            folder => group.ParentFolderId == folder.Id,
            static folder => folder.Id.ToString(),
            static folder => folder.ParentFolderId?.ToString() ?? string.Empty,
            static folder => folder.Name,
            () => _ = _objectLibrary.TryMoveGroup(group.Id, null),
            folder => _ = _objectLibrary.TryMoveGroup(group.Id, folder.Id));
    }

    private void DrawCopyAssetPathAction(ObjectLibraryPreset preset)
    {
        string assetPath = ObjectSnapshotUtility.GetRootResourcePath(preset.Model);
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Copy,
                "Copy Asset Path",
                enabled: assetPath.Length > 0))
        {
            _clipboardText.WriteText(assetPath);
        }
    }

    private void DrawLibraryFolderAssignmentMenu(IReadOnlyList<Guid> entryIds, bool enabled)
    {
        using EditorContextMenu.SubMenuScope folderMenu = EditorContextMenu.BeginSubMenu(
            "objectLibraryAssignFolder",
            FontAwesomeIcon.FolderOpen,
            "Assign Folder",
            enabled: enabled,
            tooltip: enabled ? null : "Dissolve the prefab before moving its members.");
        if (!folderMenu)
        {
            return;
        }

        ObjectLibrarySnapshot library = _objectLibrary.Current;
        bool hasCommonFolder = ObjectLibraryQuery.TryResolveCommonFolder(library, entryIds, out Guid? commonFolderId);
        IReadOnlyList<ObjectLibraryFolder> folders = OrderLibraryFolders(library.Folders);
        FolderSelectionMenu.Draw(
            folders,
            hasCommonFolder && !commonFolderId.HasValue,
            folder => hasCommonFolder && commonFolderId == folder.Id,
            static folder => folder.Id.ToString(),
            static folder => folder.ParentFolderId?.ToString() ?? string.Empty,
            static folder => folder.Name,
            () => _ = _objectLibrary.TryMoveEntries(entryIds, null),
            folder => _ = _objectLibrary.TryMoveEntries(entryIds, folder.Id));

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.FolderPlus, "New Folder..."))
        {
            Guid[] pendingIds = entryIds.ToArray();
            OpenCreateLibraryFolderDialog(folderName =>
                _objectLibrary.TryMoveEntriesToNewFolder(pendingIds, folderName, null, out _));
        }
    }

    private void OpenCreateLibraryFolderDialog(
        Func<string, bool>? create = null,
        Guid? parentFolderId = null)
    {
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "object-library-folder-create",
            "Create Library Folder",
            "Create Folder",
            input => create?.Invoke(input) ?? _objectLibrary.TryCreateFolder(input, parentFolderId, out _)) with
        {
            Icon = FontAwesomeIcon.FolderPlus,
            Accent = ThemeColors.AccentPrimary,
            Placeholder = "folder name",
            Validate = input => ValidateLibraryFolderName(input, parentFolderId),
            FailureMessage = "The Library folder could not be created.",
        });
    }

    private void OpenRenameLibraryGroupDialog(ObjectLibraryGroup group)
    {
        bool isPrefab = group is ObjectLibraryPrefab;
        string groupType = isPrefab ? "Prefab" : "Folder";
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "object-library-group-rename",
            $"Rename Library {groupType}",
            $"Rename {groupType}",
            input => isPrefab
                ? _objectLibrary.TryRenamePrefab(group.Id, input)
                : _objectLibrary.TryRenameFolder(group.Id, input)) with
        {
            Icon = FontAwesomeIcon.Edit,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = group.Name,
            Placeholder = $"{groupType.ToLowerInvariant()} name",
            Detail = $"Current: {group.Name}",
            Validate = input => ValidateLibraryGroupName(input, group.Id, groupType),
            FailureMessage = $"The Library {groupType.ToLowerInvariant()} could not be renamed.",
        });
    }

    private void OpenRenameLibraryEntryDialog(ObjectLibraryEntry entry)
    {
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "object-library-entry-rename",
            "Rename Preset",
            "Rename",
            input => _objectLibrary.TryRenameEntry(entry.Id, input)) with
        {
            Icon = FontAwesomeIcon.Edit,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = entry.Name,
            Placeholder = "preset name",
            Detail = $"Current: {entry.Name}",
            Validate = input => ValidateLibraryEntryName(input, entry.Name),
            FailureMessage = "The preset could not be renamed.",
        });
    }

    private static string? ValidateLibraryEntryName(string input, string currentName)
    {
        string name = TextUtility.TrimOrEmpty(input);
        if (name.Length == 0)
        {
            return "Enter a preset name.";
        }

        if (name.Length > ObjectLibraryRules.MaximumNameLength)
        {
            return $"Preset names can be up to {ObjectLibraryRules.MaximumNameLength} characters.";
        }

        return string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)
            ? "Choose a different preset name."
            : null;
    }

    private string? ValidateLibraryFolderName(string input, Guid? parentFolderId = null)
        => ValidateLibraryGroupName(input, null, "Folder", parentFolderId);

    private string? ValidateLibraryGroupName(
        string input,
        Guid? currentGroupId,
        string groupType,
        Guid? parentFolderId = null)
    {
        string name = TextUtility.TrimOrEmpty(input);
        if (name.Length == 0)
        {
            return $"Enter a {groupType.ToLowerInvariant()} name.";
        }

        if (name.Length > ObjectLibraryRules.MaximumNameLength)
        {
            return $"{groupType} names can be up to {ObjectLibraryRules.MaximumNameLength} characters.";
        }

        ObjectLibrarySnapshot library = _objectLibrary.Current;
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);
        ObjectLibraryGroup? currentGroup = null;
        if (currentGroupId is { } groupId)
        {
            _ = hierarchy.TryGetGroup(groupId, out currentGroup!);
        }

        if (hierarchy.ContainsSiblingName(
                currentGroup?.ParentFolderId ?? parentFolderId,
                name,
                currentGroupId))
        {
            return "A Library folder or prefab already uses this name.";
        }

        return currentGroup is not null
            && string.Equals(currentGroup.Name, name, StringComparison.OrdinalIgnoreCase)
                ? $"Choose a different {groupType.ToLowerInvariant()} name."
                : null;
    }

    private static IReadOnlyList<ObjectLibraryFolder> OrderLibraryFolders(IReadOnlyList<ObjectLibraryFolder> folders)
        => folders.OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private void DrawOpenLibraryAction(string id, IReadOnlyList<ObjectLibraryEntry> entries)
    {
        if (entries.Count == 1)
        {
            if (EditorContextMenu.DrawItem(FontAwesomeIcon.LayerGroup, "Open in Library"))
            {
                OpenLibraryEntry(entries[0]);
            }

            return;
        }

        using EditorContextMenu.SubMenuScope entriesMenu = EditorContextMenu.BeginSubMenu(
            id,
            FontAwesomeIcon.LayerGroup,
            "Open in Library");
        if (entriesMenu)
        {
            DrawLibraryEntryChoices(id, entries);
        }
    }

    internal void DrawLibraryEntryChoices(string idPrefix, IReadOnlyList<ObjectLibraryEntry> entries)
    {
        IReadOnlyList<ObjectLibraryFolder> folders = OrderLibraryFolders(_objectLibrary.Current.Folders);
        Dictionary<Guid, List<ObjectLibraryEntry>> entriesByFolder = [];
        List<ObjectLibraryEntry> ungroupedEntries = [];
        foreach (ObjectLibraryEntry entry in entries)
        {
            if (!entry.FolderId.HasValue)
            {
                ungroupedEntries.Add(entry);
                continue;
            }

            Guid folderId = entry.FolderId.Value;
            if (!entriesByFolder.TryGetValue(folderId, out List<ObjectLibraryEntry>? folderEntries))
            {
                folderEntries = [];
                entriesByFolder.Add(folderId, folderEntries);
            }

            folderEntries.Add(entry);
        }

        DrawLibraryLocationChoice(
            idPrefix,
            "ungrouped",
            FontAwesomeIcon.TimesCircle,
            "Ungrouped",
            ungroupedEntries);
        foreach (ObjectLibraryFolder folder in folders)
        {
            if (entriesByFolder.TryGetValue(folder.Id, out List<ObjectLibraryEntry>? folderEntries))
            {
                DrawLibraryLocationChoice(
                    idPrefix,
                    folder.Id.ToString(),
                    FontAwesomeIcon.Folder,
                    folder.Name,
                    folderEntries);
            }
        }
    }

    private void DrawLibraryLocationChoice(
        string idPrefix,
        string locationId,
        FontAwesomeIcon icon,
        string label,
        IReadOnlyList<ObjectLibraryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        string id = $"{idPrefix}:location:{locationId}";
        if (entries.Count == 1)
        {
            if (EditorContextMenu.DrawItem(icon, label, id: id))
            {
                OpenLibraryEntry(entries[0]);
            }

            return;
        }

        using EditorContextMenu.SubMenuScope locationMenu = EditorContextMenu.BeginSubMenu(id, icon, label);
        if (locationMenu)
        {
            DrawLibraryPresetChoices(id, entries);
        }
    }

    private void DrawLibraryPresetChoices(string idPrefix, IReadOnlyList<ObjectLibraryEntry> entries)
    {
        Dictionary<string, int> totals = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entries.Select(static entry => entry.Name))
        {
            totals.TryGetValue(name, out int total);
            totals[name] = total + 1;
        }

        Dictionary<string, int> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectLibraryEntry entry in entries)
        {
            positions.TryGetValue(entry.Name, out int position);
            positions[entry.Name] = ++position;
            string label = totals[entry.Name] > 1
                ? $"{entry.Name} #{position}"
                : entry.Name;
            if (EditorContextMenu.DrawItem(
                    SceneItemPresentation.ResolveObjectKindIcon(entry.Preset.Kind),
                    label,
                    id: $"{idPrefix}:{entry.Id}"))
            {
                OpenLibraryEntry(entry);
            }
        }
    }

    internal void OpenLibraryEntry(ObjectLibraryEntry entry)
    {
        ObjectLibrarySnapshot library = _objectLibrary.Current;
        ObjectLibraryEntry? currentEntry = ObjectLibraryQuery.FindEntry(library, entry.Id);
        if (currentEntry is null || !_createActions.CanUseLibraryPreset(currentEntry.Preset))
        {
            return;
        }

        _libraryFilter = string.Empty;
        _libraryKindFilter = string.Empty;
        Guid? groupId = currentEntry.FolderId
            ?? library.Prefabs.FirstOrDefault(prefab => prefab.EntryIds.Contains(currentEntry.Id))?.Id;
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);
        bool expanded = false;
        while (groupId is { } currentGroupId && hierarchy.TryGetGroup(currentGroupId, out ObjectLibraryGroup group))
        {
            expanded |= _collapsedLibraryGroups.Remove(currentGroupId);
            groupId = group.ParentFolderId;
        }

        if (expanded)
        {
            ++_libraryGroupStateRevision;
        }

        _draft.ApplyLibraryPreset(currentEntry.Preset);
        _ = _browserSelection.LibrarySelection.TryReplaceSelection([currentEntry.Id]);
        _browserSelection.Source = CreateBrowserSelection.CreateBrowserSource.Library;
    }
}
