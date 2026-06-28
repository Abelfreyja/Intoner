using Intoner.Objects.Models;
using Intoner.Objects.Utils;

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
    IReadOnlyList<ObjectSceneLoadRequest> GetLoadRequests(ObjectLocationScope currentLocation);
}

/// <summary> stores one requested scene snapshot with its source metadata </summary>
internal readonly record struct ObjectSceneLoadRequest(
    ObjectSnapshot Snapshot,
    ObjectSceneSource Source);

internal sealed class ObjectSceneSnapshotResolver : IObjectSceneSnapshotResolver
{
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectLayoutManager    _layoutManager;

    public ObjectSceneSnapshotResolver(
        IObjectPersistenceState persistenceState,
        IObjectLayoutManager layoutManager)
    {
        _persistenceState = persistenceState;
        _layoutManager = layoutManager;
    }

    public IReadOnlyList<ObjectSnapshot> GetSceneSnapshots()
        => _persistenceState.GetSceneSnapshots(_layoutManager.GetTemporaryLayouts());

    public bool TryGetSceneSnapshot(Guid id, out ObjectSnapshot snapshot)
        => _persistenceState.TryGetSceneSnapshot(id, _layoutManager.GetTemporaryLayouts(), out snapshot);

    public bool HasAnyLoadState()
        => _persistenceState.HasStandaloneSnapshots()
            || _layoutManager.HasAnyLoadedLayouts();

    public IReadOnlyList<ObjectSceneLoadRequest> GetLoadRequests(ObjectLocationScope currentLocation)
    {
        List<ObjectSceneLoadRequest> requests = [];
        AppendStandaloneRequests(requests, currentLocation);
        AppendDefaultLayoutRequests(requests, currentLocation);
        AppendTemporaryLayoutRequests(requests, currentLocation);
        return CollapseLoadRequests(requests);
    }

    private void AppendStandaloneRequests(List<ObjectSceneLoadRequest> requests, ObjectLocationScope currentLocation)
        => AppendLoadRequests(
            requests,
            _persistenceState.GetStandaloneSnapshots(),
            currentLocation,
            snapshot => _persistenceState.ResolveSceneSource(snapshot));

    private void AppendDefaultLayoutRequests(List<ObjectSceneLoadRequest> requests, ObjectLocationScope currentLocation)
    {
        if (!_persistenceState.TryGetDefaultLayout(out var defaultLayout))
        {
            return;
        }

        var source = ObjectSceneSource.CreateDefaultLayout(defaultLayout.Id);

        AppendLoadRequests(
            requests,
            defaultLayout.Objects,
            currentLocation,
            _ => source);
    }

    private void AppendTemporaryLayoutRequests(List<ObjectSceneLoadRequest> requests, ObjectLocationScope currentLocation)
    {
        foreach (var temporaryLayout in _layoutManager.GetTemporaryLayouts())
        {
            var source = ObjectSceneSource.CreateTemporaryLayout(temporaryLayout.SourceKey);

            AppendLoadRequests(
                requests,
                temporaryLayout.Objects,
                currentLocation,
                _ => source);
        }
    }

    private static void AppendLoadRequests(
        List<ObjectSceneLoadRequest> requests,
        IEnumerable<ObjectSnapshot> snapshots,
        ObjectLocationScope currentLocation,
        Func<ObjectSnapshot, ObjectSceneSource> resolveSource)
    {
        foreach (var snapshot in snapshots)
        {
            if (!ObjectSnapshotUtility.MatchesLocation(snapshot, currentLocation))
            {
                continue;
            }

            requests.Add(new ObjectSceneLoadRequest(snapshot, resolveSource(snapshot)));
        }
    }

    private static List<ObjectSceneLoadRequest> CollapseLoadRequests(IReadOnlyList<ObjectSceneLoadRequest> orderedRequests)
    {
        Dictionary<Guid, (int FirstIndex, ObjectSceneLoadRequest Request)> requestsById = [];
        for (var index = 0; index < orderedRequests.Count; ++index)
        {
            var request = orderedRequests[index];
            if (requestsById.TryGetValue(request.Snapshot.Id, out var existing))
            {
                requestsById[request.Snapshot.Id] = (existing.FirstIndex, request);
                continue;
            }

            requestsById.Add(request.Snapshot.Id, (index, request));
        }

        var collapsedRequests = requestsById.Values.ToList();
        collapsedRequests.Sort(static (left, right) =>
        {
            var createdAtComparison = left.Request.Snapshot.CreatedAtUtc.CompareTo(right.Request.Snapshot.CreatedAtUtc);
            return createdAtComparison != 0
                ? createdAtComparison
                : left.FirstIndex.CompareTo(right.FirstIndex);
        });

        List<ObjectSceneLoadRequest> result = new(collapsedRequests.Count);
        foreach (var request in collapsedRequests)
        {
            result.Add(request.Request);
        }

        return result;
    }
}

