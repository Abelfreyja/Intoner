namespace Intoner.Objects.Library;

/// <summary> provides read-only queries over an object library snapshot </summary>
internal static class ObjectLibraryQuery
{
    public static IReadOnlyList<ObjectLibraryEntry> FindEntries(
        ObjectLibrarySnapshot library,
        ObjectLibraryPreset preset)
    {
        if (!ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalizedPreset))
        {
            return [];
        }

        return library.Entries
            .Where(candidate => candidate.Preset == normalizedPreset)
            .ToArray();
    }

    public static IReadOnlyList<ObjectLibraryEntry> FindEntriesBySource(
        ObjectLibrarySnapshot library,
        ObjectLibraryPreset preset)
    {
        if (!ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalizedPreset))
        {
            return [];
        }

        return library.Entries
            .Where(candidate => ObjectLibraryRules.HasSameSource(candidate.Preset, normalizedPreset))
            .ToArray();
    }

    public static ObjectLibraryEntry? FindEntry(ObjectLibrarySnapshot library, Guid entryId)
        => library.Entries.FirstOrDefault(candidate => candidate.Id == entryId);

    public static ObjectLibraryPrefab? FindPrefab(ObjectLibrarySnapshot library, Guid prefabId)
        => library.Prefabs.FirstOrDefault(candidate => candidate.Id == prefabId);

    public static IReadOnlyList<ObjectLibraryEntry> GetPrefabEntries(
        ObjectLibrarySnapshot library,
        ObjectLibraryPrefab prefab)
    {
        Dictionary<Guid, ObjectLibraryEntry> entries = library.Entries.ToDictionary(static entry => entry.Id);
        return GetPrefabEntries(prefab, entries);
    }

    public static IReadOnlyList<ObjectLibraryEntry> GetPrefabEntries(
        ObjectLibraryPrefab prefab,
        IReadOnlyDictionary<Guid, ObjectLibraryEntry> entries)
        => prefab.EntryIds
            .Where(entries.ContainsKey)
            .Select(id => entries[id])
            .ToArray();

    public static bool ContainsPrefabEntries(ObjectLibrarySnapshot library, IReadOnlySet<Guid> entryIds)
        => entryIds.Count > 0
        && library.Prefabs.Any(prefab => prefab.EntryIds.Any(entryIds.Contains));

    public static bool ContainsGroupName(
        ObjectLibrarySnapshot library,
        string name,
        Guid? parentFolderId,
        Guid? excludedGroupId = null)
        => ObjectLibraryHierarchy.Create(library).ContainsSiblingName(parentFolderId, name, excludedGroupId);

    public static IReadOnlyList<ObjectLibraryEntry> GetGroupEntries(
        ObjectLibrarySnapshot library,
        ObjectLibraryGroup group,
        bool includeDescendants)
    {
        HashSet<Guid> groupIds = includeDescendants
            ? ObjectLibraryHierarchy.Create(library).GetSubtree(group.Id).Select(static item => item.Id).ToHashSet()
            : [group.Id];
        HashSet<Guid> folderIds = library.Folders
            .Where(folder => groupIds.Contains(folder.Id))
            .Select(static folder => folder.Id)
            .ToHashSet();
        HashSet<Guid> entryIds = library.Prefabs
            .Where(prefab => groupIds.Contains(prefab.Id))
            .SelectMany(static prefab => prefab.EntryIds)
            .ToHashSet();
        return library.Entries
            .Where(entry => entryIds.Contains(entry.Id)
                || entry.FolderId is { } folderId && folderIds.Contains(folderId))
            .ToArray();
    }

    public static bool ContainsAllEntries(ObjectLibrarySnapshot library, IReadOnlySet<Guid> entryIds)
    {
        if (entryIds.Count == 0)
        {
            return false;
        }

        int found = 0;
        foreach (ObjectLibraryEntry entry in library.Entries)
        {
            if (entryIds.Contains(entry.Id) && ++found == entryIds.Count)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryResolveCommonFolder(
        ObjectLibrarySnapshot library,
        IEnumerable<Guid> entryIds,
        out Guid? folderId)
    {
        HashSet<Guid> unresolved = entryIds.ToHashSet();
        folderId = null;
        if (unresolved.Count == 0
         || ContainsPrefabEntries(library, unresolved))
        {
            return false;
        }

        bool found = false;
        foreach (ObjectLibraryEntry entry in library.Entries)
        {
            if (!unresolved.Remove(entry.Id))
            {
                continue;
            }

            if (!found)
            {
                folderId = entry.FolderId;
                found = true;
            }
            else if (folderId != entry.FolderId)
            {
                folderId = null;
                return false;
            }

            if (unresolved.Count == 0)
            {
                return true;
            }
        }

        folderId = null;
        return false;
    }
}
