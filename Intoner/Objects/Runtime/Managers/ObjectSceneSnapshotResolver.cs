using Intoner.Scene;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Resolves composed object scene snapshots and location specific load requests.
/// </summary>
internal interface IObjectSceneSnapshotResolver
{
    /// <summary>
    /// Gets the full composed object scene.
    /// </summary>
    /// <returns>The composed scene object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetSceneSnapshots();

    /// <summary>
    /// Tries to resolve one scene object snapshot from the composed scene.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved scene snapshot when found.</param>
    /// <returns>true when the scene contains that object.</returns>
    bool TryGetSceneSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Checks whether any standalone or loaded layout state can contribute to runtime scene loading.
    /// </summary>
    /// <returns>true when the scene has any current load source.</returns>
    bool HasAnyLoadState();

    /// <summary>
    /// Gets ordered scene load requests for the given location scope.
    /// </summary>
    /// <param name="currentLocation">The location scope to match.</param>
    /// <returns>The ordered scene load requests.</returns>
    IReadOnlyList<ObjectSceneLoadRequest> GetLoadRequests(SceneLocationScope currentLocation);
}

/// <summary> stores one requested scene snapshot with its source metadata </summary>
internal readonly record struct ObjectSceneLoadRequest(
    ObjectSnapshot Snapshot,
    ObjectSceneSource Source);

internal sealed class ObjectSceneSnapshotResolver : IObjectSceneSnapshotResolver
{
    private readonly IObjectPersistenceState _persistenceState;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectLayoutManager    _layoutManager;

    public ObjectSceneSnapshotResolver(
        IObjectPersistenceState persistenceState,
        IObjectLayoutManager layoutManager,
        ISceneLocationService locationService)
    {
        _persistenceState = persistenceState;
        _locationService = locationService;
        _layoutManager = layoutManager;
    }

    public IReadOnlyList<ObjectSnapshot> GetSceneSnapshots()
        => _persistenceState.GetSceneSnapshots(_layoutManager.GetTemporaryLayouts());

    public bool TryGetSceneSnapshot(Guid id, out ObjectSnapshot snapshot)
        => _persistenceState.TryGetSceneSnapshot(id, _layoutManager.GetTemporaryLayouts(), out snapshot);

    public bool HasAnyLoadState()
        => _persistenceState.HasStandaloneSnapshots()
            || _layoutManager.HasAnyLoadedLayouts();

    public IReadOnlyList<ObjectSceneLoadRequest> GetLoadRequests(SceneLocationScope currentLocation)
    {
        List<ObjectSceneLoadRequest> requests = [];
        AppendLoadRequests(_persistenceState.GetStandaloneSnapshots(), ObjectSceneSource.CreateStandalone());

        foreach (ObjectLayoutSnapshot layout in _layoutManager.GetEnabledLayouts())
        {
            AppendLoadRequests(layout.Objects, ObjectSceneSource.CreatePersistentLayout(layout.Id));
        }

        foreach (ObjectTemporaryLayoutSnapshot layout in _layoutManager.GetTemporaryLayouts())
        {
            AppendLoadRequests(layout.Objects, ObjectSceneSource.CreateTemporaryLayout(layout.SourceKey));
        }

        return requests
            .DistinctBy(static request => request.Snapshot.Id)
            .OrderBy(static request => request.Snapshot.CreatedAtUtc)
            .ToList();

        void AppendLoadRequests(IEnumerable<ObjectSnapshot> snapshots, ObjectSceneSource source)
        {
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (!_locationService.MatchesLocation(snapshot.CreatedIn, currentLocation))
                {
                    continue;
                }

                requests.Add(new ObjectSceneLoadRequest(snapshot, source));
            }
        }
    }
}
