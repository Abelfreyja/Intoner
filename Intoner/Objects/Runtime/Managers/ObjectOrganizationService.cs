using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary> applies folder and collection changes against scene objects as one transaction </summary>
internal interface IObjectOrganizationService
{
    /// <summary> checks the specific folders changed by an organization history action </summary>
    SceneHistoryResult CheckStoredFolderChange(ObjectFolderSceneState before, ObjectFolderSceneState after);

    /// <summary> replays object and folder history for any stored owner </summary>
    SceneHistoryResult ApplyStoredFolderChange(IReadOnlyList<SceneItemSnapshotChange> changes,
        SceneItemHistoryDependencies dependencies, ObjectFolderSceneState before, ObjectFolderSceneState after);

    /// <summary> applies object changes and explicit folder state together </summary>
    SceneMutationStatus ApplyFolderChange(
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> requestedSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        out IReadOnlyList<SceneItemSnapshot> appliedSnapshots);

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

    public SceneHistoryResult CheckStoredFolderChange(ObjectFolderSceneState before, ObjectFolderSceneState after)
    {
        lock (_stateLock)
        {
            return ObjectFolderSceneStateUtility.TryMergeChanges(_folders.CaptureStoredState(), before, after, out _)
                ? SceneHistoryResult.Success : SceneHistoryResult.Conflict("The folders changed externally or their layout was removed.");
        }
    }

    public SceneHistoryResult ApplyStoredFolderChange(IReadOnlyList<SceneItemSnapshotChange> changes,
        SceneItemHistoryDependencies dependencies, ObjectFolderSceneState before, ObjectFolderSceneState after)
    {
        lock (_stateLock)
        {
            ObjectFolderSceneState current = _folders.CaptureStoredState();

            if (!ObjectFolderSceneStateUtility.TryMergeChanges(current, before, after, out ObjectFolderSceneState merged))
            {
                return SceneHistoryResult.Conflict("The folders changed externally or their layout was removed.");
            }

            return _sceneItems.ApplyStoredChanges(changes, dependencies,
                () => ObjectFolderSceneStateUtility.StatesMatch(current, merged) || _folders.TryApplySceneState(merged));
        }
    }

    public SceneMutationStatus ApplyFolderChange(
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> requestedSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        out IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
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

            List<SceneItemSnapshotChange> changes = new(snapshots.Count);
            List<ObjectSnapshot> requestedSnapshots = new(snapshots.Count);
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (string.Equals(
                        ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
                        normalizedCollectionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ObjectSnapshot requested = snapshot with { CollectionId = normalizedCollectionId };
                changes.Add(new SceneItemSnapshotChange(snapshot, requested));
                requestedSnapshots.Add(requested);
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
        SceneHistoryResult result;
        lock (_stateLock)
        {
            string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
            if (!_collections.TryGetCollection(normalizedCollectionId, out _))
            {
                return SceneMutationStatus.Rejected;
            }

            List<SceneItemSnapshotChange> changes = _sceneItems.GetStoredItems().OfType<ObjectSnapshot>()
                .Where(snapshot => string.Equals(ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
                    normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
                .Select(static snapshot => new SceneItemSnapshotChange(snapshot, snapshot with { CollectionId = string.Empty }))
                .ToList();

            result = _sceneItems.ApplyStoredChanges(changes, SceneItemHistoryDependencies.Empty,
                () => _collections.TryDeleteCollection(normalizedCollectionId));
        }

        if (result.IsSuccess)
        {
            _sceneItems.ReconcileStoredChanges();
            return SceneMutationStatus.Applied;
        }

        return result.Outcome == SceneHistoryOutcome.RecoveryRequired
            ? SceneMutationStatus.RecoveryRequired : SceneMutationStatus.Rejected;
    }

    private SceneMutationStatus Apply<TSnapshot>(
        string operation,
        IReadOnlyList<SceneItemSnapshotChange> changes,
        IReadOnlyList<TSnapshot> requestedSnapshots,
        Func<bool> commit,
        out IReadOnlyList<TSnapshot> appliedSnapshots) where TSnapshot : SceneItemSnapshot
    {
        appliedSnapshots = [];
        IReadOnlyList<TSnapshot> resolvedSnapshots = [];
        SceneMutationStatus status = _sceneItems.ApplyChanges(
            changes,
            () =>
            {
                if (!_sceneItems.TryResolvePlacedItems(requestedSnapshots, out resolvedSnapshots))
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

    private static bool TryBuildFolderChanges(
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> requestedSnapshots,
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

    private static bool HasUniqueIds(IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        HashSet<Guid> ids = new(snapshots.Select(static snapshot => snapshot.Id));
        return ids.Count == snapshots.Count && !ids.Contains(Guid.Empty);
    }
}

internal sealed class ObjectOrganizationHistoryAction : SceneHistoryActionBase
{
    private readonly IObjectOrganizationService _organization;
    private readonly SceneItemHistoryChange _items;
    private long _validatedRevision = long.MinValue;
    private bool _validatedApply;
    private SceneHistoryResult _validation;
    private readonly ObjectFolderSceneState _beforeFolderState;
    private readonly ObjectFolderSceneState _afterFolderState;

    public ObjectOrganizationHistoryAction(
        IObjectOrganizationService organization,
        SceneItemHistoryChange items,
        SceneHistoryKind kind,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState)
        : base(items.Title, kind)
    {
        _organization = organization;
        _items = items;
        _beforeFolderState = beforeFolderState;
        _afterFolderState = afterFolderState;
    }

    public override SceneHistoryResult Validate(bool apply)
    {
        long revision = _items.HistoryRevision;

        if (revision != _validatedRevision || apply != _validatedApply)
        {
            _validation = _items.Validate(apply);

            if (_validation.IsSuccess)
            {
                _validation = _items.Describe(_organization.CheckStoredFolderChange(apply ? _beforeFolderState : _afterFolderState,
                    apply ? _afterFolderState : _beforeFolderState), apply);
            }

            _validatedRevision = revision;
            _validatedApply = apply;
        }

        return _validation;
    }

    protected override SceneHistoryResult ApplyCore() => Replay(apply: true);
    protected override SceneHistoryResult RevertCore() => Replay(apply: false);

    private SceneHistoryResult Replay(bool apply)
        => _items.Describe(_organization.ApplyStoredFolderChange(_items.GetChanges(apply), _items.GetDependencies(apply),
            apply ? _beforeFolderState : _afterFolderState, apply ? _afterFolderState : _beforeFolderState), apply);
}
