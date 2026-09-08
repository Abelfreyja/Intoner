using Intoner.Objects.Utils;

namespace Intoner.Objects.Library;

/// <summary> owns hierarchy queries and sibling naming for library groups </summary>
internal sealed class ObjectLibraryHierarchy
{
    private static readonly Guid RootId = Guid.Empty;

    private readonly HierarchyIndex<Guid, ObjectLibraryGroup> _index;
    private readonly IReadOnlyDictionary<Guid, ObjectLibraryFolder> _folders;

    private ObjectLibraryHierarchy(
        HierarchyIndex<Guid, ObjectLibraryGroup> index,
        IReadOnlyDictionary<Guid, ObjectLibraryFolder> folders)
    {
        _index = index;
        _folders = folders;
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
            folderArray.ToDictionary(static folder => folder.Id));
        return true;
    }

    public static ObjectLibraryHierarchy Create(ObjectLibrarySnapshot library)
        => Create(library.Folders, library.Prefabs);

    public static ObjectLibraryHierarchy Create(ObjectLibraryContent library)
        => Create(library.Folders, library.Prefabs);

    private static ObjectLibraryHierarchy Create(
        IEnumerable<ObjectLibraryFolder> folders,
        IEnumerable<ObjectLibraryPrefab> prefabs)
        => TryCreate(folders, prefabs, out ObjectLibraryHierarchy hierarchy, out _)
            ? hierarchy
            : throw new InvalidOperationException("Object Library content does not form a valid hierarchy");
}
