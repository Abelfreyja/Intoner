using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores saved layouts and temporary source layouts.
/// </summary>
internal interface IObjectLayoutManager
{
    /// <summary>
    /// Commits saved owners and workspace selection together, including creation and deletion.
    /// </summary>
    PersistentMutationStatus ReplaceStoredLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts, IReadOnlyList<Guid> enabledIds, Guid? creationId);
    /// <summary>
    /// Gets all saved local layouts.
    /// </summary>
    /// <returns>The saved layout snapshots.</returns>
    IReadOnlyList<ObjectLayoutSnapshot> GetLayouts();

    /// <summary>
    /// Gets all temporary source layouts.
    /// </summary>
    /// <returns>The temporary layout snapshots.</returns>
    IReadOnlyList<ObjectTemporaryLayoutSnapshot> GetTemporaryLayouts();

    /// <summary>
    /// Gets all layouts currently loaded into the composed object scene.
    /// </summary>
    /// <returns>The loaded layout snapshots.</returns>
    IReadOnlyList<ObjectLoadedLayoutSnapshot> GetLoadedLayouts();

    /// <summary>
    /// Gets enabled saved layouts in their configured order.
    /// </summary>
    /// <returns>The persistent layouts contributing to the workspace.</returns>
    IReadOnlyList<ObjectLayoutSnapshot> GetEnabledLayouts();

    /// <summary>
    /// Gets the destination for new objects, or null for standalone storage.
    /// </summary>
    Guid? GetCreationLayoutId();

    /// <summary>
    /// Checks whether a saved layout contributes to the workspace.
    /// </summary>
    /// <param name="id">The saved layout id.</param>
    /// <returns>true when the layout is enabled.</returns>
    bool IsLayoutEnabled(Guid id);

    /// <summary>
    /// Tries to resolve one saved layout by id.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="layout">The resolved layout when found.</param>
    /// <returns>true when the layout exists.</returns>
    bool TryGetLayout(Guid id, out ObjectLayoutSnapshot layout);

    /// <summary>
    /// Creates a new empty saved layout.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <param name="layout">The created layout snapshot when storage succeeds.</param>
    /// <returns>true when the layout was stored and added.</returns>
    bool TryCreateLayout(string name, out ObjectLayoutSnapshot layout);

    /// <summary>
    /// Creates a new saved layout with initial object and folder contents.
    /// </summary>
    /// <param name="name">the requested layout name.</param>
    /// <param name="objects">the initial layout objects.</param>
    /// <param name="folders">the initial folders.</param>
    /// <param name="layout">the created layout snapshot when storage succeeds.</param>
    /// <returns>true when the layout was stored and added.</returns>
    bool TryCreateLayout(
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectLayoutSnapshot layout);

    /// <summary>
    /// Creates a saved owner with objects, folders, and flat groups in one write.
    /// </summary>
    bool TryCreateLayout(string name, IReadOnlyList<ObjectSnapshot> objects, IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectLayoutSnapshot layout, IReadOnlyList<SceneGroupSnapshot> groups);

    /// <summary>
    /// Renames one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="name">The requested layout name.</param>
    /// <returns>true when the layout exists and the name is valid.</returns>
    bool TryRenameLayout(Guid id, string name);

    /// <summary>
    /// Replaces the persisted objects for one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="objects">The replacement object list.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutObjects(Guid id, IReadOnlyList<ObjectSnapshot> objects);

    /// <summary> stores object snapshots with one write per affected layout under the scene state lock </summary>
    /// <param name="snapshots"> distinct object snapshots with their existing layout ownership </param>
    /// <returns> success, a rejected request, or a storage failure; recovery required means file rollback failed </returns>
    PersistentMutationStatus UpsertLayoutObjects(IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary>
    /// Replaces owner contents with one write per changed layout and rollback on storage failure.
    /// </summary>
    /// <param name="updates">Distinct saved layout owners and their replacement contents.</param>
    /// <returns>The durable mutation status without partial publication.</returns>
    PersistentMutationStatus ReplaceLayoutContents(IReadOnlyList<ObjectLayoutContentUpdate> updates);

    /// <summary>
    /// Restores exact saved snapshots as a batch without advancing their individual revisions.
    /// </summary>
    /// <param name="layouts">Distinct existing layouts to restore.</param>
    /// <returns>The durable restoration status.</returns>
    PersistentMutationStatus RestoreLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts);

    /// <summary>
    /// Advances revisions for saved layouts that reference any supplied object collection.
    /// </summary>
    /// <param name="collectionIds">The changed collection ids.</param>
    /// <param name="enabledLayoutAffected">true when an enabled layout references a changed collection.</param>
    /// <returns>The durable update status.</returns>
    PersistentMutationStatus AdvanceCollectionDependencyRevisions(
        IReadOnlySet<string> collectionIds,
        out bool enabledLayoutAffected);

    /// <summary>
    /// Stores enabled layout membership and the creation destination together.
    /// </summary>
    /// <param name="enabledLayoutIds">Distinct saved layout ids in workspace order.</param>
    /// <param name="creationLayoutId">An enabled layout id, or null for standalone storage.</param>
    /// <returns>true when the selection is valid and configuration was stored.</returns>
    bool TrySetWorkspaceLayouts(IReadOnlyList<Guid> enabledLayoutIds, Guid? creationLayoutId);

    /// <summary>applies a complete saved layout set loaded from disk</summary>
    /// <param name="layouts">the complete validated saved layout set</param>
    /// <returns>the reload result, including whether active scene content changed</returns>
    ObjectLayoutReloadResult ApplySavedLayoutReload(IReadOnlyList<ObjectLayoutSnapshot> layouts);

    /// <summary>
    /// Deletes one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <returns>The deletion status.</returns>
    PersistentMutationStatus DeleteLayout(Guid id);

    /// <summary>
    /// Checks whether any saved layout objects exist.
    /// </summary>
    /// <returns>true when saved layout objects are stored.</returns>
    bool HasAnyObjects();

    /// <summary>
    /// Checks whether any layouts are currently loaded.
    /// </summary>
    /// <returns>true when any saved default or temporary layouts are loaded.</returns>
    bool HasAnyLoadedLayouts();
}

/// <summary> describes whether a saved layout reload was applied and whether configuration remained synchronized </summary>
internal enum ObjectLayoutReloadStatus
{
    Applied,
    IdentityConflict,
    ConfigurationWriteFailed,
}

/// <summary> reports the committed saved layout reload state </summary>
internal readonly record struct ObjectLayoutReloadResult(
    ObjectLayoutReloadStatus Status,
    bool ActiveLayoutChanged,
    Guid ConflictingId = default)
{
    public bool IsApplied
        => Status is ObjectLayoutReloadStatus.Applied or ObjectLayoutReloadStatus.ConfigurationWriteFailed;
}

internal sealed partial class ObjectLayoutManager : IObjectLayoutManager
{
    private readonly Lock _stateLock;
    private readonly IObjectLayoutStore _layoutStore;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly ITemporarySourceStore _temporarySourceStore;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly Dictionary<Guid, ObjectLayoutSnapshot> _layouts = [];
    private readonly List<Guid> _layoutOrder = [];

    private readonly List<Guid> _enabledLayoutIds = [];
    private Guid? _creationLayoutId;
    private int _layoutCounter;
    private IReadOnlyList<ObjectLayoutSnapshot>? _layoutSnapshots;
    private long _layoutSnapshotsRevision;

    public ObjectLayoutManager(
        ILogger<ObjectLayoutManager> logger,
        ObjectStateLock stateLock,
        IObjectLayoutStore layoutStore,
        IIntonerConfigurationService configurationService,
        ITemporarySourceStore temporarySourceStore,
        IObjectRevisionTracker revisionTracker)
    {
        _stateLock = stateLock.Value;
        _layoutStore = layoutStore;
        _configurationService = configurationService;
        _temporarySourceStore = temporarySourceStore;
        _revisionTracker = revisionTracker;

        IReadOnlyList<ObjectLayoutSnapshot> loadedLayouts = _layoutStore.LoadLayouts();
        if (!TryValidateLayoutSet(loadedLayouts, out Guid conflictingId))
        {
            logger.LogWarning(
                "saved layouts contain conflicting object id {ObjectId}; starting without saved layouts",
                conflictingId);
            loadedLayouts = [];
        }

        LayoutConfiguration configured = _configurationService.Current.Layouts;
        _enabledLayoutIds.AddRange(configured.EnabledLayoutIds);
        _creationLayoutId = configured.CreationLayoutId;
        if (ReplaceSavedLayouts(loadedLayouts))
        {
            _ = StoreWorkspaceLayouts(_enabledLayoutIds, _creationLayoutId);
        }
    }

    public IReadOnlyList<ObjectLayoutSnapshot> GetLayouts()
    {
        lock (_stateLock)
        {
            long revision = _revisionTracker.GetSavedLayoutsRevision();
            if (_layoutSnapshots is null || revision != _layoutSnapshotsRevision)
            {
                _layoutSnapshots = _layoutOrder.Select(id => _layouts[id]).ToImmutableArray();
                _layoutSnapshotsRevision = revision;
            }

            return _layoutSnapshots;
        }
    }

    public IReadOnlyList<ObjectTemporaryLayoutSnapshot> GetTemporaryLayouts()
        => _temporarySourceStore.GetSources().Select(ToTemporaryLayout).ToList();

    public IReadOnlyList<ObjectLoadedLayoutSnapshot> GetLoadedLayouts()
    {
        lock (_stateLock)
        {
            List<ObjectLoadedLayoutSnapshot> loadedLayouts = [];
            foreach (ObjectLayoutSnapshot layout in GetEnabledLayouts())
            {
                loadedLayouts.Add(new ObjectLoadedLayoutSnapshot
                {
                    Kind = ObjectLoadedLayoutKind.Persistent,
                    LayoutId = layout.Id,
                    SourceKey = layout.Id.ToString("D"),
                    SourceSessionId = Guid.Empty,
                    Name = layout.Name,
                    Revision = layout.Revision,
                    UpdatedAtUtc = layout.UpdatedAtUtc,
                    Objects = layout.Objects,
                });
            }

            loadedLayouts.AddRange(_temporarySourceStore.GetSources()
                .Select(source => new ObjectLoadedLayoutSnapshot
                {
                    Kind = ObjectLoadedLayoutKind.Temporary,
                    LayoutId = null,
                    SourceKey = source.SourceKey,
                    SourceSessionId = source.SessionId,
                    Name = source.Name,
                    Revision = source.Revision,
                    UpdatedAtUtc = source.UpdatedAtUtc,
                    Objects = source.RuntimeObjects,
                }));
            return loadedLayouts;
        }
    }

    public IReadOnlyList<ObjectLayoutSnapshot> GetEnabledLayouts()
    {
        lock (_stateLock)
        {
            return _enabledLayoutIds.Select(id => _layouts[id]).ToList();
        }
    }

    public bool IsLayoutEnabled(Guid id)
    {
        lock (_stateLock)
        {
            return _enabledLayoutIds.Contains(id);
        }
    }

    public Guid? GetCreationLayoutId()
    {
        lock (_stateLock)
        {
            return _creationLayoutId;
        }
    }

    public bool TryGetLayout(Guid id, out ObjectLayoutSnapshot layout)
    {
        lock (_stateLock)
        {
            return _layouts.TryGetValue(id, out layout!);
        }
    }

    public bool TryCreateLayout(string name, out ObjectLayoutSnapshot layout)
        => TryCreateLayout(name, [], [], out layout);

    public bool TryCreateLayout(
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectLayoutSnapshot layout)
        => TryCreateLayout(name, objects, folders, out layout, []);

    public bool TryCreateLayout(string name, IReadOnlyList<ObjectSnapshot> objects, IReadOnlyList<ObjectFolderSnapshot> folders,
        out ObjectLayoutSnapshot layout, IReadOnlyList<SceneGroupSnapshot> groups)
    {
        lock (_stateLock)
        {
            layout = BuildCreatedLayout(name, objects, folders, groups);
            if (!TryValidateLayoutCandidate(layout))
            {
                layout = null!;
                return false;
            }

            if (!_layoutStore.TrySaveLayout(layout))
            {
                layout = null!;
                return false;
            }

            _layouts.Add(layout.Id, layout);
            _layoutOrder.Add(layout.Id);
            _revisionTracker.IncrementSavedLayouts();
            return true;
        }
    }

    public bool TryRenameLayout(Guid id, string name)
    {
        string sanitizedName = TextUtility.TrimOrEmpty(name);
        if (sanitizedName.Length == 0)
        {
            return false;
        }

        return TryUpdateLayout(
            id,
            layout => string.Equals(layout.Name, sanitizedName, StringComparison.Ordinal)
                ? null
                : layout with
                {
                    Name = sanitizedName,
                });
    }

    public bool TryReplaceLayoutObjects(Guid id, IReadOnlyList<ObjectSnapshot> objects)
        => TryUpdateLayout(
            id,
            layout => layout with
            {
                Objects = objects
                    .Select(snapshot => snapshot with { LayoutId = id })
                    .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                    .ToImmutableArray(),
            });

    public PersistentMutationStatus UpsertLayoutObjects(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        lock (_stateLock)
        {
            Dictionary<Guid, Dictionary<Guid, ObjectSnapshot>> updates = [];
            HashSet<Guid> objectIds = [];
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (snapshot.LayoutId is not Guid layoutId || !objectIds.Add(snapshot.Id))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }

                if (!_layouts.ContainsKey(layoutId))
                {
                    return PersistentMutationStatus.NotFound;
                }

                if (!updates.TryGetValue(layoutId, out Dictionary<Guid, ObjectSnapshot>? objects))
                {
                    objects = [];
                    updates.Add(layoutId, objects);
                }

                objects.Add(snapshot.Id, snapshot);
            }

            Dictionary<Guid, ObjectLayoutSnapshot> updatedLayouts = [];
            DateTime now = DateTime.UtcNow;
            foreach ((Guid layoutId, Dictionary<Guid, ObjectSnapshot> replacements) in updates)
            {
                ObjectLayoutSnapshot previous = _layouts[layoutId];
                List<ObjectSnapshot> objects = new(previous.Objects.Count + replacements.Count);
                foreach (ObjectSnapshot snapshot in previous.Objects)
                {
                    objects.Add(replacements.Remove(snapshot.Id, out ObjectSnapshot? replacement) ? replacement : snapshot);
                }

                objects.AddRange(replacements.Values);
                ObjectLayoutSnapshot candidate = previous with
                {
                    Objects = objects.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToImmutableArray(),
                };
                if (LayoutStateMatches(previous, candidate))
                {
                    continue;
                }

                updatedLayouts.Add(layoutId, CreateUpdatedLayout(previous, candidate, now));
            }

            return CommitLayoutBatch(updatedLayouts);
        }
    }

    public PersistentMutationStatus ReplaceLayoutContents(IReadOnlyList<ObjectLayoutContentUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return PersistentMutationStatus.Success;
        }

        lock (_stateLock)
        {
            Dictionary<Guid, ObjectLayoutSnapshot> candidates = [];
            DateTime now = DateTime.UtcNow;
            foreach (ObjectLayoutContentUpdate update in updates)
            {
                if (!_layouts.TryGetValue(update.LayoutId, out ObjectLayoutSnapshot? previous))
                {
                    return PersistentMutationStatus.NotFound;
                }

                ObjectLayoutSnapshot candidate = previous with
                {
                    Objects = update.Objects.Select(snapshot => snapshot with { LayoutId = update.LayoutId })
                        .OrderBy(static snapshot => snapshot.CreatedAtUtc).ToImmutableArray(),
                    Folders = ObjectFolderUtility.OrderFolderEntries(update.Folders),
                    Groups = update.Groups.Select(group => group with { LayoutId = update.LayoutId })
                        .OrderBy(static group => group.CreatedAtUtc).ToImmutableArray(),
                };
                if (!candidates.TryAdd(update.LayoutId, LayoutStateMatches(previous, candidate)
                        ? previous
                        : CreateUpdatedLayout(previous, candidate, now)))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }
            }

            return CommitLayoutBatch(candidates);
        }
    }

    public PersistentMutationStatus RestoreLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts)
    {
        if (layouts.Count == 0)
        {
            return PersistentMutationStatus.Success;
        }

        lock (_stateLock)
        {
            Dictionary<Guid, ObjectLayoutSnapshot> replacements = [];
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                if (!_layouts.ContainsKey(layout.Id))
                {
                    return PersistentMutationStatus.NotFound;
                }

                if (!replacements.TryAdd(layout.Id, layout))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }
            }

            return CommitLayoutBatch(replacements);
        }
    }

    public PersistentMutationStatus AdvanceCollectionDependencyRevisions(
        IReadOnlySet<string> collectionIds,
        out bool enabledLayoutAffected)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);

        lock (_stateLock)
        {
            HashSet<string> normalizedCollectionIds = collectionIds
                .Select(ObjectCollectionKeyUtility.NormalizeCollectionId)
                .Where(static collectionId => collectionId.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalizedCollectionIds.Count == 0)
            {
                enabledLayoutAffected = false;
                return PersistentMutationStatus.Success;
            }

            DateTime now = DateTime.UtcNow;
            List<ObjectSavedLayoutChange> changes = [];
            foreach (Guid layoutId in _layoutOrder)
            {
                ObjectLayoutSnapshot layout = _layouts[layoutId];
                if (!layout.Objects.Any(snapshot => normalizedCollectionIds.Contains(
                        ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId))))
                {
                    continue;
                }

                changes.Add(new ObjectSavedLayoutChange(layout, CreateUpdatedLayout(layout, layout, now)));
            }

            enabledLayoutAffected = changes.Any(change => _enabledLayoutIds.Contains(change.Id));
            PersistentMutationStatus status = CommitLayoutChanges(
                changes, _layoutOrder.ToArray(), _enabledLayoutIds.ToArray(), _creationLayoutId);
            if (status != PersistentMutationStatus.Success)
            {
                enabledLayoutAffected = false;
            }

            return status;
        }
    }

    public bool TrySetWorkspaceLayouts(IReadOnlyList<Guid> enabledLayoutIds, Guid? creationLayoutId)
    {
        lock (_stateLock)
        {
            HashSet<Guid> distinctIds = new(enabledLayoutIds);
            if (distinctIds.Count != enabledLayoutIds.Count || enabledLayoutIds.Any(id => !_layouts.ContainsKey(id)))
            {
                return false;
            }

            if (creationLayoutId.HasValue && !distinctIds.Contains(creationLayoutId.Value))
            {
                return false;
            }

            if (_creationLayoutId == creationLayoutId && _enabledLayoutIds.SequenceEqual(enabledLayoutIds))
            {
                return true;
            }

            if (!StoreWorkspaceLayouts(enabledLayoutIds, creationLayoutId))
            {
                return false;
            }

            _enabledLayoutIds.Clear();
            _enabledLayoutIds.AddRange(enabledLayoutIds);
            _creationLayoutId = creationLayoutId;
            _revisionTracker.IncrementSavedLayouts();
            return true;
        }
    }

    private bool StoreWorkspaceLayouts(IReadOnlyList<Guid> enabledLayoutIds, Guid? creationLayoutId)
        => _configurationService.TryUpdate(configuration =>
        {
            configuration.Layouts.EnabledLayoutIds = [.. enabledLayoutIds];
            configuration.Layouts.CreationLayoutId = creationLayoutId;
        });

    public ObjectLayoutReloadResult ApplySavedLayoutReload(IReadOnlyList<ObjectLayoutSnapshot> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        lock (_stateLock)
        {
            if (!TryValidateLayoutSet(layouts, out Guid conflictingId))
            {
                return new ObjectLayoutReloadResult(
                    ObjectLayoutReloadStatus.IdentityConflict,
                    ActiveLayoutChanged: false,
                    conflictingId);
            }

            if (LayoutSetsMatch(layouts))
            {
                return new ObjectLayoutReloadResult(
                    ObjectLayoutReloadStatus.Applied,
                    ActiveLayoutChanged: false);
            }

            IReadOnlyList<ObjectLayoutSnapshot> previousLayouts = GetEnabledLayouts();
            bool selectionChanged = ReplaceSavedLayouts(layouts);
            bool configurationStored = !selectionChanged
                || StoreWorkspaceLayouts(_enabledLayoutIds, _creationLayoutId);
            IReadOnlyList<ObjectLayoutSnapshot> currentLayouts = GetEnabledLayouts();
            bool activeLayoutChanged = previousLayouts.Count != currentLayouts.Count
                || previousLayouts.Where((layout, index) => !LayoutsMatch(layout, currentLayouts[index])).Any();

            _revisionTracker.IncrementSavedLayouts();
            if (activeLayoutChanged)
            {
                _revisionTracker.Increment(persistentChanged: true);
            }

            return new ObjectLayoutReloadResult(
                configurationStored
                    ? ObjectLayoutReloadStatus.Applied
                    : ObjectLayoutReloadStatus.ConfigurationWriteFailed,
                activeLayoutChanged);
        }
    }

    public PersistentMutationStatus DeleteLayout(Guid id)
    {
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out ObjectLayoutSnapshot? layout))
            {
                return PersistentMutationStatus.NotFound;
            }

            return CommitLayoutChanges([new ObjectSavedLayoutChange(layout, null)],
                _layoutOrder.Where(layoutId => layoutId != id).ToArray(),
                _enabledLayoutIds.Where(layoutId => layoutId != id).ToArray(),
                _creationLayoutId == id ? null : _creationLayoutId);
        }
    }

    public bool HasAnyObjects()
    {
        lock (_stateLock)
        {
            return _layouts.Values.Any(static layout => layout.Objects.Count > 0 || layout.Groups.Count > 0);
        }
    }

    public bool HasAnyLoadedLayouts()
    {
        lock (_stateLock)
        {
            return _enabledLayoutIds.Count > 0 || _temporarySourceStore.GetSources().Count > 0;
        }
    }

    private bool ReplaceSavedLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts)
    {
        lock (_stateLock)
        {
            _layouts.Clear();
            _layoutOrder.Clear();
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                _layouts[layout.Id] = layout;
                _layoutOrder.Add(layout.Id);
            }

            HashSet<Guid> seen = [];
            bool selectionChanged = _enabledLayoutIds.RemoveAll(id => !_layouts.ContainsKey(id) || !seen.Add(id)) > 0;
            if (_creationLayoutId.HasValue && !_enabledLayoutIds.Contains(_creationLayoutId.Value))
            {
                _creationLayoutId = null;
                selectionChanged = true;
            }

            _layoutCounter = Math.Max(_layoutCounter, _layouts.Count);
            return selectionChanged;
        }
    }

    private string SanitizeLayoutName(string name)
    {
        var trimmed = TextUtility.TrimOrEmpty(name);
        if (!string.IsNullOrEmpty(trimmed))
        {
            return trimmed;
        }

        lock (_stateLock)
        {
            _layoutCounter++;
            return $"Layout {_layoutCounter:00}";
        }
    }

    private ObjectLayoutSnapshot BuildCreatedLayout(
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectFolderSnapshot> folders,
        IReadOnlyList<SceneGroupSnapshot> groups)
    {
        var layoutId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ImmutableArray<ObjectSnapshot> layoutObjects = objects
            .Select(snapshot => snapshot with { LayoutId = layoutId })
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToImmutableArray();
        ImmutableArray<SceneGroupSnapshot> layoutGroups = groups.Select(group => group with { LayoutId = layoutId })
            .OrderBy(static group => group.CreatedAtUtc).ToImmutableArray();

        return new ObjectLayoutSnapshot
        {
            Id = layoutId,
            Name = SanitizeLayoutName(name),
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Objects = layoutObjects,
            Groups = layoutGroups,
            Folders = ObjectFolderUtility.ExpandFolderEntries(folders, layoutObjects, layoutGroups),
        };
    }

    private bool TryUpdateLayout(
        Guid id,
        Func<ObjectLayoutSnapshot, ObjectLayoutSnapshot?> createUpdate)
    {
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out ObjectLayoutSnapshot? previousLayout)
                || previousLayout is null)
            {
                return false;
            }

            ObjectLayoutSnapshot? candidate = createUpdate(previousLayout);
            if (candidate is null || LayoutStateMatches(previousLayout, candidate))
            {
                return true;
            }

            return CommitLayoutBatch(new() { [id] = CreateUpdatedLayout(previousLayout, candidate, DateTime.UtcNow) })
                == PersistentMutationStatus.Success;
        }
    }

    private PersistentMutationStatus CommitLayoutBatch(Dictionary<Guid, ObjectLayoutSnapshot> updatedLayouts)
    {
        if (updatedLayouts.Count == 0)
        {
            return PersistentMutationStatus.Success;
        }

        if (!TryValidateLayoutSet(_layoutOrder.Select(id => updatedLayouts.GetValueOrDefault(id, _layouts[id])), out _))
        {
            return PersistentMutationStatus.Conflict;
        }

        ObjectSavedLayoutChange[] changes = updatedLayouts.Values
            .Where(layout => !LayoutsMatch(_layouts[layout.Id], layout))
            .Select(layout => new ObjectSavedLayoutChange(_layouts[layout.Id], layout)).ToArray();
        return CommitLayoutChanges(changes, _layoutOrder.ToArray(), _enabledLayoutIds.ToArray(), _creationLayoutId);
    }

    private static ObjectLayoutSnapshot CreateUpdatedLayout(
        ObjectLayoutSnapshot previous,
        ObjectLayoutSnapshot candidate,
        DateTime updatedAtUtc)
        => candidate with
        {
            Id = previous.Id,
            Revision = checked(previous.Revision + 1),
            CreatedAtUtc = previous.CreatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    private bool TryValidateLayoutCandidate(ObjectLayoutSnapshot candidate)
    {
        IEnumerable<ObjectLayoutSnapshot> layouts = _layoutOrder
            .Where(id => id != candidate.Id)
            .Select(id => _layouts[id])
            .Append(candidate);
        return TryValidateLayoutSet(layouts, out _);
    }

    private bool TryValidateLayoutSet(IEnumerable<ObjectLayoutSnapshot> layouts, out Guid conflictingId)
    {
        IReadOnlyList<ObjectLayoutSnapshot> candidates = layouts as IReadOnlyList<ObjectLayoutSnapshot>
            ?? layouts.ToList();
        HashSet<Guid> layoutIds = [];
        foreach (ObjectLayoutSnapshot layout in candidates)
        {
            if (layout.Id == Guid.Empty || layout.Revision <= 0 || !layoutIds.Add(layout.Id))
            {
                conflictingId = layout.Id;
                return false;
            }
            if (!SceneGroupUtility.TryValidateOwner(layout.Id, layout.Objects, layout.Groups, out _))
            {
                conflictingId = layout.Id;
                return false;
            }
        }

        IEnumerable<IEnumerable<Guid>> ownedObjectIds = candidates
            .Select(static layout => layout.Objects.Select(static snapshot => snapshot.Id)
                .Concat(layout.Groups.Select(static group => group.Id)))
            .Concat(_temporarySourceStore.GetSources()
                .Select(static source => source.RuntimeObjects.Select(static snapshot => snapshot.Id)));
        return SceneIdentityValidation.TryValidate(
            ownedObjectIds,
            out conflictingId);
    }

    private bool LayoutSetsMatch(IReadOnlyList<ObjectLayoutSnapshot> layouts)
    {
        if (_layoutOrder.Count != layouts.Count)
        {
            return false;
        }

        for (int index = 0; index < layouts.Count; ++index)
        {
            if (_layoutOrder[index] != layouts[index].Id
                || !LayoutsMatch(_layouts[_layoutOrder[index]], layouts[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LayoutStateMatches(ObjectLayoutSnapshot? left, ObjectLayoutSnapshot? right)
        => ReferenceEquals(left, right)
           || left is not null
           && right is not null
           && left.Id == right.Id
           && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
           && left.CreatedAtUtc == right.CreatedAtUtc
           && left.Objects.SequenceEqual(right.Objects)
           && left.Groups.SequenceEqual(right.Groups)
           && ObjectFolderUtility.FolderEntriesMatch(left.Folders, right.Folders);

    private static bool LayoutsMatch(ObjectLayoutSnapshot? left, ObjectLayoutSnapshot? right)
        => ReferenceEquals(left, right)
           || left is not null
           && right is not null
           && left.Revision == right.Revision
           && left.UpdatedAtUtc == right.UpdatedAtUtc
           && LayoutStateMatches(left, right);

    private static ObjectTemporaryLayoutSnapshot ToTemporaryLayout(ObjectTemporarySourceSnapshot source)
        => new()
        {
            SourceKey = source.SourceKey,
            SourceSessionId = source.SessionId,
            Name = source.Name,
            Revision = source.Revision,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Objects = source.RuntimeObjects,
        };
}
