using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Provides read only object queries across multiple states.
/// </summary>
internal interface IObjectSceneView
{
    /// <summary>
    /// Gets the currently active object snapshots.
    /// </summary>
    /// <returns>The active object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetObjectSnapshots();

    /// <summary>
    /// Gets all persisted local object snapshots.
    /// </summary>
    /// <returns>The persisted local object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetPlacedObjectSnapshots();

    /// <summary>
    /// Gets all explicit and object included folders for the current persisted scene.
    /// </summary>
    /// <returns>The ordered folder paths for the current persisted scene.</returns>
    IReadOnlyList<string> GetPlacedFolders();

    /// <summary>
    /// Gets persisted standalone objects that are not part of a layout.
    /// </summary>
    /// <returns>The standalone object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetStandaloneObjectSnapshots();

    /// <summary>
    /// Gets the full composed object scene.
    /// </summary>
    /// <returns>The composed scene object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetSceneObjectSnapshots();

    /// <summary>
    /// Gets the current bounds snapshots for active objects.
    /// </summary>
    /// <returns>The active object bounds snapshots.</returns>
    IReadOnlyList<ObjectBoundsSnapshot> GetObjectBoundsSnapshots();

    /// <summary>
    /// Gets the runtime state snapshots for the composed object scene.
    /// </summary>
    /// <returns>The runtime state snapshots.</returns>
    IReadOnlyList<ObjectRuntimeStateSnapshot> GetRuntimeStateSnapshots();

    /// <summary>
    /// Gets the current creation context for new local objects.
    /// </summary>
    /// <returns>The current object creation context.</returns>
    ObjectCreationContext GetCurrentLocationContext();

    /// <summary>
    /// Gets the full composed-scene revision.
    /// </summary>
    /// <returns>The current scene revision.</returns>
    long GetSceneRevision();

    /// <summary>
    /// Gets the persistent-scene revision for standalone objects and the default layout.
    /// </summary>
    /// <returns>The current persistent scene revision.</returns>
    long GetPersistentSceneRevision();

    /// <summary>
    /// Checks whether any persisted local objects or layouts exist.
    /// </summary>
    /// <returns>true when any persisted local object state exists.</returns>
    bool HasPersistedObjects();

    /// <summary>
    /// Tries to resolve one persisted local object snapshot.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved persisted object snapshot when found.</param>
    /// <returns>true when the object exists in persisted local state.</returns>
    bool TryGetPersistedObjectSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve one scene object snapshot from the composed scene.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved scene object snapshot when found.</param>
    /// <returns>true when the scene contains that object.</returns>
    bool TryGetSceneObjectSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve one runtime state snapshot.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved runtime state snapshot when found.</param>
    /// <returns>true when the scene contains that object.</returns>
    bool TryGetRuntimeStateSnapshot(Guid id, out ObjectRuntimeStateSnapshot snapshot);
}

internal sealed class ObjectSceneView : IObjectSceneView
{
    private readonly IObjectScene                  _scene;
    private readonly IObjectSceneSnapshotResolver  _snapshotResolver;
    private readonly IObjectFolderService          _objectFolderService;
    private readonly IObjectPersistenceState       _persistenceState;
    private readonly IObjectRevisionTracker        _revisionTracker;
    private readonly IObjectRuntimeLocationService _locationService;

    public ObjectSceneView(
        IObjectScene scene,
        IObjectSceneSnapshotResolver snapshotResolver,
        IObjectFolderService objectFolderService,
        IObjectPersistenceState persistenceState,
        IObjectRevisionTracker revisionTracker,
        IObjectRuntimeLocationService locationService)
    {
        _scene = scene;
        _snapshotResolver = snapshotResolver;
        _objectFolderService = objectFolderService;
        _persistenceState = persistenceState;
        _revisionTracker = revisionTracker;
        _locationService = locationService;
    }

    public IReadOnlyList<ObjectSnapshot> GetObjectSnapshots()
        => _scene.GetObjectSnapshots();

    public IReadOnlyList<ObjectSnapshot> GetPlacedObjectSnapshots()
        => _persistenceState.GetPersistedSnapshots();

    public IReadOnlyList<string> GetPlacedFolders()
        => _objectFolderService.GetSceneFolders(GetPlacedObjectSnapshots());

    public IReadOnlyList<ObjectSnapshot> GetStandaloneObjectSnapshots()
        => _persistenceState.GetStandaloneSnapshots();

    public IReadOnlyList<ObjectSnapshot> GetSceneObjectSnapshots()
        => _snapshotResolver.GetSceneSnapshots();

    public IReadOnlyList<ObjectBoundsSnapshot> GetObjectBoundsSnapshots()
        => _scene.GetBoundsSnapshots();

    public IReadOnlyList<ObjectRuntimeStateSnapshot> GetRuntimeStateSnapshots()
        => _scene.GetRuntimeStateSnapshots(_snapshotResolver.GetSceneSnapshots());

    public ObjectCreationContext GetCurrentLocationContext()
        => _locationService.GetCurrentCreationContext();

    public long GetSceneRevision()
        => _revisionTracker.GetSceneRevision();

    public long GetPersistentSceneRevision()
        => _revisionTracker.GetPersistentSceneRevision();

    public bool HasPersistedObjects()
        => _persistenceState.HasPersistedObjects();

    public bool TryGetPersistedObjectSnapshot(Guid id, out ObjectSnapshot snapshot)
        => _persistenceState.TryGetPersistedSnapshot(id, out snapshot);

    public bool TryGetSceneObjectSnapshot(Guid id, out ObjectSnapshot snapshot)
    {
        if (_scene.TryGetObjectSnapshot(id, out snapshot))
        {
            return true;
        }

        return _snapshotResolver.TryGetSceneSnapshot(id, out snapshot);
    }

    public bool TryGetRuntimeStateSnapshot(Guid id, out ObjectRuntimeStateSnapshot snapshot)
        => _scene.TryGetRuntimeStateSnapshot(id, _snapshotResolver.GetSceneSnapshots(), out snapshot);
}

