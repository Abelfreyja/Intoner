using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores saved layouts and temporary source layouts.
/// </summary>
internal interface IObjectLayoutManager
{
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
    /// Tries to resolve one temporary source layout.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="layout">The resolved temporary layout when found.</param>
    /// <returns>true when the temporary layout exists.</returns>
    bool TryGetTemporaryLayout(string sourceKey, out ObjectTemporaryLayoutSnapshot layout);

    /// <summary>
    /// Gets the current revision for the given temporary source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <returns>The current source revision, or zero when the source is unknown.</returns>
    long GetTemporarySourceRevision(string sourceKey);

    /// <summary>
    /// Tries to resolve one temporary object snapshot.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="objectId">The source object id.</param>
    /// <param name="snapshot">The resolved temporary object snapshot when found.</param>
    /// <returns>true when the temporary object exists.</returns>
    bool TryGetTemporaryObjectSnapshot(string sourceKey, Guid objectId, out ObjectSnapshot snapshot);

    /// <summary>
    /// Gets all layouts currently loaded into the composed object scene.
    /// </summary>
    /// <returns>The loaded layout snapshots.</returns>
    IReadOnlyList<ObjectLoadedLayoutSnapshot> GetLoadedLayouts();

    /// <summary>
    /// Gets the current default layout id.
    /// </summary>
    /// <returns>The default layout id when one is selected.</returns>
    Guid? GetDefaultLayoutId();

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

    /// <summary>
    /// Replaces objects and folder organization for one saved layout in one write.
    /// </summary>
    /// <param name="id">The saved layout id.</param>
    /// <param name="objects">The replacement object list.</param>
    /// <param name="folders">The replacement explicit folder list.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutContent(
        Guid id,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectFolderSnapshot> folders);

    /// <summary>
    /// Restores one saved layout snapshot without changing its metadata.
    /// </summary>
    /// <param name="layout">The exact layout snapshot to restore.</param>
    /// <returns>true when the layout exists and was restored.</returns>
    bool TryRestoreLayout(ObjectLayoutSnapshot layout);

    /// <summary>
    /// Replaces the explicit folders for one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="folders">The replacement folder list.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutFolders(Guid id, IReadOnlyList<ObjectFolderSnapshot> folders);

    /// <summary> advances revisions for saved layouts that reference any supplied object collection </summary>
    /// <param name="collectionIds">the changed collection ids</param>
    /// <param name="defaultLayoutAffected">true when the current default layout references a changed collection</param>
    /// <returns>the durable update status</returns>
    PersistentMutationStatus AdvanceCollectionDependencyRevisions(
        IReadOnlySet<string> collectionIds,
        out bool defaultLayoutAffected);

    /// <summary>
    /// Sets the default local layout.
    /// </summary>
    /// <param name="id">The layout id to load, or null to clear the default layout.</param>
    /// <returns>true when the layout selection was valid.</returns>
    bool TrySetDefaultLayout(Guid? id);

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
    /// Clears object contents and folder metadata from saved layouts.
    /// </summary>
    /// <param name="persistChanges">true when saved layout files should be rewritten with empty contents.</param>
    /// <returns>The clear status.</returns>
    PersistentMutationStatus ClearAllLayoutObjects(bool persistChanges);

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

internal sealed class ObjectLayoutManager : IObjectLayoutManager
{
    private readonly Lock _stateLock;
    private readonly IObjectLayoutStore _layoutStore;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly ITemporarySourceStore _temporarySourceStore;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly Dictionary<Guid, ObjectLayoutSnapshot> _layouts = [];
    private readonly List<Guid> _layoutOrder = [];

    private Guid? _defaultLayoutId;
    private int _layoutCounter;

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

        if (ReplaceSavedLayouts(loadedLayouts, _configurationService.Current.Layouts.DefaultLayoutId))
        {
            _ = _configurationService.TryUpdate(static configuration => configuration.Layouts.DefaultLayoutId = null);
        }
    }

    public IReadOnlyList<ObjectLayoutSnapshot> GetLayouts()
    {
        lock (_stateLock)
        {
            return _layoutOrder
                .Where(static id => id != Guid.Empty)
                .Select(id => _layouts[id])
                .ToList();
        }
    }

    public IReadOnlyList<ObjectTemporaryLayoutSnapshot> GetTemporaryLayouts()
        => _temporarySourceStore.GetSources().Select(ToTemporaryLayout).ToList();

    public bool TryGetTemporaryLayout(string sourceKey, out ObjectTemporaryLayoutSnapshot layout)
    {
        string sanitizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            layout = null!;
            return false;
        }

        if (_temporarySourceStore.TryGetSource(sanitizedSourceKey, out ObjectTemporarySourceSnapshot source))
        {
            layout = ToTemporaryLayout(source);
            return true;
        }

        layout = null!;
        return false;
    }

    public long GetTemporarySourceRevision(string sourceKey)
    {
        string sanitizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            return 0;
        }

        return _temporarySourceStore.GetRevision(sanitizedSourceKey);
    }

    public bool TryGetTemporaryObjectSnapshot(string sourceKey, Guid objectId, out ObjectSnapshot snapshot)
    {
        string sanitizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            snapshot = default!;
            return false;
        }

        if (_temporarySourceStore.TryGetSource(sanitizedSourceKey, out ObjectTemporarySourceSnapshot source))
        {
            Guid mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(sanitizedSourceKey, objectId);
            if (TemporarySourceUtility.TryFindObject(source.RuntimeObjects, mappedObjectId, out ObjectSnapshot foundSnapshot))
            {
                snapshot = foundSnapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public IReadOnlyList<ObjectLoadedLayoutSnapshot> GetLoadedLayouts()
    {
        lock (_stateLock)
        {
            List<ObjectLoadedLayoutSnapshot> loadedLayouts = [];
            if (_defaultLayoutId.HasValue
                && _layouts.TryGetValue(_defaultLayoutId.Value, out var defaultLayout))
            {
                loadedLayouts.Add(new ObjectLoadedLayoutSnapshot
                {
                    Kind = ObjectLoadedLayoutKind.Default,
                    LayoutId = defaultLayout.Id,
                    SourceKey = defaultLayout.Id.ToString("D"),
                    SourceSessionId = Guid.Empty,
                    Name = defaultLayout.Name,
                    Revision = defaultLayout.Revision,
                    UpdatedAtUtc = defaultLayout.UpdatedAtUtc,
                    Objects = defaultLayout.Objects,
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

    public Guid? GetDefaultLayoutId()
    {
        lock (_stateLock)
        {
            return _defaultLayoutId;
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
    {
        lock (_stateLock)
        {
            layout = BuildCreatedLayout(name, objects, folders);
            if (!TryValidateLayoutCandidate(layout, out _))
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
                    .ToList(),
            });

    public bool TryReplaceLayoutFolders(Guid id, IReadOnlyList<ObjectFolderSnapshot> folders)
        => TryUpdateLayout(
            id,
            layout => layout with
            {
                Folders = ObjectFolderUtility.OrderFolderEntries(folders),
            });

    public PersistentMutationStatus AdvanceCollectionDependencyRevisions(
        IReadOnlySet<string> collectionIds,
        out bool defaultLayoutAffected)
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
                defaultLayoutAffected = false;
                return PersistentMutationStatus.Success;
            }

            DateTime now = DateTime.UtcNow;
            List<ObjectLayoutSnapshot> previousLayouts = [];
            List<ObjectLayoutSnapshot> updatedLayouts = [];
            foreach (Guid layoutId in _layoutOrder)
            {
                ObjectLayoutSnapshot layout = _layouts[layoutId];
                if (!layout.Objects.Any(snapshot => normalizedCollectionIds.Contains(
                        ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId))))
                {
                    continue;
                }

                previousLayouts.Add(layout);
                updatedLayouts.Add(CreateUpdatedLayout(layout, layout, now));
            }

            defaultLayoutAffected = _defaultLayoutId.HasValue
                && updatedLayouts.Any(layout => layout.Id == _defaultLayoutId.Value);
            PersistentMutationStatus status = TrySaveLayoutBatch(previousLayouts, updatedLayouts);
            if (status != PersistentMutationStatus.Success)
            {
                defaultLayoutAffected = false;
                return status;
            }

            foreach (ObjectLayoutSnapshot layout in updatedLayouts)
            {
                _layouts[layout.Id] = layout;
            }

            if (updatedLayouts.Count > 0)
            {
                _revisionTracker.IncrementSavedLayouts();
            }

            return PersistentMutationStatus.Success;
        }
    }

    public bool TryReplaceLayoutContent(
        Guid id,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectFolderSnapshot> folders)
        => TryUpdateLayout(
            id,
            layout => layout with
            {
                Objects = objects
                    .Select(snapshot => snapshot with { LayoutId = id })
                    .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                    .ToList(),
                Folders = ObjectFolderUtility.OrderFolderEntries(folders),
            });

    public bool TryRestoreLayout(ObjectLayoutSnapshot layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(layout.Id, out ObjectLayoutSnapshot? previousLayout))
            {
                return false;
            }

            return LayoutsMatch(previousLayout, layout) || TryCommitLayout(layout);
        }
    }

    public bool TrySetDefaultLayout(Guid? id)
    {
        lock (_stateLock)
        {
            if (id.HasValue && !_layouts.ContainsKey(id.Value))
            {
                return false;
            }

            if (_defaultLayoutId == id)
            {
                return true;
            }

            Guid? previousId = _defaultLayoutId;
            _defaultLayoutId = id;
            if (_configurationService.TryUpdate(configuration => configuration.Layouts.DefaultLayoutId = id))
            {
                return true;
            }

            _defaultLayoutId = previousId;
            return false;
        }
    }

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

            Guid? requestedDefaultLayoutId = _defaultLayoutId;
            ObjectLayoutSnapshot? previousDefaultLayout = GetLayoutOrDefault(requestedDefaultLayoutId);
            bool clearedDefaultLayout = ReplaceSavedLayouts(layouts, requestedDefaultLayoutId);
            bool configurationStored = !clearedDefaultLayout
                || _configurationService.TryUpdate(static configuration => configuration.Layouts.DefaultLayoutId = null);
            ObjectLayoutSnapshot? currentDefaultLayout = GetLayoutOrDefault(_defaultLayoutId);
            bool activeLayoutChanged = !LayoutsMatch(previousDefaultLayout, currentDefaultLayout);

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
            if (!_layouts.ContainsKey(id))
            {
                return PersistentMutationStatus.NotFound;
            }

            bool wasDefault = _defaultLayoutId == id;
            if (wasDefault && !TrySetDefaultLayout(null))
            {
                return PersistentMutationStatus.StorageFailed;
            }

            if (!_layoutStore.TryDeleteLayout(id))
            {
                if (wasDefault && !TrySetDefaultLayout(id))
                {
                    return PersistentMutationStatus.RecoveryRequired;
                }

                return PersistentMutationStatus.StorageFailed;
            }

            _layouts.Remove(id);
            _layoutOrder.Remove(id);
            _revisionTracker.IncrementSavedLayouts();
            return PersistentMutationStatus.Success;
        }
    }

    public PersistentMutationStatus ClearAllLayoutObjects(bool persistChanges)
    {
        lock (_stateLock)
        {
            DateTime now = DateTime.UtcNow;
            List<ObjectLayoutSnapshot> previousLayouts = [];
            List<ObjectLayoutSnapshot> updatedLayouts = [];
            foreach (Guid layoutId in _layoutOrder)
            {
                if (!_layouts.TryGetValue(layoutId, out ObjectLayoutSnapshot? layout)
                    || layout.Objects.Count == 0 && layout.Folders.Count == 0)
                {
                    continue;
                }

                previousLayouts.Add(layout);
                updatedLayouts.Add(CreateUpdatedLayout(
                    layout,
                    layout with
                    {
                        Folders = [],
                        Objects = [],
                    },
                    now));
            }

            if (persistChanges)
            {
                PersistentMutationStatus status = TrySaveLayoutBatch(previousLayouts, updatedLayouts);
                if (status != PersistentMutationStatus.Success)
                {
                    return status;
                }
            }

            foreach (ObjectLayoutSnapshot layout in updatedLayouts)
            {
                _layouts[layout.Id] = layout;
            }

            if (updatedLayouts.Count > 0)
            {
                _revisionTracker.IncrementSavedLayouts();
            }

            return PersistentMutationStatus.Success;
        }
    }

    public bool HasAnyObjects()
    {
        lock (_stateLock)
        {
            return _layouts.Values.Any(static layout => layout.Objects.Count > 0);
        }
    }

    public bool HasAnyLoadedLayouts()
    {
        lock (_stateLock)
        {
            return _defaultLayoutId.HasValue || _temporarySourceStore.GetSources().Count > 0;
        }
    }

    private bool ReplaceSavedLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? requestedDefaultLayoutId)
    {
        bool clearedDefaultLayout = false;
        lock (_stateLock)
        {
            _layouts.Clear();
            _layoutOrder.Clear();
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                _layouts[layout.Id] = layout;
                _layoutOrder.Add(layout.Id);
            }

            if (requestedDefaultLayoutId.HasValue && _layouts.ContainsKey(requestedDefaultLayoutId.Value))
            {
                _defaultLayoutId = requestedDefaultLayoutId;
            }
            else
            {
                clearedDefaultLayout = _defaultLayoutId.HasValue || requestedDefaultLayoutId.HasValue;
                _defaultLayoutId = null;
            }

            _layoutCounter = Math.Max(_layoutCounter, _layouts.Count);
        }

        return clearedDefaultLayout;
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
        IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        var layoutId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        List<ObjectSnapshot> layoutObjects = objects
            .Select(snapshot => snapshot with { LayoutId = layoutId })
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
        IReadOnlyList<ObjectFolderSnapshot> orderedFolders = ObjectFolderUtility.OrderFolderEntries(
            folders.Concat(layoutObjects.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath))));

        return new ObjectLayoutSnapshot
        {
            Id = layoutId,
            Name = SanitizeLayoutName(name),
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Objects = layoutObjects,
            Folders = orderedFolders,
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

            return TryCommitLayout(CreateUpdatedLayout(previousLayout, candidate, DateTime.UtcNow));
        }
    }

    private bool TryCommitLayout(ObjectLayoutSnapshot layout)
    {
        if (!TryValidateLayoutCandidate(layout, out _)
            || !_layoutStore.TrySaveLayout(layout))
        {
            return false;
        }

        _layouts[layout.Id] = layout;
        _revisionTracker.IncrementSavedLayouts();
        return true;
    }

    private PersistentMutationStatus TrySaveLayoutBatch(
        IReadOnlyList<ObjectLayoutSnapshot> previousLayouts,
        IReadOnlyList<ObjectLayoutSnapshot> updatedLayouts)
    {
        for (int index = 0; index < updatedLayouts.Count; ++index)
        {
            if (_layoutStore.TrySaveLayout(updatedLayouts[index]))
            {
                continue;
            }

            bool restored = true;
            for (int restoreIndex = index - 1; restoreIndex >= 0; --restoreIndex)
            {
                if (!_layoutStore.TrySaveLayout(previousLayouts[restoreIndex]))
                {
                    restored = false;
                }
            }

            return restored
                ? PersistentMutationStatus.StorageFailed
                : PersistentMutationStatus.RecoveryRequired;
        }

        return PersistentMutationStatus.Success;
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

    private bool TryValidateLayoutCandidate(ObjectLayoutSnapshot candidate, out Guid conflictingId)
    {
        IEnumerable<ObjectLayoutSnapshot> layouts = _layoutOrder
            .Where(id => id != candidate.Id)
            .Select(id => _layouts[id])
            .Append(candidate);
        return TryValidateLayoutSet(layouts, out conflictingId);
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
        }

        IEnumerable<IEnumerable<Guid>> ownedObjectIds = candidates
            .Select(static layout => layout.Objects.Select(static snapshot => snapshot.Id))
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

    private ObjectLayoutSnapshot? GetLayoutOrDefault(Guid? id)
        => id.HasValue && TryGetLayout(id.Value, out ObjectLayoutSnapshot layout)
            ? layout
            : null;

    private static bool LayoutStateMatches(ObjectLayoutSnapshot? left, ObjectLayoutSnapshot? right)
        => ReferenceEquals(left, right)
           || left is not null
           && right is not null
           && left.Id == right.Id
           && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
           && left.CreatedAtUtc == right.CreatedAtUtc
           && left.Objects.SequenceEqual(right.Objects)
           && ObjectFolderUtility.FolderEntriesMatch(left.Folders, right.Folders);

    private static bool LayoutsMatch(ObjectLayoutSnapshot? left, ObjectLayoutSnapshot? right)
        => ReferenceEquals(left, right)
           || left is not null
           && right is not null
           && LayoutStateMatches(left, right)
           && left.Revision == right.Revision
           && left.UpdatedAtUtc == right.UpdatedAtUtc;

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

