using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary> applies folder and collection changes against scene objects as one transaction </summary>
internal interface IObjectOrganizationService
{
    /// <summary> applies object changes and explicit folder state together </summary>
    SceneMutationStatus ApplyFolderChange(
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots);

    /// <summary> assigns one collection to the supplied objects </summary>
    SceneMutationStatus AssignCollection(
        string collectionId,
        IReadOnlyList<ObjectSnapshot> snapshots,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots);

    /// <summary> unassigns and deletes one collection </summary>
    SceneMutationStatus DeleteCollection(string collectionId);
}

internal sealed class ObjectOrganizationService : IObjectOrganizationService
{
    private readonly ILogger<ObjectOrganizationService> _logger;
    private readonly Lock _stateLock;
    private readonly ISceneItemService _sceneItems;
    private readonly IObjectFolderService _folders;
    private readonly IObjectCollectionManager _collections;

    public ObjectOrganizationService(
        ILogger<ObjectOrganizationService> logger,
        ObjectStateLock stateLock,
        ISceneItemService sceneItems,
        IObjectFolderService folders,
        IObjectCollectionManager collections)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        _sceneItems = sceneItems;
        _folders = folders;
        _collections = collections;
    }

    public SceneMutationStatus ApplyFolderChange(
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        lock (_stateLock)
        {
            appliedSnapshots = [];
            if (!ObjectFolderSceneStateUtility.StatesMatch(_folders.CaptureSceneState(), beforeFolderState)
                || !TryBuildFolderChanges(beforeSnapshots, requestedSnapshots, out IReadOnlyList<SceneItemSnapshotChange> changes))
            {
                return SceneMutationStatus.Rejected;
            }

            return Apply(
                "folder change",
                changes,
                requestedSnapshots,
                () => ObjectFolderSceneStateUtility.StatesMatch(beforeFolderState, afterFolderState)
                      || _folders.TryApplySceneState(afterFolderState),
                out appliedSnapshots);
        }
    }

    public SceneMutationStatus AssignCollection(
        string collectionId,
        IReadOnlyList<ObjectSnapshot> snapshots,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        lock (_stateLock)
        {
            appliedSnapshots = [];
            string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
            if (normalizedCollectionId.Length > 0
                && !_collections.TryGetCollection(normalizedCollectionId, out _))
            {
                return SceneMutationStatus.Rejected;
            }

            var beforeSnapshots = new List<ObjectSnapshot>(snapshots.Count);
            var requestedSnapshots = new List<ObjectSnapshot>(snapshots.Count);
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (string.Equals(
                        ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
                        normalizedCollectionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                beforeSnapshots.Add(snapshot);
                requestedSnapshots.Add(snapshot with { CollectionId = normalizedCollectionId });
            }

            if (!TryBuildUpdates(beforeSnapshots, requestedSnapshots, out IReadOnlyList<SceneItemSnapshotChange> changes))
            {
                return SceneMutationStatus.Rejected;
            }

            return Apply(
                "collection assignment",
                changes,
                requestedSnapshots,
                static () => true,
                out appliedSnapshots);
        }
    }

    public SceneMutationStatus DeleteCollection(string collectionId)
    {
        lock (_stateLock)
        {
            string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
            if (!_collections.TryGetCollection(normalizedCollectionId, out _))
            {
                return SceneMutationStatus.Rejected;
            }

            IReadOnlyList<ObjectSnapshot> beforeSnapshots = _sceneItems.GetPlacedItems()
                .OfType<ObjectSnapshot>()
                .Where(snapshot => string.Equals(
                    ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
                    normalizedCollectionId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            IReadOnlyList<ObjectSnapshot> requestedSnapshots = beforeSnapshots
                .Select(snapshot => snapshot with { CollectionId = string.Empty })
                .ToList();
            if (!TryBuildUpdates(beforeSnapshots, requestedSnapshots, out IReadOnlyList<SceneItemSnapshotChange> changes))
            {
                return SceneMutationStatus.Rejected;
            }

            return Apply(
                "collection deletion",
                changes,
                requestedSnapshots,
                () => _collections.TryDeleteCollection(normalizedCollectionId),
                out _);
        }
    }

    private SceneMutationStatus Apply(
        string operation,
        IReadOnlyList<SceneItemSnapshotChange> changes,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        Func<bool> commit,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        appliedSnapshots = [];
        IReadOnlyList<ObjectSnapshot> resolvedSnapshots = [];
        SceneMutationStatus status = _sceneItems.ApplyChanges(
            changes,
            () =>
            {
                if (!TryResolveAppliedSnapshots(requestedSnapshots, out resolvedSnapshots))
                {
                    _logger.LogError("could not resolve applied objects after {Operation}", operation);
                    return false;
                }

                return commit();
            });
        if (status.IsApplied())
        {
            appliedSnapshots = resolvedSnapshots;
        }

        return status;
    }

    private bool TryResolveAppliedSnapshots(
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        out IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        Dictionary<Guid, ObjectSnapshot> placedById = _sceneItems.GetPlacedItems()
            .OfType<ObjectSnapshot>()
            .ToDictionary(static snapshot => snapshot.Id);
        var applied = new List<ObjectSnapshot>(requestedSnapshots.Count);
        foreach (ObjectSnapshot requested in requestedSnapshots)
        {
            if (!placedById.TryGetValue(requested.Id, out ObjectSnapshot? appliedSnapshot))
            {
                appliedSnapshots = [];
                return false;
            }

            applied.Add(appliedSnapshot);
        }

        appliedSnapshots = applied;
        return true;
    }

    private static bool TryBuildUpdates(
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        out IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        if (beforeSnapshots.Count != requestedSnapshots.Count)
        {
            changes = [];
            return false;
        }

        var result = new List<SceneItemSnapshotChange>(beforeSnapshots.Count);
        var ids = new HashSet<Guid>();
        for (int index = 0; index < beforeSnapshots.Count; ++index)
        {
            ObjectSnapshot before = beforeSnapshots[index];
            ObjectSnapshot after = requestedSnapshots[index];
            if (before.Id == Guid.Empty
                || before.Id != after.Id
                || !ids.Add(before.Id))
            {
                changes = [];
                return false;
            }

            if (!Equals(before, after))
            {
                result.Add(new SceneItemSnapshotChange(before, after));
            }
        }

        changes = result;
        return true;
    }

    private static bool TryBuildFolderChanges(
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        out IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        if (!HasUniqueIds(beforeSnapshots) || !HasUniqueIds(requestedSnapshots))
        {
            changes = [];
            return false;
        }

        changes = SceneItemHistoryChanges.Build(beforeSnapshots, requestedSnapshots);
        return true;
    }

    private static bool HasUniqueIds(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        HashSet<Guid> ids = new(snapshots.Select(static snapshot => snapshot.Id));
        return ids.Count == snapshots.Count && !ids.Contains(Guid.Empty);
    }
}
