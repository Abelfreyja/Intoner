using Intoner.Displays;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Collections.Frozen;
using System.Numerics;

namespace Intoner.Objects.UI.Services;

/// <summary> coordinates scene editor undo and redo recording around editor mutations </summary>
internal interface IHistoryCoordinator : IDisposable
{
    /// <summary> applies and records checked item and folder changes together </summary>
    bool TryApplyFolderChange(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> requestedSnapshots, ObjectFolderSceneState beforeFolders, ObjectFolderSceneState afterFolders,
        IReadOnlyList<Guid>? selectionAfterApply = null, IReadOnlyList<Guid>? selectionAfterRevert = null);
    /// <summary> centers the group pivot on its members </summary>
    bool TryCenterGroupPivot(SceneGroupSnapshot group);
    /// <summary> edits pivot position and orientation </summary>
    void ApplyInspectorGroupPivotEdit(string editId, string title, SceneGroupSnapshot group, Vector3 position, Vector3 rotationDegrees);
    /// <summary> applies a checked batch </summary>
    bool TryApplySceneChanges(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> before,
        IReadOnlyList<SceneItemSnapshot> after, IReadOnlyList<Guid> selectionAfter);

    /// <summary> connects selection callbacks used for history restore and capture </summary>
    /// <param name="captureSelectionIds">reads the current editor selection ids</param>
    /// <param name="applySelectionIds">applies a restored editor selection</param>
    void ConnectSelectionHandlers(Func<IReadOnlyList<Guid>> captureSelectionIds, Action<IReadOnlyList<Guid>?> applySelectionIds);

    /// <summary> clears any connected selection callbacks </summary>
    void DisconnectSelectionHandlers();

    /// <summary> finishes inspector edits when the current location changes, retaining history </summary>
    /// <param name="currentContext">the latest object creation context</param>
    /// <returns>true when the location context changed</returns>
    bool RefreshContext(SceneCreationContext currentContext);

    /// <summary> forces any pending inspector edits to become one history step </summary>
    void CommitPendingInspectorEdits();

    /// <summary> applies one inspector edit and records it when the edit is finished </summary>
    /// <param name="editId">stable editor local id for the edited field</param>
    /// <param name="kind">main history kind for the edit</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="startSnapshot">snapshot captured before the edit started</param>
    /// <param name="nextSnapshot">latest replacement snapshot to apply</param>
    /// <param name="recordImmediately">whether the edit should record immediately instead of waiting for deactivation</param>
    void ApplyInspectorSnapshotEdit(string editId, SceneHistoryKind kind, string title, SceneItemSnapshot startSnapshot, SceneItemSnapshot? nextSnapshot, bool recordImmediately = false);

    /// <summary> tries to undo one history step </summary>
    /// <returns>true when one history step was undone</returns>
    bool TryUndo();

    /// <summary> tries to redo one history step </summary>
    /// <returns>true when one history step was redone</returns>
    bool TryRedo();

    /// <summary> tries to jump to a reachable history state index </summary>
    /// <param name="stateIndex">the target history state index</param>
    /// <returns>true when the jump succeeded</returns>
    bool TryJumpToState(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries);

    /// <summary> tries to create one object and record the change </summary>
    /// <param name="title">display title for the history entry</param>
    /// <param name="kind">the object kind to create</param>
    /// <param name="overrides">optional placement overrides</param>
    /// <returns>true when the object was created and recorded</returns>
    bool TryCreateObject(string title, ObjectKind kind, ObjectPlacementOverrides? overrides);

    /// <summary> tries to assign one collection to the supplied objects and record the change </summary>
    /// <param name="title">display title for the history entry</param>
    /// <param name="collectionId">the collection id, or an empty value to unassign</param>
    /// <param name="snapshots">current object snapshots</param>
    /// <returns>true when the collection assignment succeeded and was recorded</returns>
    bool TryAssignObjectCollection(string title, string collectionId, IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary> tries to move one scene item to the player and record the change </summary>
    /// <param name="itemId">the scene item id to move</param>
    /// <returns>true when the move succeeded and was recorded</returns>
    bool TryMoveItemToPlayer(Guid itemId);

    /// <summary> changes the selected objects location in one history step </summary>
    /// <param name="snapshots">the object snapshots shown by the location editor</param>
    /// <param name="change">the world visibility and optional housing address</param>
    /// <returns>true when the location change succeeded and was recorded</returns>
    bool TryChangeObjectLocation(IReadOnlyList<ObjectSnapshot> snapshots, SceneLocationChange change);

    /// <summary> tries to apply one selected object update batch and record the change </summary>
    /// <param name="kind">main history kind for the update</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="selectedSnapshots">current selected snapshots</param>
    /// <param name="updateFactory">builds the next snapshot for each selected object</param>
    /// <returns>true when the batch update succeeded and was recorded</returns>
    bool TryApplySelectedSnapshotUpdate(SceneHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> selectedSnapshots, Func<ObjectSnapshot, ObjectSnapshot> updateFactory);

    /// <summary> tries to apply one selected scene item update batch and record the change </summary>
    /// <param name="kind">main history kind for the update</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="selectedSnapshots">current selected snapshots</param>
    /// <param name="updateFactory">builds the next snapshot for each selected item</param>
    /// <returns>true when the batch update succeeded and was recorded</returns>
    bool TryApplySceneItemUpdate(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> selectedSnapshots, Func<SceneItemSnapshot, SceneItemSnapshot> updateFactory);

    /// <summary> tries to duplicate multiple selected scene items and record the change </summary>
    /// <param name="selectedSnapshots">the selected snapshots to duplicate</param>
    /// <returns>true when the duplicate batch succeeded and was recorded</returns>
    bool TryDuplicateSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots);

    /// <summary> tries to remove multiple selected scene items and record the change </summary>
    /// <param name="selectedSnapshots">the selected snapshots to remove</param>
    /// <returns>true when the remove batch succeeded and was recorded</returns>
    bool TryRemoveSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots);

    /// <summary> tries to clear the confirmed placed objects and groups and record the change </summary>
    /// <param name="persistentRevision">the persistent scene revision shown when the clear was confirmed</param>
    /// <param name="targets">the objects and groups included in the clear confirmation</param>
    /// <returns>true when the clear succeeded and was recorded</returns>
    bool TryClearPlacedObjects(long persistentRevision, IReadOnlyList<ObjectSceneItemSnapshot> targets);

    /// <summary> commits and records a completed scene preview together </summary>
    bool TryCommitSceneEdit(SceneEditSession edit, SceneHistoryKind kind, string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots, IReadOnlyList<SceneItemSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert);

    /// <summary> creates a display and records its placement </summary>
    bool TryCreateDisplay(SceneTransform transform);
}

internal sealed class HistoryCoordinator : IHistoryCoordinator
{
    private sealed class PendingInspectorEdit(
        string editId,
        string title,
        SceneHistoryKind kind,
        Guid itemId,
        IReadOnlyList<SceneItemSnapshot> startSnapshots,
        Guid[] selectionIds,
        bool editGroupPivot,
        IReadOnlyList<SceneItemSnapshot> storedItems)
    {
        public string EditId { get; } = editId;
        public string Title { get; } = title;
        public SceneHistoryKind Kind { get; } = kind;
        public Guid ItemId { get; } = itemId;
        public IReadOnlyList<SceneItemSnapshot> StartSnapshots { get; } = startSnapshots;
        public IReadOnlyList<SceneItemSnapshot> LatestSnapshots { get; set; } = startSnapshots;
        public Guid[] SelectionIds { get; } = selectionIds;
        public bool EditGroupPivot { get; } = editGroupPivot;
        public IReadOnlyList<SceneItemSnapshot> StoredItems { get; } = storedItems;
        public long HistoryRevision { get; set; }
    }

    private readonly Lock _stateLock;
    private readonly ISceneHistoryManager _historyManager;
    private readonly IObjectMutationService _mutationService;
    private readonly IObjectOrganizationService _organizationService;
    private readonly IObjectSceneView _sceneView;
    private readonly ISceneItemService _sceneItemService;
    private readonly IDisplayService _displayService;
    private readonly IScenePlacementService _placementService;
    private PendingInspectorEdit? _pendingInspectorEdit;

    private Func<IReadOnlyList<Guid>>? _captureSelectionIds;
    private Action<IReadOnlyList<Guid>?>? _applySelectionIds;
    private SceneCreationContext? _currentContext;

    public HistoryCoordinator(
        ISceneHistoryManager historyManager,
        IObjectMutationService mutationService,
        IObjectOrganizationService organizationService,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService,
        IDisplayService displayService,
        IScenePlacementService placementService,
        ObjectStateLock stateLock)
    {
        _stateLock = stateLock.Value;
        _historyManager = historyManager;
        _mutationService = mutationService;
        _organizationService = organizationService;
        _sceneView = sceneView;
        _sceneItemService = sceneItemService;
        _displayService = displayService;
        _placementService = placementService;

        _historyManager.ActionApplied += HandleHistoryActionApplied;
        _historyManager.ActionReverted += HandleHistoryActionReverted;
    }

    public void ConnectSelectionHandlers(Func<IReadOnlyList<Guid>> captureSelectionIds, Action<IReadOnlyList<Guid>?> applySelectionIds)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        ArgumentNullException.ThrowIfNull(captureSelectionIds);
        ArgumentNullException.ThrowIfNull(applySelectionIds);

        _captureSelectionIds = captureSelectionIds;
        _applySelectionIds = applySelectionIds;
    }

    public void DisconnectSelectionHandlers()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        _captureSelectionIds = null;
        _applySelectionIds = null;
    }

    public bool RefreshContext(SceneCreationContext currentContext)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        ArgumentNullException.ThrowIfNull(currentContext);

        if (Equals(_currentContext, currentContext))
        {
            return false;
        }

        CommitPendingInspectorEdits();
        _currentContext = currentContext;
        return true;
    }

    public void CommitPendingInspectorEdits()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (_pendingInspectorEdit is not { } edit)
        {
            return;
        }

        _pendingInspectorEdit = null;
        RecordItemHistory(edit.Kind, CaptureItemHistory(edit.Title, edit.StartSnapshots, edit.LatestSnapshots, edit.StoredItems),
            edit.SelectionIds, edit.SelectionIds);
    }

    public void ApplyInspectorSnapshotEdit(string editId, SceneHistoryKind kind, string title, SceneItemSnapshot startSnapshot, SceneItemSnapshot? nextSnapshot, bool recordImmediately = false)
        => ApplyInspectorSnapshotEdit(editId, kind, title, startSnapshot, nextSnapshot, recordImmediately, false);

    public void ApplyInspectorGroupPivotEdit(string editId, string title, SceneGroupSnapshot group, Vector3 position, Vector3 rotationDegrees)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (group.Locked || !NumericsUtility.IsFinite(position) || !NumericsUtility.IsFinite(rotationDegrees))
            return;
        ApplyInspectorSnapshotEdit(editId, SceneHistoryKind.Transform, title, group,
            group with { Transform = group.Transform with { Position = position, RotationDegrees = rotationDegrees } }, false, true);
    }

    private void ApplyInspectorSnapshotEdit(string editId, SceneHistoryKind kind, string title, SceneItemSnapshot startSnapshot,
        SceneItemSnapshot? nextSnapshot, bool recordImmediately, bool editGroupPivot)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (_pendingInspectorEdit is not null
            && (!string.Equals(_pendingInspectorEdit.EditId, editId, StringComparison.Ordinal)
                || _pendingInspectorEdit.ItemId != startSnapshot.Id
                || _pendingInspectorEdit.EditGroupPivot != editGroupPivot))
        {
            CommitPendingInspectorEdits();
        }

        if (_pendingInspectorEdit is { } pending && pending.HistoryRevision != _sceneItemService.GetHistoryRevision())
        {
            SceneItemHistoryChange change = CaptureItemHistory(pending.Title, pending.StartSnapshots, pending.LatestSnapshots, pending.StoredItems);

            if (!change.HasChanges || !change.Validate(apply: false).IsSuccess)
            {
                CommitPendingInspectorEdits();
            }
        }

        if (nextSnapshot is not null && !Equals(startSnapshot, nextSnapshot))
        {
            long? expectedRevision = startSnapshot is SceneGroupSnapshot
                ? _sceneItemService.GetRevisions().Persistent
                : null;

            if (!TryResolveItemChanges(startSnapshot, nextSnapshot, out IReadOnlyList<SceneItemSnapshot> before,
                    out IReadOnlyList<SceneItemSnapshot> after, editGroupPivot))
            {
                return;
            }

            IReadOnlyList<SceneItemSnapshot> storedItems = _pendingInspectorEdit?.StoredItems ?? _sceneItemService.GetStoredItems().ToArray();

            if (!TryApplySnapshotChanges(before, after, out IReadOnlyList<SceneItemSnapshot> applied, expectedRevision))
            {
                return;
            }

            long historyRevision = _sceneItemService.GetHistoryRevision();

            if (_pendingInspectorEdit is null)
            {
                _pendingInspectorEdit = new PendingInspectorEdit(
                    editId,
                    title,
                    kind,
                    startSnapshot.Id,
                    before,
                    [.. CaptureCurrentSelectionIds()],
                    editGroupPivot,
                    storedItems);
            }

            _pendingInspectorEdit.LatestSnapshots = applied;
            _pendingInspectorEdit.HistoryRevision = historyRevision;
        }

        if (recordImmediately)
        {
            CommitPendingInspectorEdits();
        }
    }

    public bool TryUndo()
        => TryReplayHistory(_historyManager.Undo);

    public bool TryRedo()
        => TryReplayHistory(_historyManager.Redo);

    public bool TryJumpToState(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries)
        => TryReplayHistory(() => _historyManager.TryJumpToState(stateIndex, expectedEntries));

    public bool TryCreateObject(string title, ObjectKind kind, ObjectPlacementOverrides? overrides)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();
        var selectionBefore = CaptureCurrentSelectionIds();
        var createdId = _mutationService.CreateObjectAtPlayer(kind, out var createdSnapshot, overrides);
        if (!createdId.HasValue)
        {
            return false;
        }

        return TryRecordCompletedAction(SceneHistoryKind.Create, title, [], [createdSnapshot], [createdId.Value], selectionBefore);
    }

    public bool TryAssignObjectCollection(string title, string collectionId, IReadOnlyList<ObjectSnapshot> snapshots)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        IReadOnlyList<ObjectSnapshot> beforeSnapshots = snapshots
            .Where(snapshot => !string.Equals(
                ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
                normalizedCollectionId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (beforeSnapshots.Count == 0)
        {
            return false;
        }

        CommitPendingInspectorEdits();
        if (!_organizationService.AssignCollection(
                normalizedCollectionId,
                beforeSnapshots,
                out IReadOnlyList<ObjectSnapshot> afterSnapshots).IsApplied())
        {
            return false;
        }

        IReadOnlyList<Guid> selectionIds = CaptureCurrentSelectionIds();
        return TryRecordCompletedAction(
            SceneHistoryKind.Organization,
            title,
            beforeSnapshots,
            afterSnapshots,
            selectionIds,
            selectionIds);
    }

    public bool TryMoveItemToPlayer(Guid itemId)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();
        if (!_sceneItemService.TryGetPlacedItem(itemId, out SceneItemSnapshot beforeSnapshot)
            || !_placementService.TryResolveFromPlayer(out SceneTransform placement))
        {
            return false;
        }

        SceneItemSnapshot requestedSnapshot = beforeSnapshot with
        {
            Transform = beforeSnapshot.Transform with
            {
                Position = placement.Position,
                RotationDegrees = placement.RotationDegrees,
            },
        };
        if (beforeSnapshot is SceneGroupSnapshot group)
            return TryTransformGroup(group, requestedSnapshot.Transform);
        return TryApplySceneChanges(
            SceneHistoryKind.Move,
            "Move Item To Player",
            [beforeSnapshot],
            [requestedSnapshot],
            CaptureCurrentSelectionIds());
    }

    private bool TryTransformGroup(SceneGroupSnapshot group, SceneTransform transform)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();
        long revision = _sceneItemService.GetRevisions().Persistent;
        return TryResolveItemChanges(group, group with { Transform = transform }, out IReadOnlyList<SceneItemSnapshot> before,
                out IReadOnlyList<SceneItemSnapshot> after)
            && TryApplySceneChanges(SceneHistoryKind.Transform, "Transform Group", before, after, CaptureCurrentSelectionIds(), revision);
    }

    public bool TryCenterGroupPivot(SceneGroupSnapshot group)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (group.Locked)
            return false;
        CommitPendingInspectorEdits();
        long revision = _sceneItemService.GetRevisions().Persistent;
        IReadOnlyList<ObjectSnapshot> members = _sceneView.GetPlacedObjectSnapshots().Where(snapshot => snapshot.GroupId == group.Id).ToArray();
        if (members.Count == 0)
            return false;
        Vector3 position = SceneSelectionTransformMath.ResolveSelectionPivotPosition(members, new SceneItemBoundsLookup(_sceneView.GetObjectBoundsSnapshots()));
        return TryApplySceneChanges(SceneHistoryKind.Transform, "Center Group Pivot", [group],
            [group with { Transform = group.Transform with { Position = position } }], [group.Id], revision);
    }

    private bool TryResolveItemChanges(SceneItemSnapshot start, SceneItemSnapshot next,
        out IReadOnlyList<SceneItemSnapshot> before, out IReadOnlyList<SceneItemSnapshot> after, bool editGroupPivot = false,
        ILookup<Guid?, ObjectSnapshot>? groupMembers = null)
    {
        before = [];
        after = [];
        if (start is not SceneGroupSnapshot group || next is not SceneGroupSnapshot requested)
        {
            before = [start];
            after = [next];
            return true;
        }

        IReadOnlyList<ObjectSnapshot> members = [];
        if (!editGroupPivot)
            members = groupMembers is null
                ? _sceneView.GetPlacedObjectSnapshots().Where(snapshot => snapshot.GroupId == group.Id).ToArray()
                : groupMembers[group.Id].ToArray();
        IReadOnlySet<Guid> scalableMembers = FrozenSet<Guid>.Empty;
        if (!editGroupPivot && group.Transform != requested.Transform)
        {
            HashSet<Guid> scalable = [];
            foreach (ObjectSnapshot member in members)
            {
                if (!_sceneItemService.TryGetManipulation(member, out SceneItemManipulationPolicy policy))
                    return false;
                if (policy.Describe(member).SupportsScale)
                    scalable.Add(member.Id);
            }
            scalableMembers = scalable;
        }

        return SceneGroupUtility.TryPlanChange(group, requested, members, scalableMembers, editGroupPivot, out before, out after);
    }

    public bool TryApplySelectedSnapshotUpdate(SceneHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> selectedSnapshots, Func<ObjectSnapshot, ObjectSnapshot> updateFactory)
        => TryApplySceneItemUpdate(
            kind,
            title,
            selectedSnapshots,
            snapshot => updateFactory((ObjectSnapshot)snapshot));

    public bool TryChangeObjectLocation(IReadOnlyList<ObjectSnapshot> snapshots, SceneLocationChange change)
    {
        if (snapshots.Any(static snapshot => !snapshot.CreatedIn.IsAssigned))
        {
            return false;
        }

        return TryApplySelectedSnapshotUpdate(SceneHistoryKind.Organization, "Change Location", snapshots, snapshot =>
        {
            SceneCreationContext location = change.Apply(snapshot.CreatedIn);

            if (location == snapshot.CreatedIn)
            {
                return snapshot;
            }

            return snapshot with
            {
                CreatedIn = location,
                GroupId = location.HasSameLocation(snapshot.CreatedIn) ? snapshot.GroupId : null,
            };
        });
    }

    public bool TryApplySceneItemUpdate(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        Func<SceneItemSnapshot, SceneItemSnapshot> updateFactory)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        CommitPendingInspectorEdits();
        var beforeSnapshots = new List<SceneItemSnapshot>(selectedSnapshots.Count);
        var requestedSnapshots = new List<SceneItemSnapshot>(selectedSnapshots.Count);
        HashSet<Guid> transformedGroups = kind == SceneHistoryKind.Transform
            ? selectedSnapshots.OfType<SceneGroupSnapshot>().Select(static group => group.Id).ToHashSet()
            : [];
        long? expectedRevision = transformedGroups.Count > 0 ? _sceneItemService.GetRevisions().Persistent : null;
        ILookup<Guid?, ObjectSnapshot>? groupMembers = null;
        foreach (SceneItemSnapshot snapshot in selectedSnapshots)
        {
            if (snapshot is ObjectSnapshot { GroupId: { } groupId } && transformedGroups.Contains(groupId))
                continue;
            SceneItemSnapshot nextSnapshot = updateFactory(snapshot);
            if (Equals(snapshot, nextSnapshot))
            {
                continue;
            }

            if (snapshot is SceneGroupSnapshot && kind == SceneHistoryKind.Transform)
            {
                groupMembers ??= _sceneView.GetPlacedObjectSnapshots()
                    .Where(member => member.GroupId is Guid id && transformedGroups.Contains(id))
                    .ToLookup(static member => member.GroupId);
                if (!TryResolveItemChanges(snapshot, nextSnapshot, out IReadOnlyList<SceneItemSnapshot> before,
                        out IReadOnlyList<SceneItemSnapshot> after, groupMembers: groupMembers))
                    return false;
                beforeSnapshots.AddRange(before);
                requestedSnapshots.AddRange(after);
            }
            else
            {
                beforeSnapshots.Add(snapshot);
                requestedSnapshots.Add(nextSnapshot);
            }
        }

        return TryApplySceneChanges(kind, title, beforeSnapshots, requestedSnapshots, CaptureCurrentSelectionIds(), expectedRevision);
    }

    public bool TryDuplicateSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        CommitPendingInspectorEdits();
        var selectionBefore = CaptureCurrentSelectionIds();
        if (!_sceneItemService.DuplicateMany(
                selectedSnapshots,
                out IReadOnlyList<SceneItemSnapshot> duplicateSnapshots).IsApplied()
            || duplicateSnapshots.Count == 0)
        {
            return false;
        }

        var selectionAfterApply = duplicateSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToArray();
        return TryRecordCompletedAction(
            SceneHistoryKind.Create,
            duplicateSnapshots.Count == 1 ? "Duplicate Item" : "Duplicate Items",
            [],
            duplicateSnapshots,
            selectionAfterApply,
            selectionBefore);
    }

    public bool TryApplySceneChanges(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> before,
        IReadOnlyList<SceneItemSnapshot> after, IReadOnlyList<Guid> selectionAfter)
        => TryApplySceneChanges(kind, title, before, after, selectionAfter, null);

    private bool TryApplySceneChanges(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> before,
        IReadOnlyList<SceneItemSnapshot> after, IReadOnlyList<Guid> selectionAfter, long? expectedPersistentRevision)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();
        IReadOnlyList<Guid> selectionBefore = CaptureCurrentSelectionIds();
        return TryApplySnapshotChanges(before, after, out IReadOnlyList<SceneItemSnapshot> applied, expectedPersistentRevision)
            && TryRecordCompletedAction(kind, title, before, applied, selectionAfter, selectionBefore);
    }

    private bool TryApplySnapshotChanges(IReadOnlyList<SceneItemSnapshot> before,
        IReadOnlyList<SceneItemSnapshot> after, out IReadOnlyList<SceneItemSnapshot> applied, long? expectedPersistentRevision = null)
    {
        applied = [];
        IReadOnlyList<SceneItemSnapshotChange> changes = SceneItemHistoryChanges.Build(before, after);
        if (changes.Count == 0)
            return false;
        HashSet<SceneItemSnapshot> unchanged = [.. before];
        unchanged.IntersectWith(after);
        IReadOnlyList<SceneItemSnapshot> resolved = [];
        SceneMutationStatus status = unchanged.Count == 0 && !expectedPersistentRevision.HasValue
            ? _sceneItemService.ApplyChanges(changes, ResolveApplied)
            : _sceneItemService.ApplyChanges(changes, unchanged, ResolveApplied, expectedPersistentRevision);
        if (!status.IsApplied())
            return false;
        applied = resolved;
        return true;

        bool ResolveApplied()
            => _sceneItemService.TryResolvePlacedItems(after, out resolved);
    }

    public bool TryRemoveSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
        => TryApplySceneChanges(SceneHistoryKind.Remove, selectedSnapshots.Count == 1 ? "Remove Item" : "Remove Items",
            selectedSnapshots, [], []);

    public bool TryClearPlacedObjects(long persistentRevision, IReadOnlyList<ObjectSceneItemSnapshot> targets)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();

        if (_sceneItemService.GetRevisions().Persistent != persistentRevision || targets.Count == 0)
        {
            return false;
        }

        IReadOnlyList<Guid> selectionBefore = CaptureCurrentSelectionIds();
        HashSet<Guid> removedIds = targets.Select(static item => item.Id).ToHashSet();
        Guid[] selectionAfter = selectionBefore.Where(id => !removedIds.Contains(id)).ToArray();
        return TryApplySnapshotChanges(targets, [], out IReadOnlyList<SceneItemSnapshot> applied, persistentRevision)
            && TryRecordCompletedAction(SceneHistoryKind.Clear, "Clear Placed Objects", targets, applied, selectionAfter, selectionBefore);
    }

    public bool TryApplyFolderChange(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> requestedSnapshots, ObjectFolderSceneState beforeFolders, ObjectFolderSceneState afterFolders,
        IReadOnlyList<Guid>? selectionAfterApply = null, IReadOnlyList<Guid>? selectionAfterRevert = null)
    {
        using Lock.Scope scope = _stateLock.EnterScope();

        if (SceneItemHistoryChanges.Build(beforeSnapshots, requestedSnapshots).Count == 0
            && ObjectFolderSceneStateUtility.StatesMatch(beforeFolders, afterFolders))
        {
            return false;
        }

        CommitPendingInspectorEdits();
        IReadOnlyList<SceneItemSnapshot> stored = _sceneItemService.GetStoredItems();

        if (!_organizationService.ApplyFolderChange(beforeSnapshots, requestedSnapshots, beforeFolders, afterFolders,
                out IReadOnlyList<SceneItemSnapshot> applied).IsApplied())
        {
            return false;
        }

        SceneItemHistoryChange items = CaptureItemHistory(title, beforeSnapshots, applied, stored);

        if (!items.HasChanges && ObjectFolderSceneStateUtility.StatesMatch(beforeFolders, afterFolders))
        {
            return false;
        }

        ISceneHistoryAction action = new ObjectOrganizationHistoryAction(_organizationService, items, kind, beforeFolders, afterFolders);

        RecordCompletedAction(action, selectionAfterApply, selectionAfterRevert);
        return true;
    }

    private SceneItemHistoryChange CaptureItemHistory(string title, IReadOnlyList<SceneItemSnapshot> before,
        IReadOnlyList<SceneItemSnapshot> after, IReadOnlyList<SceneItemSnapshot> stored)
        => new(_sceneItemService, title, SceneItemHistoryChanges.Build(before, after), before.Intersect(after).ToArray(), stored);

    public bool TryCreateDisplay(SceneTransform transform)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        CommitPendingInspectorEdits();
        IReadOnlyList<Guid> selectionBefore = CaptureCurrentSelectionIds();
        return _displayService.TryCreate(new DisplaySettings(), transform, true, out DisplaySnapshot created)
            && TryRecordCompletedAction(SceneHistoryKind.Create, "Create Display", [], [created], [created.Id], selectionBefore);
    }

    public bool TryCommitSceneEdit(SceneEditSession edit, SceneHistoryKind kind, string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots, IReadOnlyList<SceneItemSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        SceneItemHistoryChange change = CaptureItemHistory(title, beforeSnapshots, afterSnapshots, _sceneItemService.GetStoredItems());
        return edit.Commit().IsApplied() && RecordItemHistory(kind, change, selectionAfterApply, selectionAfterRevert);
    }

    private bool TryRecordCompletedAction(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        return RecordItemHistory(kind, CaptureItemHistory(title, beforeSnapshots, afterSnapshots, _sceneItemService.GetStoredItems()),
            selectionAfterApply, selectionAfterRevert);
    }

    private bool RecordItemHistory(SceneHistoryKind kind, SceneItemHistoryChange change,
        IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert)
    {
        if (!change.HasChanges)
        {
            return false;
        }

        RecordCompletedAction(new SceneItemHistoryAction(kind, change), selectionAfterApply, selectionAfterRevert);
        return true;
    }

    private void RecordCompletedAction(ISceneHistoryAction action,
        IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert)
        => _historyManager.RecordCompleted(selectionAfterApply is null && selectionAfterRevert is null
            ? action : new SelectionHistoryAction(action, selectionAfterApply, selectionAfterRevert));

    public void Dispose()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        _historyManager.ActionApplied -= HandleHistoryActionApplied;
        _historyManager.ActionReverted -= HandleHistoryActionReverted;
        _pendingInspectorEdit = null;
        DisconnectSelectionHandlers();
    }

    private void HandleHistoryActionApplied(ISceneHistoryAction action)
    {
        if (action is SelectionHistoryAction selectionAction)
        {
            ApplyHistorySelection(selectionAction.SelectionAfterApply);
        }
    }

    private void HandleHistoryActionReverted(ISceneHistoryAction action)
    {
        if (action is SelectionHistoryAction selectionAction)
        {
            ApplyHistorySelection(selectionAction.SelectionAfterRevert);
        }
    }

    private void ApplyHistorySelection(IReadOnlyList<Guid>? selectionIds)
    {
        if (selectionIds is null || _applySelectionIds is null)
        {
            return;
        }

        if (selectionIds.Count == 0)
        {
            _applySelectionIds(Array.Empty<Guid>());
            return;
        }

        var validItemIds = _sceneItemService.GetPlacedItems()
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        var resolvedSelection = selectionIds
            .Where(validItemIds.Contains)
            .ToArray();
        _applySelectionIds(resolvedSelection);
    }

    private IReadOnlyList<Guid> CaptureCurrentSelectionIds()
        => _captureSelectionIds?.Invoke() ?? Array.Empty<Guid>();

    private bool TryReplayHistory(Func<bool> replay)
    {
        lock (_stateLock)
        {
            CommitPendingInspectorEdits();
            long revision = _sceneItemService.GetStoredRevision();
            bool replayed = replay();

            if (_sceneItemService.GetStoredRevision() != revision)
            {
                _sceneItemService.ReconcileStoredChanges();
            }

            return replayed;
        }
    }
}
