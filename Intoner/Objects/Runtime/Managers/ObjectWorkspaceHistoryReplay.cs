using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Collections.Immutable;

namespace Intoner.Objects.Runtime;

/// <summary> captures and replays stored workspace changes for layout history </summary>
internal interface IObjectWorkspaceHistoryReplay
{
    /// <summary> gets content and dependency versions for cached validation </summary>
    ObjectWorkspaceHistoryVersion GetVersion();

    /// <summary> captures saved owners, standalone content and workspace selection </summary>
    ObjectWorkspaceHistoryState CaptureState();

    /// <summary> checks a recorded change without changing saved state </summary>
    SceneHistoryResult Check(ObjectWorkspaceHistoryChange change);

    /// <summary> replays the change and retains the highest observed layout revisions </summary>
    SceneHistoryResult Apply(ObjectWorkspaceHistoryChange change, Dictionary<Guid, long> revisionFloors);
}

internal sealed class ObjectWorkspaceHistoryReplay : IObjectWorkspaceHistoryReplay
{
    private readonly Lock _stateLock;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectIdentityService _objectIdentityService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectSceneState _sceneState;

    public ObjectWorkspaceHistoryReplay(ObjectStateLock stateLock, IObjectLayoutManager layoutManager,
        IObjectPersistenceState persistenceState, IObjectFolderService objectFolderService,
        IObjectIdentityService objectIdentityService, IObjectHousingModePolicy housingModePolicy,
        IObjectRevisionTracker revisionTracker, IObjectSceneState sceneState)
    {
        _stateLock = stateLock.Value;
        _layoutManager = layoutManager;
        _persistenceState = persistenceState;
        _objectFolderService = objectFolderService;
        _objectIdentityService = objectIdentityService;
        _housingModePolicy = housingModePolicy;
        _revisionTracker = revisionTracker;
        _sceneState = sceneState;
    }
    public ObjectWorkspaceHistoryVersion GetVersion()
    {
        lock (_stateLock)
        {
            return new ObjectWorkspaceHistoryVersion(unchecked(_revisionTracker.GetPersistentSceneRevision() + _revisionTracker.GetSavedLayoutsRevision()), _objectIdentityService.Revision, _housingModePolicy.GetState());
        }
    }

    public ObjectWorkspaceHistoryState CaptureState()
    {
        lock (_stateLock)
        {
            return new ObjectWorkspaceHistoryState(_layoutManager.GetLayouts().ToImmutableArray(),
                [.. _persistenceState.GetStandaloneSnapshots(), .. _persistenceState.GetStandaloneGroups()],
                _objectFolderService.CaptureSceneState().StandaloneFolders,
                _layoutManager.GetEnabledLayouts().Select(static layout => layout.Id).ToImmutableArray(),
                _layoutManager.GetCreationLayoutId());
        }
    }

    public SceneHistoryResult Check(ObjectWorkspaceHistoryChange change)
    {
        lock (_stateLock)
        {
            return PrepareHistoryChange(change, CaptureState(), null, out _);
        }
    }

    public SceneHistoryResult Apply(ObjectWorkspaceHistoryChange change, Dictionary<Guid, long> revisionFloors)
    {
        lock (_stateLock)
        {
            ObjectWorkspaceHistoryState current = CaptureState();
            TrackRevisions(current.Layouts, revisionFloors);
            SceneHistoryResult validation = PrepareHistoryChange(change, current, revisionFloors, out PreparedHistoryChange? prepared);

            if (!validation.IsSuccess)
            {
                return validation;
            }

            PreparedHistoryChange next = prepared!;
            PersistentMutationStatus status = _layoutManager.ReplaceStoredLayouts(next.Layouts, next.EnabledLayoutIds, next.CreationLayoutId);

            if (status != PersistentMutationStatus.Success)
            {
                return status == PersistentMutationStatus.RecoveryRequired
                    ? SceneHistoryResult.RecoveryRequired("Layout storage could not be restored after a failed history operation.")
                    : SceneHistoryResult.Failed("The layout change could not be saved.");
            }

            if (change.StandaloneChanged)
            {
                _persistenceState.ReplaceStandaloneContent(next.StandaloneObjects, next.StandaloneGroups);
                _objectFolderService.ReplaceStandaloneState(next.StandaloneFolders);
            }

            TrackRevisions(next.Layouts, revisionFloors);
            bool currentChanged = change.SelectionChanged || change.StandaloneChanged
                || change.Layouts.Any(transition => transition.ContentsChanged
                    && (current.EnabledLayoutIds.Contains(transition.Id) || next.EnabledLayoutIds.Contains(transition.Id)));

            if (currentChanged)
            {
                _revisionTracker.Increment(persistentChanged: true);
                _sceneState.MarkNeedsRefresh();
            }

            return SceneHistoryResult.Success;
        }
    }

    private SceneHistoryResult PrepareHistoryChange(ObjectWorkspaceHistoryChange change, ObjectWorkspaceHistoryState current,
        IReadOnlyDictionary<Guid, long>? revisionFloors, out PreparedHistoryChange? prepared)
    {
        prepared = null;
        SceneHistoryResult validation = change.Validate(current);

        if (!validation.IsSuccess)
        {
            return validation;
        }

        Dictionary<Guid, ObjectLayoutSnapshot> layouts = current.Layouts.ToDictionary(static layout => layout.Id);
        List<Guid> order = current.Layouts.Select(static layout => layout.Id).ToList();
        List<ObjectSavedLayoutChange> restored = [];
        foreach (ObjectSavedLayoutChange transition in change.Layouts)
        {
            if (transition.After is null)
            {
                layouts.Remove(transition.Id);
                order.Remove(transition.Id);
                continue;
            }

            ObjectLayoutSnapshot? saved = layouts.GetValueOrDefault(transition.Id);
            ObjectLayoutSnapshot candidate = saved is null || transition.ContentsChanged ? transition.After : saved;
            candidate = candidate with
            {
                Name = transition.NameChanged ? transition.After.Name : saved?.Name ?? candidate.Name,
                CreatedAtUtc = saved?.CreatedAtUtc ?? candidate.CreatedAtUtc,
                Revision = checked(Math.Max(saved?.Revision ?? transition.After.Revision, (revisionFloors?.GetValueOrDefault(transition.Id) ?? 0)) + 1),
                UpdatedAtUtc = DateTime.UtcNow,
            };
            layouts[transition.Id] = candidate;

            if (saved is null)
            {
                restored.Add(transition);
            }
        }

        foreach (ObjectSavedLayoutChange transition in restored.OrderBy(static layout => layout.AfterIndex))
        {
            int index = transition.AfterIndex < 0 ? order.Count : Math.Min(transition.AfterIndex, order.Count);
            order.Insert(index, transition.Id);
        }

        change.GetSelection(current, out IReadOnlyList<Guid> enabledLayoutIds, out Guid? creationLayoutId);
        IReadOnlyList<SceneItemSnapshot> standalone = change.StandaloneChanged ? change.After.Standalone : current.Standalone;
        IReadOnlyList<ObjectFolderSnapshot> folders = change.StandaloneChanged ? change.After.StandaloneFolders : current.StandaloneFolders;
        ObjectSnapshot[] standaloneObjects = standalone.OfType<ObjectSnapshot>().ToArray();
        SceneGroupSnapshot[] standaloneGroups = standalone.OfType<SceneGroupSnapshot>().ToArray();
        List<ObjectLayoutSnapshot> nextLayouts = order.Select(id => layouts[id]).ToList();

        if (!SceneGroupUtility.TryValidateOwner(null, standaloneObjects, standaloneGroups, out _)
            || nextLayouts.Any(layout => !SceneGroupUtility.TryValidateOwner(layout.Id, layout.Objects, layout.Groups, out _)))
        {
            return SceneHistoryResult.Failed("The recorded layout ownership or group membership is invalid.");
        }

        ObjectSnapshot[] allObjects = [.. standaloneObjects, .. nextLayouts.SelectMany(static layout => layout.Objects)];
        SceneGroupSnapshot[] allGroups = [.. standaloneGroups, .. nextLayouts.SelectMany(static layout => layout.Groups)];

        if (!_objectIdentityService.TryValidatePersistentSceneReplacement(allObjects,
                current.Layouts.Select(static layout => layout.Id).ToHashSet(), out _, allGroups))
        {
            return SceneHistoryResult.Conflict("An object identity is now owned by another source.");
        }

        if (change.SelectionChanged && !_housingModePolicy.TryValidateLayout(
                [.. standaloneObjects, .. enabledLayoutIds.SelectMany(id => layouts[id].Objects)], out string reason))
        {
            return SceneHistoryResult.Conflict(reason);
        }

        prepared = new PreparedHistoryChange(nextLayouts, enabledLayoutIds, creationLayoutId, standaloneObjects, standaloneGroups, folders);
        return SceneHistoryResult.Success;
    }

    private static void TrackRevisions(IReadOnlyList<ObjectLayoutSnapshot> layouts, Dictionary<Guid, long> revisionFloors)
    {
        if (revisionFloors.Count == 0)
        {
            return;
        }

        foreach (ObjectLayoutSnapshot layout in layouts)
        {
            if (revisionFloors.TryGetValue(layout.Id, out long previous))
            {
                revisionFloors[layout.Id] = Math.Max(previous, layout.Revision);
            }
        }
    }

    private sealed record PreparedHistoryChange(
        IReadOnlyList<ObjectLayoutSnapshot> Layouts,
        IReadOnlyList<Guid> EnabledLayoutIds,
        Guid? CreationLayoutId,
        IReadOnlyList<ObjectSnapshot> StandaloneObjects,
        IReadOnlyList<SceneGroupSnapshot> StandaloneGroups,
        IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders);
}
