using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Library;

/// <summary> owns reusable object presets, folders, and prefabs </summary>
internal interface IObjectLibrary
{
    /// <summary> gets the current immutable library snapshot </summary>
    ObjectLibrarySnapshot Current { get; }

    /// <summary> adds a preset to the library </summary>
    bool TryAdd(string name, ObjectLibraryPreset preset, Guid? folderId, out ObjectLibraryEntry entry);

    /// <summary> adds a preset and its new folder in one change </summary>
    bool TryAddToNewFolder(
        string entryName,
        ObjectLibraryPreset preset,
        string folderName,
        Guid? parentFolderId,
        out ObjectLibraryEntry entry,
        out ObjectLibraryFolder folder);

    /// <summary> renames a library entry </summary>
    bool TryRenameEntry(Guid entryId, string name);

    /// <summary> creates an organizational folder </summary>
    bool TryCreateFolder(string name, Guid? parentFolderId, out ObjectLibraryFolder folder);

    /// <summary> captures placed objects as a reusable prefab </summary>
    bool TryCreatePrefab(
        string name,
        string color,
        SceneCreationContext capturedIn,
        IReadOnlyList<ObjectSnapshot> snapshots,
        Guid? parentFolderId,
        out ObjectLibraryPrefab prefab);

    /// <summary> imports a complete folder or prefab hierarchy in one library change </summary>
    bool TryImport(ObjectLibraryImport import, Guid? parentFolderId, out ObjectLibraryGroup group);

    /// <summary> renames an organizational folder </summary>
    bool TryRenameFolder(Guid folderId, string name);

    /// <summary> renames a prefab </summary>
    bool TryRenamePrefab(Guid prefabId, string name);

    /// <summary> promotes a folder's direct contents to its parent and removes the folder </summary>
    bool TryDissolveFolder(Guid folderId);

    /// <summary> converts a prefab's members to ordinary entries in its parent and removes the prefab </summary>
    bool TryDissolvePrefab(Guid prefabId);

    /// <summary> changes an organizational folder color </summary>
    bool TrySetFolderColor(Guid folderId, string color);

    /// <summary> changes a prefab color </summary>
    bool TrySetPrefabColor(Guid prefabId, string color);

    /// <summary> moves a folder or prefab into another folder or the Library root </summary>
    bool TryMoveGroup(Guid groupId, Guid? parentFolderId);

    /// <summary> moves entries into a folder or the library root </summary>
    bool TryMoveEntries(IEnumerable<Guid> entryIds, Guid? folderId);

    /// <summary> moves entries into a new folder in one change </summary>
    bool TryMoveEntriesToNewFolder(
        IEnumerable<Guid> entryIds,
        string folderName,
        Guid? parentFolderId,
        out ObjectLibraryFolder folder);

    /// <summary> removes entries from the library </summary>
    bool TryRemoveEntries(IEnumerable<Guid> entryIds);
}

internal sealed class ObjectLibrary : IObjectLibrary, IDisposable
{
    private readonly IObjectLibraryStore _store;
    private readonly Lock _stateLock = new();
    private ObjectLibrarySnapshot _current;
    private bool _disposed;

    public ObjectLibrary(IObjectLibraryStore store)
    {
        _store = store;
        _current = ObjectLibrarySnapshot.Empty;
        _store.FilesChanged += HandleFilesChanged;
        try
        {
            lock (_stateLock)
            {
                _current = ObjectLibrarySnapshot.Create(0, store.Load());
            }
        }
        catch
        {
            _store.FilesChanged -= HandleFilesChanged;
            throw;
        }
    }

    public ObjectLibrarySnapshot Current
        => Volatile.Read(ref _current);

    public bool TryAdd(string name, ObjectLibraryPreset preset, Guid? folderId, out ObjectLibraryEntry entry)
    {
        entry = default!;
        string normalizedName = ObjectLibraryRules.NormalizeName(name);
        if (normalizedName.Length == 0
         || !ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalizedPreset))
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (!ObjectLibraryRules.CanReceiveEntries(current.Folders, folderId))
            {
                return false;
            }

            ObjectLibraryEntry candidate = CreateEntry(
                normalizedName,
                normalizedPreset,
                folderId,
                DateTimeOffset.UtcNow);
            if (!TryPublish([.. current.Entries, candidate], current.Folders, current.Prefabs))
            {
                return false;
            }

            entry = candidate;
            return true;
        }
    }

    public bool TryAddToNewFolder(
        string entryName,
        ObjectLibraryPreset preset,
        string folderName,
        Guid? parentFolderId,
        out ObjectLibraryEntry entry,
        out ObjectLibraryFolder folder)
    {
        entry = default!;
        folder = default!;
        string normalizedEntryName = ObjectLibraryRules.NormalizeName(entryName);
        string normalizedFolderName = ObjectLibraryRules.NormalizeName(folderName);
        if (normalizedEntryName.Length == 0
         || normalizedFolderName.Length == 0
         || !ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalizedPreset))
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            if (!hierarchy.ContainsFolder(parentFolderId)
             || hierarchy.ContainsSiblingName(parentFolderId, normalizedFolderName))
            {
                return false;
            }

            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            ObjectLibraryFolder folderCandidate = CreateFolder(normalizedFolderName, parentFolderId, createdAt);
            ObjectLibraryEntry entryCandidate = CreateEntry(
                normalizedEntryName,
                normalizedPreset,
                folderCandidate.Id,
                createdAt);
            if (!TryPublish(
                    [.. current.Entries, entryCandidate],
                    [.. current.Folders, folderCandidate],
                    current.Prefabs))
            {
                return false;
            }

            entry = entryCandidate;
            folder = folderCandidate;
            return true;
        }
    }

    public bool TryRenameEntry(Guid entryId, string name)
    {
        string normalizedName = ObjectLibraryRules.NormalizeName(name);
        if (normalizedName.Length == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            int entryIndex = IndexOfEntry(current.Entries, entryId);
            if (entryIndex < 0 || ObjectLibraryRules.NamesMatch(current.Entries[entryIndex].Name, normalizedName))
            {
                return false;
            }

            ObjectLibraryEntry[] entries = current.Entries.ToArray();
            entries[entryIndex] = entries[entryIndex] with { Name = normalizedName };
            return TryPublish(entries, current.Folders, current.Prefabs);
        }
    }

    public bool TryCreateFolder(string name, Guid? parentFolderId, out ObjectLibraryFolder folder)
    {
        folder = default!;
        string normalizedName = ObjectLibraryRules.NormalizeName(name);
        if (normalizedName.Length == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            if (!hierarchy.ContainsFolder(parentFolderId)
             || hierarchy.ContainsSiblingName(parentFolderId, normalizedName))
            {
                return false;
            }

            ObjectLibraryFolder candidate = CreateFolder(normalizedName, parentFolderId, DateTimeOffset.UtcNow);
            if (!TryPublish(current.Entries, [.. current.Folders, candidate], current.Prefabs))
            {
                return false;
            }

            folder = candidate;
            return true;
        }
    }

    public bool TryCreatePrefab(
        string name,
        string color,
        SceneCreationContext capturedIn,
        IReadOnlyList<ObjectSnapshot> snapshots,
        Guid? parentFolderId,
        out ObjectLibraryPrefab prefab)
    {
        ArgumentNullException.ThrowIfNull(capturedIn);
        ArgumentNullException.ThrowIfNull(snapshots);

        prefab = default!;
        if (!ObjectLibraryPrefabFactory.TryCreate(
                name,
                color,
                capturedIn,
                snapshots,
                out ObjectLibraryPrefab candidate,
                out ObjectLibraryEntry[] entries))
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            if (!hierarchy.ContainsFolder(parentFolderId)
             || hierarchy.ContainsSiblingName(parentFolderId, candidate.Name))
            {
                return false;
            }

            candidate = candidate with { ParentFolderId = parentFolderId };

            if (!TryPublish(
                    [.. current.Entries, .. entries],
                    current.Folders,
                    [.. current.Prefabs, candidate]))
            {
                return false;
            }

            prefab = candidate;
            return true;
        }
    }

    public bool TryImport(ObjectLibraryImport import, Guid? parentFolderId, out ObjectLibraryGroup group)
    {
        ArgumentNullException.ThrowIfNull(import);
        group = null!;
        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (!TryBuildImportedContent(
                    current,
                    import,
                    parentFolderId,
                    out ObjectLibraryContent content,
                    out ObjectLibraryGroup candidate)
                || !TryPublish(content.Entries, content.Folders, content.Prefabs))
            {
                return false;
            }

            group = candidate;
            return true;
        }
    }

    public bool TryRenameFolder(Guid folderId, string name)
        => TryRenameGroup(folderId, name, isPrefab: false);

    public bool TryRenamePrefab(Guid prefabId, string name)
        => TryRenameGroup(prefabId, name, isPrefab: true);

    public bool TryDissolveFolder(Guid folderId)
    {
        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryFolder? folder = current.Folders.FirstOrDefault(candidate => candidate.Id == folderId);
            if (folder is null)
            {
                return false;
            }

            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            HashSet<string> siblingNames = hierarchy.GetChildren(folder.ParentFolderId)
                .Where(group => group.Id != folderId)
                .Select(static group => group.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<Guid, string> promotedNames = [];
            foreach (ObjectLibraryGroup child in hierarchy.GetChildren(folderId))
            {
                string name = ObjectLibraryRules.ResolveAvailableGroupName(child.Name, siblingNames);
                if (name.Length == 0)
                {
                    return false;
                }

                promotedNames.Add(child.Id, name);
                _ = siblingNames.Add(name);
            }

            ObjectLibraryEntry[] entries = current.Entries
                .Select(entry => entry.FolderId == folderId
                    ? ObjectLibraryRules.MoveEntry(entry, folder.ParentFolderId)
                    : entry)
                .ToArray();
            ObjectLibraryFolder[] folders = current.Folders
                .Where(folder => folder.Id != folderId)
                .Select(candidate => candidate.ParentFolderId == folderId
                    ? candidate with
                    {
                        Name = promotedNames[candidate.Id],
                        ParentFolderId = folder.ParentFolderId,
                    }
                    : candidate)
                .ToArray();
            ObjectLibraryPrefab[] prefabs = current.Prefabs
                .Select(prefab => prefab.ParentFolderId == folderId
                    ? prefab with
                    {
                        Name = promotedNames[prefab.Id],
                        ParentFolderId = folder.ParentFolderId,
                    }
                    : prefab)
                .ToArray();
            return TryPublish(entries, folders, prefabs);
        }
    }

    public bool TryDissolvePrefab(Guid prefabId)
    {
        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryPrefab? prefab = ObjectLibraryQuery.FindPrefab(current, prefabId);
            if (prefab is null)
            {
                return false;
            }

            HashSet<Guid> memberIds = prefab.EntryIds.ToHashSet();
            ObjectLibraryEntry[] entries = current.Entries
                .Select(entry => memberIds.Contains(entry.Id)
                    ? ObjectLibraryRules.MoveEntry(entry, prefab.ParentFolderId)
                    : entry)
                .ToArray();
            ObjectLibraryPrefab[] prefabs = current.Prefabs.Where(candidate => candidate.Id != prefabId).ToArray();
            return TryPublish(entries, current.Folders, prefabs);
        }
    }

    public bool TrySetFolderColor(Guid folderId, string color)
        => TrySetGroupColor(folderId, color, isPrefab: false);

    public bool TrySetPrefabColor(Guid prefabId, string color)
        => TrySetGroupColor(prefabId, color, isPrefab: true);

    public bool TryMoveGroup(Guid groupId, Guid? parentFolderId)
    {
        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            if (!hierarchy.TryGetGroup(groupId, out ObjectLibraryGroup group)
             || group.ParentFolderId == parentFolderId
             || !hierarchy.ContainsFolder(parentFolderId)
             || parentFolderId == groupId
             || parentFolderId is { } parentId && hierarchy.IsDescendantOf(parentId, groupId)
             || hierarchy.ContainsSiblingName(parentFolderId, group.Name, groupId))
            {
                return false;
            }

            if (group is ObjectLibraryFolder)
            {
                ObjectLibraryFolder[] folders = current.Folders
                    .Select(folder => folder.Id == groupId ? folder with { ParentFolderId = parentFolderId } : folder)
                    .ToArray();
                return TryPublish(current.Entries, folders, current.Prefabs);
            }

            ObjectLibraryPrefab[] prefabs = current.Prefabs
                .Select(prefab => prefab.Id == groupId ? prefab with { ParentFolderId = parentFolderId } : prefab)
                .ToArray();
            return TryPublish(current.Entries, current.Folders, prefabs);
        }
    }

    public bool TryMoveEntries(IEnumerable<Guid> entryIds, Guid? folderId)
    {
        HashSet<Guid> ids = entryIds.ToHashSet();
        if (ids.Count == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (!ObjectLibraryRules.CanReceiveEntries(current.Folders, folderId)
             || !ObjectLibraryQuery.ContainsAllEntries(current, ids)
             || ObjectLibraryQuery.ContainsPrefabEntries(current, ids))
            {
                return false;
            }

            bool changed = false;
            ObjectLibraryEntry[] entries = current.Entries.ToArray();
            for (int index = 0; index < entries.Length; ++index)
            {
                ObjectLibraryEntry currentEntry = entries[index];
                if (!ids.Contains(currentEntry.Id) || currentEntry.FolderId == folderId)
                {
                    continue;
                }

                entries[index] = ObjectLibraryRules.MoveEntry(currentEntry, folderId);
                changed = true;
            }

            return changed && TryPublish(entries, current.Folders, current.Prefabs);
        }
    }

    public bool TryMoveEntriesToNewFolder(
        IEnumerable<Guid> entryIds,
        string folderName,
        Guid? parentFolderId,
        out ObjectLibraryFolder folder)
    {
        folder = default!;
        HashSet<Guid> ids = entryIds.ToHashSet();
        string normalizedFolderName = ObjectLibraryRules.NormalizeName(folderName);
        if (ids.Count == 0 || normalizedFolderName.Length == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(current);
            if (!hierarchy.ContainsFolder(parentFolderId)
             || hierarchy.ContainsSiblingName(parentFolderId, normalizedFolderName)
             || !ObjectLibraryQuery.ContainsAllEntries(current, ids)
             || ObjectLibraryQuery.ContainsPrefabEntries(current, ids))
            {
                return false;
            }

            ObjectLibraryFolder candidate = CreateFolder(normalizedFolderName, parentFolderId, DateTimeOffset.UtcNow);
            ObjectLibraryEntry[] entries = current.Entries
                .Select(entry => ids.Contains(entry.Id)
                    ? ObjectLibraryRules.MoveEntry(entry, candidate.Id)
                    : entry)
                .ToArray();
            if (!TryPublish(entries, [.. current.Folders, candidate], current.Prefabs))
            {
                return false;
            }

            folder = candidate;
            return true;
        }
    }

    public bool TryRemoveEntries(IEnumerable<Guid> entryIds)
    {
        HashSet<Guid> ids = entryIds.ToHashSet();
        if (ids.Count == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (!ObjectLibraryQuery.ContainsAllEntries(current, ids)
             || ObjectLibraryQuery.ContainsPrefabEntries(current, ids))
            {
                return false;
            }

            ObjectLibraryEntry[] entries = current.Entries
                .Where(entry => !ids.Contains(entry.Id))
                .ToArray();
            return TryPublish(entries, current.Folders, current.Prefabs);
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _store.FilesChanged -= HandleFilesChanged;
        }
    }

    private bool TryRenameGroup(Guid groupId, string name, bool isPrefab)
    {
        string normalizedName = ObjectLibraryRules.NormalizeName(name);
        if (normalizedName.Length == 0)
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (isPrefab)
            {
                int index = IndexOfGroup(current.Prefabs, groupId);
                if (index < 0
                 || ObjectLibraryRules.NamesMatch(current.Prefabs[index].Name, normalizedName)
                 || ObjectLibraryQuery.ContainsGroupName(
                     current,
                     normalizedName,
                     current.Prefabs[index].ParentFolderId,
                     groupId))
                {
                    return false;
                }

                ObjectLibraryPrefab[] prefabs = current.Prefabs.ToArray();
                prefabs[index] = prefabs[index] with { Name = normalizedName };
                return TryPublish(current.Entries, current.Folders, prefabs);
            }

            int folderIndex = IndexOfGroup(current.Folders, groupId);
            if (folderIndex < 0
             || ObjectLibraryRules.NamesMatch(current.Folders[folderIndex].Name, normalizedName)
             || ObjectLibraryQuery.ContainsGroupName(
                 current,
                 normalizedName,
                 current.Folders[folderIndex].ParentFolderId,
                 groupId))
            {
                return false;
            }

            ObjectLibraryFolder[] folders = current.Folders.ToArray();
            folders[folderIndex] = folders[folderIndex] with { Name = normalizedName };
            return TryPublish(current.Entries, folders, current.Prefabs);
        }
    }

    private bool TrySetGroupColor(Guid groupId, string color, bool isPrefab)
    {
        string normalizedColor = ObjectFolderUtility.SanitizeFolderColorValue(color);
        lock (_stateLock)
        {
            ObjectLibrarySnapshot current = _current;
            if (isPrefab)
            {
                int index = IndexOfGroup(current.Prefabs, groupId);
                if (index < 0 || ColorsMatch(current.Prefabs[index].Color, normalizedColor))
                {
                    return false;
                }

                ObjectLibraryPrefab[] prefabs = current.Prefabs.ToArray();
                prefabs[index] = prefabs[index] with { Color = normalizedColor };
                return TryPublish(current.Entries, current.Folders, prefabs);
            }

            int folderIndex = IndexOfGroup(current.Folders, groupId);
            if (folderIndex < 0 || ColorsMatch(current.Folders[folderIndex].Color, normalizedColor))
            {
                return false;
            }

            ObjectLibraryFolder[] folders = current.Folders.ToArray();
            folders[folderIndex] = folders[folderIndex] with { Color = normalizedColor };
            return TryPublish(current.Entries, folders, current.Prefabs);
        }
    }

    private bool TryPublish(
        IEnumerable<ObjectLibraryEntry> entries,
        IEnumerable<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
    {
        ObjectLibraryEntry[] sourceEntries = entries.ToArray();
        ObjectLibraryFolder[] sourceFolders = folders.ToArray();
        ObjectLibraryPrefab[] sourcePrefabs = prefabs.ToArray();
        ObjectLibraryContent content = ObjectLibraryRules.Normalize(sourceEntries, sourceFolders, sourcePrefabs);
        if (content.Entries.Length != sourceEntries.Length
         || content.Folders.Length != sourceFolders.Length
         || content.Prefabs.Length != sourcePrefabs.Length)
        {
            return false;
        }

        ObjectLibrarySnapshot current = _current;
        if (!_store.TrySave(current, content))
        {
            return false;
        }

        Volatile.Write(ref _current, ObjectLibrarySnapshot.Create(current.Revision + 1, content));
        return true;
    }

    private static bool TryBuildImportedContent(
        ObjectLibrarySnapshot current,
        ObjectLibraryImport import,
        Guid? parentFolderId,
        out ObjectLibraryContent content,
        out ObjectLibraryGroup candidate)
    {
        content = default;
        candidate = null!;
        ObjectLibraryContent isolated = import.Content;
        if (!ObjectLibraryHierarchy.TryCreate(
                isolated.Folders,
                isolated.Prefabs,
                out ObjectLibraryHierarchy isolatedHierarchy,
                out _)
         || isolatedHierarchy.Roots.Count != 1
         || isolatedHierarchy.Roots[0].Id != import.RootGroupId)
        {
            return false;
        }

        candidate = isolatedHierarchy.Roots[0];
        ObjectLibraryHierarchy currentHierarchy = ObjectLibraryHierarchy.Create(current);
        if (!currentHierarchy.ContainsFolder(parentFolderId))
        {
            return false;
        }

        HashSet<Guid> currentEntryIds = current.Entries.Select(static entry => entry.Id).ToHashSet();
        HashSet<Guid> currentGroupIds = current.Folders
            .Select(static group => group.Id)
            .Concat(current.Prefabs.Select(static group => group.Id))
            .ToHashSet();
        if (isolated.Entries.Any(entry => currentEntryIds.Contains(entry.Id))
         || isolated.Folders.Any(group => currentGroupIds.Contains(group.Id))
         || isolated.Prefabs.Any(group => currentGroupIds.Contains(group.Id)))
        {
            return false;
        }

        string name = ObjectLibraryRules.ResolveAvailableGroupName(current, candidate.Name, parentFolderId);
        if (name.Length == 0)
        {
            return false;
        }

        Guid candidateId = candidate.Id;
        ObjectLibraryFolder[] importedFolders = isolated.Folders
            .Select(folder => folder.Id == candidateId
                ? folder with { Name = name, ParentFolderId = parentFolderId }
                : folder)
            .ToArray();
        ObjectLibraryPrefab[] importedPrefabs = isolated.Prefabs
            .Select(prefab => prefab.Id == candidateId
                ? prefab with { Name = name, ParentFolderId = parentFolderId }
                : prefab)
            .ToArray();
        content = ObjectLibraryRules.Normalize(
            current.Entries.Concat(isolated.Entries),
            current.Folders.Concat(importedFolders),
            current.Prefabs.Concat(importedPrefabs));
        candidate = content.Folders.Cast<ObjectLibraryGroup>()
            .Concat(content.Prefabs)
            .FirstOrDefault(group => group.Id == import.RootGroupId)!;
        return candidate is not null
            && content.Entries.Length == current.Entries.Count + isolated.Entries.Length
            && content.Folders.Length == current.Folders.Count + isolated.Folders.Length
            && content.Prefabs.Length == current.Prefabs.Count + isolated.Prefabs.Length;
    }

    private void HandleFilesChanged()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            ObjectLibraryContent content = _store.Load();
            ObjectLibrarySnapshot current = _current;
            if (!ObjectLibraryRules.ContentEquals(current, content))
            {
                Volatile.Write(ref _current, ObjectLibrarySnapshot.Create(current.Revision + 1, content));
            }
        }
    }

    private static ObjectLibraryEntry CreateEntry(
        string name,
        ObjectLibraryPreset preset,
        Guid? folderId,
        DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            FolderId = folderId,
            CreatedAtUtc = createdAt,
            Preset = preset,
        };

    private static ObjectLibraryFolder CreateFolder(
        string name,
        Guid? parentFolderId,
        DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentFolderId = parentFolderId,
            CreatedAtUtc = createdAt,
        };

    private static int IndexOfEntry(IReadOnlyList<ObjectLibraryEntry> entries, Guid entryId)
    {
        for (int index = 0; index < entries.Count; ++index)
        {
            if (entries[index].Id == entryId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfGroup<TGroup>(IReadOnlyList<TGroup> groups, Guid groupId)
        where TGroup : ObjectLibraryGroup
    {
        for (int index = 0; index < groups.Count; ++index)
        {
            if (groups[index].Id == groupId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ColorsMatch(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
