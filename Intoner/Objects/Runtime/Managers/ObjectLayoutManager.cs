using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores saved layouts and temporary source layouts.
/// </summary>
internal interface IObjectLayoutManager
{
    /// <summary>
    /// Raised when saved layout files are reloaded from disk.
    /// </summary>
    event Action SavedLayoutsReloaded;

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
    /// <returns>The created layout snapshot.</returns>
    ObjectLayoutSnapshot CreateLayout(string name);

    /// <summary>
    /// Creates a new saved layout with initial object and folder contents.
    /// </summary>
    /// <param name="name">the requested layout name.</param>
    /// <param name="objects">the initial layout objects.</param>
    /// <param name="folders">the initial folders.</param>
    /// <param name="folderColors">the initial folder color map.</param>
    /// <returns>the created layout snapshot.</returns>
    ObjectLayoutSnapshot CreateLayout(string name, IReadOnlyList<ObjectSnapshot> objects, IReadOnlyList<string> folders, IReadOnlyDictionary<string, string> folderColors);

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
    /// Replaces the explicit folders for one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="folders">The replacement folder list.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutFolders(Guid id, IReadOnlyList<string> folders);

    /// <summary>
    /// Replaces the explicit folder colors for one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="folderColors">The replacement folder color map.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutFolderColors(Guid id, IReadOnlyDictionary<string, string> folderColors);

    /// <summary>
    /// Replaces explicit folders and folder colors for one saved layout in one write.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <param name="folders">the replacement folder list.</param>
    /// <param name="folderColors">the replacement folder color map.</param>
    /// <returns>true when the layout exists and was updated.</returns>
    bool TryReplaceLayoutFolderState(Guid id, IReadOnlyList<string> folders, IReadOnlyDictionary<string, string> folderColors);

    /// <summary>
    /// Sets the default local layout.
    /// </summary>
    /// <param name="id">The layout id to load, or null to clear the default layout.</param>
    /// <returns>true when the layout selection was valid.</returns>
    bool TrySetDefaultLayout(Guid? id);

    /// <summary>
    /// Replaces one temporary source layout with a full snapshot.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="objects">The replacement temporary objects.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary layout mutation.</returns>
    ObjectTemporaryMutationResult TryApplyTemporaryLayout(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectSnapshot> objects, long revision);

    /// <summary>
    /// Applies a batched temporary change set for one source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="changes">The ordered changes to apply.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary mutation batch.</returns>
    ObjectTemporaryMutationResult TryApplyTemporaryChanges(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectTemporaryChange> changes, long revision);

    /// <summary>
    /// Creates or updates one temporary object for a source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="snapshot">The incoming temporary object snapshot.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <param name="appliedSnapshot">The remapped temporary snapshot that was stored.</param>
    /// <returns>The result of the temporary object mutation.</returns>
    ObjectTemporaryMutationResult TryUpsertTemporaryObject(string sourceKey, Guid sessionId, string name, ObjectSnapshot snapshot, long revision, out ObjectSnapshot appliedSnapshot);

    /// <summary>
    /// Applies a partial update to one temporary object.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="objectId">The source object id.</param>
    /// <param name="patch">The partial object patch to apply.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <param name="appliedSnapshot">The remapped temporary snapshot after patching.</param>
    /// <returns>The result of the temporary object mutation.</returns>
    ObjectTemporaryMutationResult TryPatchTemporaryObject(string sourceKey, Guid sessionId, string name, Guid objectId, ObjectSnapshotPatch patch, long revision, out ObjectSnapshot appliedSnapshot);

    /// <summary>
    /// Removes one temporary object from a source layout.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="objectId">The source object id.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <param name="mappedObjectId">The remapped local temporary object id when the object existed.</param>
    /// <returns>The result of the temporary object removal.</returns>
    ObjectTemporaryMutationResult TryRemoveTemporaryObject(string sourceKey, Guid sessionId, Guid objectId, long revision, out Guid mappedObjectId);

    /// <summary>
    /// Removes one temporary source layout entirely.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary layout removal.</returns>
    ObjectTemporaryMutationResult TryRemoveTemporaryLayout(string sourceKey, Guid sessionId, long revision);

    /// <summary>
    /// Deletes one saved layout.
    /// </summary>
    /// <param name="id">The layout id.</param>
    /// <returns>true when the layout existed and was deleted.</returns>
    bool TryDeleteLayout(Guid id);

    /// <summary>
    /// Clears object contents and folder metadata from saved layouts.
    /// </summary>
    /// <param name="persistChanges">true when saved layout files should be rewritten with empty contents.</param>
    void ClearAllLayoutObjects(bool persistChanges);

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

internal sealed class ObjectLayoutManager : IObjectLayoutManager, IDisposable
{
    private readonly record struct TemporaryWriteContext(
        string SourceKey,
        ObjectTemporaryLayoutSnapshot? ExistingLayout,
        ObjectTemporarySourceState CurrentState,
        bool ResetSource);

    private readonly Lock _stateLock = new();
    private readonly IObjectLayoutStore _layoutStore;
    private readonly IObjectConfigurationService _configurationService;
    private readonly Dictionary<Guid, ObjectLayoutSnapshot> _layouts = [];
    private readonly Dictionary<string, ObjectTemporaryLayoutSnapshot> _temporaryLayouts = [];
    private readonly Dictionary<string, ObjectTemporarySourceState> _temporarySourceStates = [];
    private readonly List<Guid> _layoutOrder = [];
    private readonly List<string> _temporaryLayoutOrder = [];

    private Guid? _defaultLayoutId;
    private int _layoutCounter;
    private Action? _savedLayoutsReloaded;
    private bool _disposed;

    public ObjectLayoutManager(
        IObjectLayoutStore layoutStore,
        IObjectConfigurationService configurationService)
    {
        _layoutStore = layoutStore;
        _configurationService = configurationService;

        if (ReplaceSavedLayouts(_layoutStore.LoadLayouts(), _configurationService.Current.Layouts.DefaultLayoutId))
        {
            _configurationService.Update(static configuration => configuration.Layouts.DefaultLayoutId = null);
        }

        _layoutStore.LayoutFilesChanged += HandleLayoutFilesChanged;
    }

    public event Action SavedLayoutsReloaded
    {
        add => _savedLayoutsReloaded += value;
        remove => _savedLayoutsReloaded -= value;
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
    {
        lock (_stateLock)
        {
            return _temporaryLayoutOrder
                .Select(key => _temporaryLayouts[key])
                .ToList();
        }
    }

    public bool TryGetTemporaryLayout(string sourceKey, out ObjectTemporaryLayoutSnapshot layout)
    {
        var sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            layout = null!;
            return false;
        }

        lock (_stateLock)
        {
            return _temporaryLayouts.TryGetValue(sanitizedSourceKey, out layout!);
        }
    }

    public long GetTemporarySourceRevision(string sourceKey)
    {
        var sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            return 0;
        }

        lock (_stateLock)
        {
            return ResolveCurrentTemporarySourceState(
                sanitizedSourceKey,
                _temporaryLayouts.GetValueOrDefault(sanitizedSourceKey)).Revision;
        }
    }

    public bool TryGetTemporaryObjectSnapshot(string sourceKey, Guid objectId, out ObjectSnapshot snapshot)
    {
        var sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            snapshot = default!;
            return false;
        }

        lock (_stateLock)
        {
            if (_temporaryLayouts.TryGetValue(sanitizedSourceKey, out var layout))
            {
                var mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(sanitizedSourceKey, objectId);
                if (ObjectTemporaryLayoutUtility.TryFindObject(layout.Objects, mappedObjectId, out var foundSnapshot))
                {
                    snapshot = foundSnapshot;
                    return true;
                }
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
                    Revision = 0,
                    UpdatedAtUtc = defaultLayout.UpdatedAtUtc,
                    Objects = defaultLayout.Objects,
                });
            }

            loadedLayouts.AddRange(_temporaryLayoutOrder
                .Where(_temporaryLayouts.ContainsKey)
                .Select(key => _temporaryLayouts[key])
                .Select(layout => new ObjectLoadedLayoutSnapshot
                {
                    Kind = ObjectLoadedLayoutKind.Temporary,
                    LayoutId = null,
                    SourceKey = layout.SourceKey,
                    SourceSessionId = layout.SourceSessionId,
                    Name = layout.Name,
                    Revision = layout.Revision,
                    UpdatedAtUtc = layout.UpdatedAtUtc,
                    Objects = layout.Objects,
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

    public ObjectLayoutSnapshot CreateLayout(string name)
        => CreateLayout(name, [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public ObjectLayoutSnapshot CreateLayout(
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors)
    {
        ObjectLayoutSnapshot layout = BuildCreatedLayout(name, objects, folders, folderColors);
        AddLayout(layout);
        return layout;
    }

    public bool TryRenameLayout(Guid id, string name)
    {
        string sanitizedName = ObjectStringUtility.TrimOrEmpty(name);
        if (sanitizedName.Length == 0)
        {
            return false;
        }

        ObjectLayoutSnapshot updatedLayout;
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out ObjectLayoutSnapshot? layout))
            {
                return false;
            }

            if (string.Equals(layout.Name, sanitizedName, StringComparison.Ordinal))
            {
                return true;
            }

            updatedLayout = layout with
            {
                Name = sanitizedName,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _layouts[id] = updatedLayout;
        }

        _layoutStore.SaveLayout(updatedLayout);
        return true;
    }

    public bool TryReplaceLayoutObjects(Guid id, IReadOnlyList<ObjectSnapshot> objects)
    {
        ObjectLayoutSnapshot updatedLayout;
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out var layout))
            {
                return false;
            }

            updatedLayout = layout with
            {
                Objects = objects
                    .Select(snapshot => snapshot with { LayoutId = id })
                    .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                    .ToList(),
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _layouts[id] = updatedLayout;
        }

        _layoutStore.SaveLayout(updatedLayout);
        return true;
    }

    public bool TryReplaceLayoutFolderState(Guid id, IReadOnlyList<string> folders, IReadOnlyDictionary<string, string> folderColors)
    {
        ObjectLayoutSnapshot updatedLayout;
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out var layout))
            {
                return false;
            }

            IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(folders);
            IReadOnlyDictionary<string, string> orderedFolderColors = ObjectFolderUtility.OrderFolderColorMap(folderColors, orderedFolders);
            if (ObjectFolderUtility.FolderListsMatch(layout.Folders, orderedFolders)
                && ObjectFolderUtility.FolderColorMapsMatch(layout.FolderColors, orderedFolderColors))
            {
                return true;
            }

            updatedLayout = layout with
            {
                Folders = orderedFolders,
                FolderColors = orderedFolderColors,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _layouts[id] = updatedLayout;
        }

        _layoutStore.SaveLayout(updatedLayout);
        return true;
    }

    public bool TryReplaceLayoutFolders(Guid id, IReadOnlyList<string> folders)
    {
        ObjectLayoutSnapshot updatedLayout;
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out var layout))
            {
                return false;
            }

            var orderedFolders = ObjectFolderUtility.OrderFolders(folders);
            var orderedFolderColors = ObjectFolderUtility.OrderFolderColorMap(layout.FolderColors, orderedFolders);
            if (ObjectFolderUtility.FolderListsMatch(layout.Folders, orderedFolders)
                && ObjectFolderUtility.FolderColorMapsMatch(layout.FolderColors, orderedFolderColors))
            {
                return true;
            }

            updatedLayout = layout with
            {
                Folders = orderedFolders,
                FolderColors = orderedFolderColors,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _layouts[id] = updatedLayout;
        }

        _layoutStore.SaveLayout(updatedLayout);
        return true;
    }

    public bool TryReplaceLayoutFolderColors(Guid id, IReadOnlyDictionary<string, string> folderColors)
    {
        ObjectLayoutSnapshot updatedLayout;
        lock (_stateLock)
        {
            if (!_layouts.TryGetValue(id, out var layout))
            {
                return false;
            }

            var orderedFolderColors = ObjectFolderUtility.OrderFolderColorMap(folderColors, layout.Folders);
            if (ObjectFolderUtility.FolderColorMapsMatch(layout.FolderColors, orderedFolderColors))
            {
                return true;
            }

            updatedLayout = layout with
            {
                FolderColors = orderedFolderColors,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _layouts[id] = updatedLayout;
        }

        _layoutStore.SaveLayout(updatedLayout);
        return true;
    }

    public bool TrySetDefaultLayout(Guid? id)
    {
        lock (_stateLock)
        {
            if (id.HasValue && !_layouts.ContainsKey(id.Value))
            {
                return false;
            }

            _defaultLayoutId = id;
        }

        _configurationService.Update(configuration => configuration.Layouts.DefaultLayoutId = id);
        return true;
    }

    public ObjectTemporaryMutationResult TryApplyTemporaryLayout(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectSnapshot> objects, long revision)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: true,
                    out var context,
                    out var error))
            {
                return error;
            }

            return CommitTemporaryLayout(
                context,
                sessionId,
                name,
                ObjectTemporaryLayoutUtility.OrderObjects(objects.Select(snapshot => ObjectTemporaryLayoutUtility.RemapSnapshot(context.SourceKey, snapshot))),
                revision);
        }
    }

    public ObjectTemporaryMutationResult TryApplyTemporaryChanges(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectTemporaryChange> changes, long revision)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: true,
                    out var context,
                    out var error))
            {
                return error;
            }

            var nextObjects = ObjectTemporaryLayoutUtility.CreateObjectMap(context.ExistingLayout?.Objects);

            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case ObjectTemporaryChangeKind.Upsert when change.Snapshot is not null:
                        var remappedSnapshot = ObjectTemporaryLayoutUtility.RemapSnapshot(context.SourceKey, change.Snapshot);
                        nextObjects[remappedSnapshot.Id] = remappedSnapshot;
                        break;
                    case ObjectTemporaryChangeKind.Patch when change.Patch is not null:
                        var mappedPatchObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(context.SourceKey, change.ObjectId);
                        if (!nextObjects.TryGetValue(mappedPatchObjectId, out var existingSnapshot))
                        {
                            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, context.CurrentState.Revision);
                        }

                        nextObjects[mappedPatchObjectId] = ObjectSnapshotUtility.ApplyPatch(existingSnapshot, change.Patch);
                        break;
                    case ObjectTemporaryChangeKind.Remove:
                        var mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(context.SourceKey, change.ObjectId);
                        if (!nextObjects.Remove(mappedObjectId) && !context.ResetSource)
                        {
                            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, context.CurrentState.Revision);
                        }

                        break;
                    default:
                        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, context.CurrentState.Revision);
                }
            }

            return CommitTemporaryLayout(
                context,
                sessionId,
                name,
                ObjectTemporaryLayoutUtility.OrderObjects(nextObjects.Values),
                revision);
        }
    }

    public ObjectTemporaryMutationResult TryUpsertTemporaryObject(string sourceKey, Guid sessionId, string name, ObjectSnapshot snapshot, long revision, out ObjectSnapshot appliedSnapshot)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: true,
                    out var context,
                    out var error))
            {
                appliedSnapshot = default!;
                return error;
            }

            var remappedSnapshot = ObjectTemporaryLayoutUtility.RemapSnapshot(context.SourceKey, snapshot);
            appliedSnapshot = remappedSnapshot;
            return CommitTemporaryLayout(
                context,
                sessionId,
                name,
                ObjectTemporaryLayoutUtility.ReplaceObject(context.ExistingLayout?.Objects, remappedSnapshot),
                revision);
        }
    }

    public ObjectTemporaryMutationResult TryPatchTemporaryObject(string sourceKey, Guid sessionId, string name, Guid objectId, ObjectSnapshotPatch patch, long revision, out ObjectSnapshot appliedSnapshot)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: true,
                    out var context,
                    out var error))
            {
                appliedSnapshot = default!;
                return error;
            }

            var mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(context.SourceKey, objectId);
            if (!ObjectTemporaryLayoutUtility.TryFindObject(context.ExistingLayout?.Objects, mappedObjectId, out var existingSnapshot))
            {
                appliedSnapshot = default!;
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, context.CurrentState.Revision);
            }

            appliedSnapshot = ObjectSnapshotUtility.ApplyPatch(existingSnapshot, patch);
            return CommitTemporaryLayout(
                context,
                sessionId,
                name,
                ObjectTemporaryLayoutUtility.ReplaceObject(context.ExistingLayout?.Objects, appliedSnapshot),
                revision);
        }
    }

    public ObjectTemporaryMutationResult TryRemoveTemporaryObject(string sourceKey, Guid sessionId, Guid objectId, long revision, out Guid mappedObjectId)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: true,
                    out var context,
                    out var error))
            {
                mappedObjectId = Guid.Empty;
                return error;
            }

            mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(context.SourceKey, objectId);
            if (!ObjectTemporaryLayoutUtility.TryFindObject(context.ExistingLayout?.Objects, mappedObjectId, out _)
                && !context.ResetSource)
            {
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, context.CurrentState.Revision);
            }

            return CommitTemporaryLayout(
                context,
                sessionId,
                string.Empty,
                ObjectTemporaryLayoutUtility.RemoveObject(context.ExistingLayout?.Objects, mappedObjectId),
                revision);
        }
    }

    public ObjectTemporaryMutationResult TryRemoveTemporaryLayout(string sourceKey, Guid sessionId, long revision)
    {
        lock (_stateLock)
        {
            if (!TryPrepareTemporaryWrite(
                    sourceKey,
                    sessionId,
                    revision,
                    clearExistingLayoutOnNewSession: false,
                    out var context,
                    out var error))
            {
                return error;
            }

            if (context.ExistingLayout is null && context.CurrentState.Revision == 0)
            {
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, 0);
            }

            if (context.ExistingLayout is not null)
            {
                _temporaryLayouts.Remove(context.SourceKey);
                _temporaryLayoutOrder.Remove(context.SourceKey);
            }

            return CommitTemporarySourceState(context, sessionId, revision);
        }
    }

    public bool TryDeleteLayout(Guid id)
    {
        lock (_stateLock)
        {
            if (!_layouts.Remove(id))
            {
                return false;
            }

            _layoutOrder.Remove(id);
            if (_defaultLayoutId == id)
            {
                _defaultLayoutId = null;
            }
        }

        _layoutStore.DeleteLayout(id);
        if (_configurationService.Current.Layouts.DefaultLayoutId == id)
        {
            _configurationService.Update(static configuration => configuration.Layouts.DefaultLayoutId = null);
        }

        return true;
    }

    public void ClearAllLayoutObjects(bool persistChanges)
    {
        var now = DateTime.UtcNow;
        List<ObjectLayoutSnapshot> updatedLayouts = [];

        lock (_stateLock)
        {
            foreach (var layoutId in _layoutOrder)
            {
                if (!_layouts.TryGetValue(layoutId, out var layout)
                    || layout.Objects.Count == 0 && layout.Folders.Count == 0 && layout.FolderColors.Count == 0)
                {
                    continue;
                }

                _layouts[layoutId] = layout with
                {
                    Folders = [],
                    FolderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Objects = [],
                    UpdatedAtUtc = now,
                };
                updatedLayouts.Add(_layouts[layoutId]);
            }
        }

        if (!persistChanges)
        {
            return;
        }

        foreach (ObjectLayoutSnapshot layout in updatedLayouts)
        {
            _layoutStore.SaveLayout(layout);
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
            return _defaultLayoutId.HasValue || _temporaryLayouts.Count > 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _layoutStore.LayoutFilesChanged -= HandleLayoutFilesChanged;
    }

    private void HandleLayoutFilesChanged()
    {
        Guid? requestedDefaultLayoutId = GetDefaultLayoutId();
        bool clearedDefaultLayout = ReplaceSavedLayouts(_layoutStore.LoadLayouts(), requestedDefaultLayoutId);
        if (clearedDefaultLayout)
        {
            _configurationService.Update(static configuration => configuration.Layouts.DefaultLayoutId = null);
        }

        _savedLayoutsReloaded?.Invoke();
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
        var trimmed = ObjectStringUtility.TrimOrEmpty(name);
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

    private void AddLayout(ObjectLayoutSnapshot layout)
    {
        lock (_stateLock)
        {
            _layouts[layout.Id] = layout;
            _layoutOrder.Add(layout.Id);
        }

        _layoutStore.SaveLayout(layout);
    }

    private ObjectLayoutSnapshot BuildCreatedLayout(
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors)
    {
        var layoutId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        List<ObjectSnapshot> layoutObjects = objects
            .Select(snapshot => snapshot with { LayoutId = layoutId })
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
        IReadOnlyList<string> orderedFolders = ObjectFolderUtility.OrderFolders(
            folders.Concat(layoutObjects.Select(static snapshot => snapshot.FolderPath)));

        return new ObjectLayoutSnapshot
        {
            Id = layoutId,
            Name = SanitizeLayoutName(name),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Objects = layoutObjects,
            Folders = orderedFolders,
            FolderColors = ObjectFolderUtility.OrderFolderColorMap(folderColors, orderedFolders),
        };
    }

    private bool TryPrepareTemporaryWrite(
        string sourceKey,
        Guid sessionId,
        long revision,
        bool clearExistingLayoutOnNewSession,
        out TemporaryWriteContext context,
        out ObjectTemporaryMutationResult error)
    {
        var sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (string.IsNullOrEmpty(sanitizedSourceKey))
        {
            context = default;
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidSource, 0);
            return false;
        }

        _temporaryLayouts.TryGetValue(sanitizedSourceKey, out var existingLayout);
        var currentState = ResolveCurrentTemporarySourceState(sanitizedSourceKey, existingLayout);
        var resetSource = ObjectTemporarySourceUtility.IsNewSession(currentState.SessionId, sessionId);
        if (resetSource)
        {
            if (clearExistingLayoutOnNewSession)
            {
                existingLayout = null;
            }

            currentState = new ObjectTemporarySourceState(sessionId, 0);
        }

        if (ObjectTemporarySourceUtility.IsStaleRevision(currentState.Revision, revision))
        {
            context = default;
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.StaleRevision, currentState.Revision);
            return false;
        }

        context = new TemporaryWriteContext(
            sanitizedSourceKey,
            existingLayout,
            currentState,
            resetSource);
        error = default;
        return true;
    }

    private ObjectTemporarySourceState ResolveCurrentTemporarySourceState(string sourceKey, ObjectTemporaryLayoutSnapshot? existingLayout)
        => existingLayout is not null
            ? new ObjectTemporarySourceState(existingLayout.SourceSessionId, existingLayout.Revision)
            : _temporarySourceStates.GetValueOrDefault(sourceKey);

    private ObjectTemporaryMutationResult CommitTemporaryLayout(
        TemporaryWriteContext context,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        long revision)
    {
        var nextSessionId = ObjectTemporarySourceUtility.ResolveSessionId(context.CurrentState.SessionId, sessionId);
        var nextRevision = ObjectTemporarySourceUtility.ResolveRevision(context.CurrentState.Revision, revision);
        EnsureTemporaryLayoutOrder(context.SourceKey);
        _temporaryLayouts[context.SourceKey] = new ObjectTemporaryLayoutSnapshot
        {
            SourceKey = context.SourceKey,
            SourceSessionId = nextSessionId,
            Name = ObjectTemporarySourceUtility.ResolveName(context.ExistingLayout?.Name, name, context.SourceKey),
            Revision = nextRevision,
            UpdatedAtUtc = DateTime.UtcNow,
            Objects = objects,
        };
        _temporarySourceStates[context.SourceKey] = new ObjectTemporarySourceState(nextSessionId, nextRevision);
        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, nextRevision);
    }

    private ObjectTemporaryMutationResult CommitTemporarySourceState(
        TemporaryWriteContext context,
        Guid sessionId,
        long revision)
    {
        var nextSessionId = ObjectTemporarySourceUtility.ResolveSessionId(context.CurrentState.SessionId, sessionId);
        var nextRevision = ObjectTemporarySourceUtility.ResolveRevision(context.CurrentState.Revision, revision);
        _temporarySourceStates[context.SourceKey] = new ObjectTemporarySourceState(nextSessionId, nextRevision);
        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, nextRevision);
    }

    private void EnsureTemporaryLayoutOrder(string sourceKey)
    {
        if (!_temporaryLayouts.ContainsKey(sourceKey))
        {
            _temporaryLayoutOrder.Add(sourceKey);
        }
    }

}

