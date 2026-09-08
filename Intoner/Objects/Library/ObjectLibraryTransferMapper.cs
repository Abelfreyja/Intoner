using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.Library;

/// <summary> maps complete library subtrees to and from portable object transfers </summary>
internal static class ObjectLibraryTransferMapper
{
    public static bool TryCreateDocument(
        ObjectLibrarySnapshot library,
        ObjectLibraryGroup group,
        out ObjectTransferDocument document)
    {
        document = null!;
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);
        IReadOnlyList<ObjectLibraryGroup> groups = hierarchy.GetSubtree(group.Id);
        if (groups.Count == 0)
        {
            return false;
        }

        IReadOnlyList<ObjectLibraryEntry> entries = ObjectLibraryQuery.GetGroupEntries(
            library,
            group,
            includeDescendants: true);
        if (groups.Count > ObjectTransferCodec.MaximumObjectCount
         || entries.Count > ObjectTransferCodec.MaximumObjectCount
         || group is ObjectLibraryPrefab && entries.Count == 0)
        {
            return false;
        }

        document = new ObjectTransferDocument(
            ObjectTransferKind.ObjectLibrary,
            ObjectLibrary: new ObjectLibraryTransfer(
                group.Id,
                groups.Select(candidate => ToTransferGroup(candidate, candidate.Id == group.Id)).ToArray(),
                entries.Select(ToTransferEntry).ToArray()));
        return true;
    }

    public static bool TryPrepareImport(ObjectTransferDocument document, out ObjectLibraryImport import)
    {
        import = null!;
        if (document is not
            {
                Kind: ObjectTransferKind.ObjectLibrary,
                SceneObjects: null,
                ObjectLibrary: { } source,
                Transform: null,
            }
         || source.Groups is null
         || source.Entries is null
         || source.Groups.Count == 0
         || source.Groups.Count > ObjectTransferCodec.MaximumObjectCount
         || source.Entries.Count > ObjectTransferCodec.MaximumObjectCount)
        {
            return false;
        }

        Dictionary<Guid, Guid> groupIds = CreateIdMap(source.Groups.Select(static group => group?.Id ?? Guid.Empty));
        Dictionary<Guid, Guid> entryIds = CreateIdMap(source.Entries.Select(static entry => entry?.Id ?? Guid.Empty));
        if (groupIds.Count != source.Groups.Count
         || entryIds.Count != source.Entries.Count
         || !groupIds.TryGetValue(source.RootGroupId, out Guid rootGroupId))
        {
            return false;
        }

        Dictionary<Guid, ObjectLibraryTransferGroup> sourceGroups = source.Groups.ToDictionary(static group => group.Id);
        ObjectLibraryTransferGroup root = sourceGroups[source.RootGroupId];
        if (!HierarchyIndex<Guid, ObjectLibraryTransferGroup>.TryCreate(
                source.Groups,
                static group => group.Id,
                static group => group.ParentFolderId ?? Guid.Empty,
                Guid.Empty,
                comparer: null,
                out HierarchyIndex<Guid, ObjectLibraryTransferGroup> hierarchy,
                out _)
         || hierarchy.Roots.Count != 1
         || hierarchy.Roots[0].Id != source.RootGroupId
         || root.ParentFolderId.HasValue)
        {
            return false;
        }

        Dictionary<Guid, Guid> prefabMembership = [];
        if (source.Groups.Any(group => !TryValidateGroup(
                group,
                sourceGroups,
                entryIds,
                prefabMembership)))
        {
            return false;
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        ObjectLibraryEntry[] entries = new ObjectLibraryEntry[source.Entries.Count];
        for (int index = 0; index < source.Entries.Count; ++index)
        {
            if (!TryCreateEntry(
                    source.Entries[index],
                    createdAt,
                    groupIds,
                    entryIds,
                    sourceGroups,
                    prefabMembership,
                    out entries[index]))
            {
                return false;
            }
        }

        List<ObjectLibraryFolder> folders = [];
        List<ObjectLibraryPrefab> prefabs = [];
        foreach (ObjectLibraryTransferGroup group in source.Groups)
        {
            Guid? parentFolderId = group.ParentFolderId is { } parentId ? groupIds[parentId] : null;
            if (group.Kind == ObjectLibraryTransferKind.Folder)
            {
                folders.Add(new ObjectLibraryFolder
                {
                    Id = groupIds[group.Id],
                    Name = ObjectLibraryRules.NormalizeName(group.Name),
                    ParentFolderId = parentFolderId,
                    Color = ObjectFolderUtility.SanitizeFolderColorValue(group.Color),
                    CreatedAtUtc = createdAt,
                });
                continue;
            }

            SceneCreationContext capturedIn = ObjectApiMapper.ToCreationContext(group.CapturedIn!);
            prefabs.Add(new ObjectLibraryPrefab
            {
                Id = groupIds[group.Id],
                Name = ObjectLibraryRules.NormalizeName(group.Name),
                ParentFolderId = parentFolderId,
                Color = ObjectFolderUtility.SanitizeFolderColorValue(group.Color),
                CreatedAtUtc = createdAt,
                CapturedIn = capturedIn,
                EntryIds = group.EntryIds.Select(id => entryIds[id]).ToArray(),
            });
        }

        ObjectLibraryContent content = ObjectLibraryRules.Normalize(entries, folders, prefabs);
        if (!MatchesNormalizedContent(content, entries, folders, prefabs))
        {
            return false;
        }

        import = new ObjectLibraryImport(content, rootGroupId);
        return true;
    }

    private static ObjectLibraryTransferGroup ToTransferGroup(ObjectLibraryGroup group, bool isRoot)
        => group switch
        {
            ObjectLibraryFolder folder => new ObjectLibraryTransferGroup(
                folder.Id,
                ObjectLibraryTransferKind.Folder,
                folder.Name,
                isRoot ? null : folder.ParentFolderId,
                folder.Color,
                CapturedIn: null,
                EntryIds: []),
            ObjectLibraryPrefab prefab => new ObjectLibraryTransferGroup(
                prefab.Id,
                ObjectLibraryTransferKind.Prefab,
                prefab.Name,
                isRoot ? null : prefab.ParentFolderId,
                prefab.Color,
                ObjectApiMapper.ToLocation(prefab.CapturedIn),
                prefab.EntryIds),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
        };

    private static ObjectLibraryTransferEntry ToTransferEntry(ObjectLibraryEntry entry)
        => new(
            entry.Id,
            entry.Name,
            ObjectApiMapper.ToWorldObjectKind(entry.Preset.Kind),
            entry.Preset.Visible,
            ObjectApiMapper.ToObjectVector3(entry.Preset.Scale),
            ObjectApiMapper.ToWorldModel(entry.Preset.Kind, entry.Preset.Model),
            entry.WorldTransform is { } transform ? ObjectApiMapper.ToWorldTransform(transform) : null,
            entry.AttachmentParentEntryId,
            entry.FolderId);

    private static bool TryValidateGroup(
        ObjectLibraryTransferGroup group,
        IReadOnlyDictionary<Guid, ObjectLibraryTransferGroup> groups,
        IReadOnlyDictionary<Guid, Guid> entryIds,
        IDictionary<Guid, Guid> prefabMembership)
    {
        if (group is null
         || !Enum.IsDefined(group.Kind)
         || ObjectLibraryRules.NormalizeName(group.Name).Length == 0
         || group.EntryIds is null
         || group.ParentFolderId is { } parentId
            && (!groups.TryGetValue(parentId, out ObjectLibraryTransferGroup? parent)
                || parent.Kind != ObjectLibraryTransferKind.Folder))
        {
            return false;
        }

        if (group.Kind == ObjectLibraryTransferKind.Folder)
        {
            return group.CapturedIn is null && group.EntryIds.Count == 0;
        }

        if (group.CapturedIn is null
         || !ObjectApiMapper.ToCreationContext(group.CapturedIn).Scope.IsValid
         || group.EntryIds.Count == 0
         || group.EntryIds.Distinct().Count() != group.EntryIds.Count)
        {
            return false;
        }

        if (group.EntryIds.Any(entryId => !entryIds.ContainsKey(entryId)
            || !prefabMembership.TryAdd(entryId, group.Id)))
        {
            return false;
        }

        return true;
    }

    private static bool TryCreateEntry(
        ObjectLibraryTransferEntry source,
        DateTimeOffset createdAt,
        IReadOnlyDictionary<Guid, Guid> groupIds,
        IReadOnlyDictionary<Guid, Guid> entryIds,
        IReadOnlyDictionary<Guid, ObjectLibraryTransferGroup> groups,
        IReadOnlyDictionary<Guid, Guid> prefabMembership,
        out ObjectLibraryEntry entry)
    {
        entry = null!;
        bool inPrefab = prefabMembership.TryGetValue(source.Id, out Guid prefabId);
        bool inFolder = source.FolderId.HasValue;
        Guid folderId = source.FolderId ?? Guid.Empty;
        if (inPrefab == inFolder
         || inFolder && (!groups.TryGetValue(folderId, out ObjectLibraryTransferGroup? folder)
             || folder.Kind != ObjectLibraryTransferKind.Folder)
         || inPrefab && groups[prefabId].Kind != ObjectLibraryTransferKind.Prefab
         || inFolder && (source.WorldTransform is not null || source.AttachmentParentEntryId is not null)
         || inPrefab && (source.WorldTransform is null
             || source.AttachmentParentEntryId is { } parentId
                && (!prefabMembership.TryGetValue(parentId, out Guid parentPrefabId)
                    || parentPrefabId != prefabId)))
        {
            return false;
        }

        string name = ObjectLibraryRules.NormalizeName(source.Name);
        if (name.Length == 0
         || !ObjectApiMapper.TryToObjectKind(source.Kind, out ObjectKind kind)
         || !ObjectApiMapper.TryToObjectData(kind, source.Model, out ObjectData model)
         || !ObjectLibraryRules.TryNormalizePreset(
             new ObjectLibraryPreset
             {
                 Kind = kind,
                 Visible = source.Visible,
                 Scale = ObjectApiMapper.ToVector3(source.Scale),
                 Model = model,
             },
             out ObjectLibraryPreset? preset))
        {
            return false;
        }

        SceneTransform? worldTransform = null;
        if (inPrefab && !ObjectLibraryRules.TryNormalizeWorldTransform(
                ObjectApiMapper.ToTransform(source.WorldTransform!),
                preset.Scale,
                out worldTransform))
        {
            return false;
        }

        entry = new ObjectLibraryEntry
        {
            Id = entryIds[source.Id],
            Name = name,
            FolderId = inFolder ? groupIds[source.FolderId!.Value] : null,
            AttachmentParentEntryId = source.AttachmentParentEntryId is { } sourceParentId
                ? entryIds[sourceParentId]
                : null,
            CreatedAtUtc = createdAt,
            Preset = preset,
            WorldTransform = worldTransform,
        };
        return true;
    }

    private static bool MatchesNormalizedContent(
        ObjectLibraryContent content,
        IReadOnlyList<ObjectLibraryEntry> entries,
        IReadOnlyList<ObjectLibraryFolder> folders,
        IReadOnlyList<ObjectLibraryPrefab> prefabs)
    {
        if (!content.Entries.SequenceEqual(entries)
         || !content.Folders.SequenceEqual(folders)
         || content.Prefabs.Length != prefabs.Count)
        {
            return false;
        }

        for (int index = 0; index < prefabs.Count; ++index)
        {
            if (!ObjectLibraryRules.PrefabEquals(content.Prefabs[index], prefabs[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<Guid, Guid> CreateIdMap(IEnumerable<Guid> sourceIds)
    {
        Dictionary<Guid, Guid> ids = [];
        foreach (Guid id in sourceIds)
        {
            if (id == Guid.Empty || !ids.TryAdd(id, Guid.NewGuid()))
            {
                return [];
            }
        }

        return ids;
    }
}
