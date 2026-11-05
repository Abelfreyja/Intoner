using Intoner.Objects.Utils;
using System.Runtime.CompilerServices;

namespace Intoner.Objects.Library;

/// <summary> owns hierarchy queries and sibling naming for library groups </summary>
internal sealed class ObjectLibraryHierarchy
{
    private static readonly Guid RootId = Guid.Empty;
    private static readonly ConditionalWeakTable<ObjectLibrarySnapshot, ObjectLibraryHierarchy> Snapshots = new();

    private readonly HierarchyIndex<Guid, ObjectLibraryGroup> _index;
    private readonly IReadOnlyDictionary<Guid, ObjectLibraryFolder> _folders;
    private readonly Dictionary<Guid, ObjectLibraryPrefab> _prefabsByEntry = [];

    private ObjectLibraryHierarchy(
        HierarchyIndex<Guid, ObjectLibraryGroup> index,
        IReadOnlyDictionary<Guid, ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
    {
        _index = index;
        _folders = folders;
        foreach (ObjectLibraryPrefab prefab in prefabs)
        {
            foreach (Guid entryId in prefab.EntryIds)
            {
                _prefabsByEntry.TryAdd(entryId, prefab);
            }
        }
    }

    public IReadOnlyList<ObjectLibraryGroup> Roots
        => _index.Roots;

    public IReadOnlyList<ObjectLibraryGroup> GetChildren(Guid? parentFolderId)
        => _index.GetChildren(parentFolderId ?? RootId);

    public IReadOnlyList<ObjectLibraryGroup> GetSubtree(Guid groupId)
        => _index.GetSubtree(groupId);

    public IReadOnlyList<ObjectLibraryGroup> GetDescendants(Guid groupId)
        => _index.GetDescendants(groupId);

    public IReadOnlyDictionary<Guid, IReadOnlyList<TValue>> CollectSubtreeValues<TValue>(
        Func<ObjectLibraryGroup, IEnumerable<TValue>> getDirectValues)
        => _index.CollectSubtreeValues(getDirectValues);

    public bool IsDescendantOf(Guid candidateId, Guid ancestorId)
        => _index.IsDescendantOf(candidateId, ancestorId);

    public bool TryGetGroup(Guid groupId, out ObjectLibraryGroup group)
        => _index.TryGetNode(groupId, out group!);

    public ObjectLibraryGroup? GetEntryGroup(ObjectLibraryEntry entry)
    {
        if (_prefabsByEntry.TryGetValue(entry.Id, out ObjectLibraryPrefab? prefab))
        {
            return prefab;
        }

        return entry.FolderId is { } folderId && TryGetGroup(folderId, out ObjectLibraryGroup folder) ? folder : null;
    }

    public string GetGroupPath(ObjectLibraryGroup group)
    {
        List<string> segments = [];
        ObjectLibraryGroup current = group;
        while (true)
        {
            segments.Add(current.Name);

            if (current.ParentFolderId is not { } parentId || !TryGetGroup(parentId, out current))
            {
                break;
            }
        }

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    public bool ContainsFolder(Guid? folderId)
        => !folderId.HasValue || _folders.ContainsKey(folderId.Value);

    public bool ContainsSiblingName(Guid? parentFolderId, string name, Guid? excludedGroupId = null)
        => GetChildren(parentFolderId).Any(group => group.Id != excludedGroupId
            && ObjectLibraryRules.NamesMatch(group.Name, name));

    public static bool TryCreate(
        IEnumerable<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs,
        out ObjectLibraryHierarchy hierarchy,
        out IReadOnlySet<Guid> invalidIds)
    {
        ObjectLibraryFolder[] folderArray = folders.ToArray();
        ObjectLibraryGroup[] groups = folderArray.Cast<ObjectLibraryGroup>().Concat(prefabs).ToArray();
        HashSet<Guid> folderIds = folderArray.Select(static folder => folder.Id).ToHashSet();
        HashSet<Guid> invalid = groups
            .Where(group => group.ParentFolderId is { } parentId
                && !folderIds.Contains(parentId))
            .Select(static group => group.Id)
            .ToHashSet();

        bool valid = HierarchyIndex<Guid, ObjectLibraryGroup>.TryCreate(
            groups,
            static group => group.Id,
            static group => group.ParentFolderId ?? RootId,
            RootId,
            comparer: null,
            out HierarchyIndex<Guid, ObjectLibraryGroup> index,
            out IReadOnlySet<Guid> hierarchyInvalidIds);
        invalid.UnionWith(hierarchyInvalidIds);
        invalidIds = invalid;
        if (!valid || invalid.Count > 0)
        {
            hierarchy = null!;
            return false;
        }

        hierarchy = new ObjectLibraryHierarchy(
            index,
            folderArray.ToDictionary(static folder => folder.Id),
            groups.OfType<ObjectLibraryPrefab>());
        return true;
    }

    public static ObjectLibraryHierarchy Create(ObjectLibrarySnapshot library)
        => Snapshots.GetValue(library, static snapshot => Create(snapshot.Folders, snapshot.Prefabs));

    public static ObjectLibraryHierarchy Create(ObjectLibraryContent library)
        => Create(library.Folders, library.Prefabs);

    private static ObjectLibraryHierarchy Create(
        IEnumerable<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
        => TryCreate(folders, prefabs, out ObjectLibraryHierarchy hierarchy, out _)
            ? hierarchy
            : throw new InvalidOperationException("Object Library content does not form a valid hierarchy");
}
