using Intoner.Objects.Library;
using Intoner.Objects.Utils;

namespace Intoner.Objects.UI;

internal sealed class LibraryBrowserView(
    IReadOnlyList<ObjectLibraryEntry> rootEntries,
    IReadOnlyList<LibraryBrowserView.Group> rootGroups,
    IReadOnlyList<Guid> selectableOrder)
{
    public sealed class Group(
        ObjectLibraryGroup source,
        IReadOnlyList<ObjectLibraryEntry> allEntries,
        IReadOnlyList<ObjectLibraryEntry> visibleEntries,
        IReadOnlyList<Group> children,
        bool expanded)
    {
        public bool Expanded { get; } = expanded;
        public ObjectLibraryGroup Source { get; } = source;
        public IReadOnlyList<ObjectLibraryEntry> AllEntries { get; } = allEntries;
        public IReadOnlyList<ObjectLibraryEntry> VisibleEntries { get; } = visibleEntries;
        public IReadOnlyList<Group> Children { get; } = children;
        public int VisibleEntryCount { get; } = visibleEntries.Count + children.Sum(static child => child.VisibleEntryCount);
        public int VisibleRowCount { get; } = expanded ? 1 + visibleEntries.Count + children.Sum(static child => child.VisibleRowCount) : 1;
    }

    public IReadOnlyList<ObjectLibraryEntry> RootEntries { get; } = rootEntries;
    public IReadOnlyList<Group> RootGroups { get; } = rootGroups;
    public IReadOnlyList<Guid> SelectableOrder { get; } = selectableOrder;
    public int VisibleEntryCount { get; } = rootEntries.Count + rootGroups.Sum(static group => group.VisibleEntryCount);
    public int VisibleRowCount { get; } = rootEntries.Count + rootGroups.Sum(static group => group.VisibleRowCount);

    public static LibraryBrowserView Create(
        ObjectLibrarySnapshot library,
        string filter,
        string kindFilter,
        IReadOnlySet<Guid> expandedGroups,
        Func<ObjectLibraryEntry, string> getSearchText)
    {
        string[] searchTokens = SearchTermUtility.BuildSearchTokens(filter);
        ObjectLibraryHierarchy hierarchy = ObjectLibraryHierarchy.Create(library);

        List<ObjectLibraryEntry> rootEntries = [];
        Dictionary<Guid, List<ObjectLibraryEntry>> entriesByFolder = [];
        Dictionary<Guid, ObjectLibraryEntry> entriesById = [];
        foreach (ObjectLibraryEntry entry in library.Entries.OrderBy(static entry => entry.CreatedAtUtc))
        {
            entriesById.Add(entry.Id, entry);
            ObjectLibraryGroup? group = hierarchy.GetEntryGroup(entry);

            if (group is ObjectLibraryFolder folder)
            {
                if (!entriesByFolder.TryGetValue(folder.Id, out List<ObjectLibraryEntry>? folderEntries))
                {
                    folderEntries = [];
                    entriesByFolder.Add(folder.Id, folderEntries);
                }

                folderEntries.Add(entry);
            }
            else if (group is null && MatchesKind(entry) && MatchesSearch(entry))
            {
                rootEntries.Add(entry);
            }
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<ObjectLibraryEntry>> subtreeEntries = hierarchy.CollectSubtreeValues(DirectEntries);
        List<Group> groups = BuildGroups(hierarchy.Roots, ancestorMatchesSearch: false);
        List<Guid> selectableOrder = rootEntries.Select(static entry => entry.Id).ToList();
        foreach (Group group in groups)
        {
            AddSelectableEntries(group);
        }

        return new LibraryBrowserView(rootEntries, groups, selectableOrder);

        bool MatchesKind(ObjectLibraryEntry entry)
            => kindFilter.Length == 0
            || string.Equals(entry.Preset.Kind.ToString(), kindFilter, StringComparison.OrdinalIgnoreCase);

        bool MatchesSearch(ObjectLibraryEntry entry)
            => searchTokens.Length == 0
            || SearchTermUtility.MatchesSearchText(getSearchText(entry), searchTokens);

        IReadOnlyList<ObjectLibraryEntry> DirectEntries(ObjectLibraryGroup group)
            => group switch
            {
                ObjectLibraryFolder folder => entriesByFolder.GetValueOrDefault(folder.Id) ?? [],
                ObjectLibraryPrefab prefab => ObjectLibraryQuery.GetPrefabEntries(prefab, entriesById),
                _ => [],
            };

        List<Group> BuildGroups(IReadOnlyList<ObjectLibraryGroup> sources, bool ancestorMatchesSearch)
        {
            List<Group> result = [];
            foreach (ObjectLibraryGroup source in sources.OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool groupMatches = ancestorMatchesSearch
                    || searchTokens.Length == 0
                    || SearchTermUtility.MatchesSearchText(SearchTermUtility.BuildSearchText([source.Name]), searchTokens);
                List<ObjectLibraryEntry> visibleEntries = [];
                foreach (ObjectLibraryEntry entry in DirectEntries(source))
                {
                    if (MatchesKind(entry) && (groupMatches || MatchesSearch(entry)))
                    {
                        visibleEntries.Add(entry);
                    }
                }

                List<Group> children = BuildGroups(hierarchy.GetChildren(source.Id), groupMatches);
                if (visibleEntries.Count > 0 || children.Count > 0 || groupMatches && kindFilter.Length == 0)
                {
                    result.Add(new Group(source, subtreeEntries[source.Id], visibleEntries, children,
                        filter.Length > 0 || expandedGroups.Contains(source.Id)));
                }
            }

            return result;
        }

        void AddSelectableEntries(Group group)
        {
            if (!group.Expanded)
            {
                return;
            }

            foreach (ObjectLibraryEntry entry in group.VisibleEntries)
            {
                selectableOrder.Add(entry.Id);
            }

            foreach (Group child in group.Children)
            {
                AddSelectableEntries(child);
            }
        }
    }
}
