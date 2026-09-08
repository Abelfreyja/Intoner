using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.UI.Services;

/// <summary> coordinates scene editor undo and redo recording around editor mutations </summary>
internal interface IHistoryCoordinator : IDisposable
{
    /// <summary> connects selection callbacks used for history restore and capture </summary>
    /// <param name="captureSelectionIds">reads the current editor selection ids</param>
    /// <param name="applySelectionIds">applies a restored editor selection</param>
    void ConnectSelectionHandlers(Func<IReadOnlyList<Guid>> captureSelectionIds, Action<IReadOnlyList<Guid>?> applySelectionIds);

    /// <summary> clears any connected selection callbacks </summary>
    void DisconnectSelectionHandlers();

    /// <summary> refreshes the active creation context and clears history when the location changes </summary>
    /// <param name="currentContext">the latest object creation context</param>
    /// <param name="commitPendingChanges">commits any pending editor change before the context swap</param>
    /// <returns>true when the context changed and history was reset</returns>
    bool RefreshContext(SceneCreationContext currentContext, Action commitPendingChanges);

    /// <summary> forces any pending inspector edits to become one history step </summary>
    void CommitPendingInspectorEdits();

    /// <summary> commits pending edits and validates history before a tracked persistent mutation starts </summary>
    void PrepareForMutation();

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
    bool TryJumpToState(int stateIndex);

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

    /// <summary> changes only an object's world and records the applied location for undo </summary>
    /// <param name="snapshot"> the unchanged object snapshot shown by the location editor </param>
    /// <param name="world"> the destination world resolved from the game sheets </param>
    /// <returns> true when the checked world change succeeded and was recorded </returns>
    bool TryChangeObjectWorld(ObjectSnapshot snapshot, SceneWorldInfo world);

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

    /// <summary> tries to clear all placed objects in the confirmed scene revision and record the change </summary>
    /// <param name="persistentRevision">the persistent scene revision shown when the clear was confirmed</param>
    /// <returns>true when the clear succeeded and was recorded</returns>
    bool TryClearPlacedObjects(long persistentRevision);

    /// <summary> records one completed before and after snapshot change </summary>
    /// <param name="kind">main history kind for the change</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="beforeSnapshots">snapshots captured before the change</param>
    /// <param name="afterSnapshots">snapshots captured after the change</param>
    /// <param name="selectionAfterApply">optional selection to restore after apply or redo</param>
    /// <param name="selectionAfterRevert">optional selection to restore after revert or undo</param>
    /// <returns>true when a replayable history action was recorded</returns>
    bool TryRecordCompletedAction(SceneHistoryKind kind, string title, IReadOnlyList<SceneItemSnapshot> beforeSnapshots, IReadOnlyList<SceneItemSnapshot> afterSnapshots, IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert);

    /// <summary> records an already applied history action and synchronizes persistent revision tracking </summary>
    /// <param name="action">the completed action to record</param>
    void RecordCompletedAction(ISceneHistoryAction action);
}

internal sealed class HistoryCoordinator : IHistoryCoordinator
{
    private sealed class PendingInspectorEdit
    {
        public PendingInspectorEdit(
            string editId,
            string title,
            SceneHistoryKind kind,
            Guid itemId,
            SceneItemSnapshot startSnapshot,
            SceneItemSnapshot latestSnapshot,
            IReadOnlyList<Guid> selectionIds)
        {
            EditId = editId;
            Title = title;
            Kind = kind;
            ItemId = itemId;
            StartSnapshot = startSnapshot;
            LatestSnapshot = latestSnapshot;
            SelectionIds = [.. selectionIds];
        }

        public string EditId { get; }
        public string Title { get; }
        public SceneHistoryKind Kind { get; }
        public Guid ItemId { get; }
        public SceneItemSnapshot StartSnapshot { get; }
        public SceneItemSnapshot LatestSnapshot { get; set; }
        public Guid[] SelectionIds { get; }
    }

    private readonly ILogger<HistoryCoordinator> _logger;
    private readonly ISceneHistoryManager _historyManager;
    private readonly IObjectMutationService _mutationService;
    private readonly IObjectOrganizationService _organizationService;
    private readonly IObjectSceneView _sceneView;
    private readonly ISceneItemService _sceneItemService;
    private readonly IScenePlacementService _placementService;
    private PendingInspectorEdit? _pendingInspectorEdit;

    private Func<IReadOnlyList<Guid>>? _captureSelectionIds;
    private Action<IReadOnlyList<Guid>?>? _applySelectionIds;
    private SceneCreationContext? _currentContext;
    private long _historyPersistentSceneRevision;

    public HistoryCoordinator(
        ILogger<HistoryCoordinator> logger,
        ISceneHistoryManager historyManager,
        IObjectMutationService mutationService,
        IObjectOrganizationService organizationService,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService,
        IScenePlacementService placementService)
    {
        _logger = logger;
        _historyManager = historyManager;
        _mutationService = mutationService;
        _organizationService = organizationService;
        _sceneView = sceneView;
        _sceneItemService = sceneItemService;
        _placementService = placementService;
        _historyPersistentSceneRevision = _sceneItemService.GetRevisions().Persistent;

        _historyManager.ActionApplied += HandleHistoryActionApplied;
        _historyManager.ActionReverted += HandleHistoryActionReverted;
    }

    public void ConnectSelectionHandlers(Func<IReadOnlyList<Guid>> captureSelectionIds, Action<IReadOnlyList<Guid>?> applySelectionIds)
    {
        ArgumentNullException.ThrowIfNull(captureSelectionIds);
        ArgumentNullException.ThrowIfNull(applySelectionIds);

        _captureSelectionIds = captureSelectionIds;
        _applySelectionIds = applySelectionIds;
    }

    public void DisconnectSelectionHandlers()
    {
        _captureSelectionIds = null;
        _applySelectionIds = null;
    }

    public bool RefreshContext(SceneCreationContext currentContext, Action commitPendingChanges)
    {
        ArgumentNullException.ThrowIfNull(currentContext);
        ArgumentNullException.ThrowIfNull(commitPendingChanges);

        if (Equals(_currentContext, currentContext))
        {
            return false;
        }

        commitPendingChanges();
        _historyManager.ClearHistory();
        TrackPersistentSceneRevision();
        _currentContext = currentContext;
        return true;
    }

    public void CommitPendingInspectorEdits()
    {
        if (_pendingInspectorEdit is null)
        {
            return;
        }

        FinalizeInspectorSnapshotEdit();
    }

    public void PrepareForMutation()
    {
        CommitPendingInspectorEdits();
        ResetHistoryForUntrackedPersistentChange();
    }

    public void ApplyInspectorSnapshotEdit(string editId, SceneHistoryKind kind, string title, SceneItemSnapshot startSnapshot, SceneItemSnapshot? nextSnapshot, bool recordImmediately = false)
    {
        if (_pendingInspectorEdit is not null
            && (!string.Equals(_pendingInspectorEdit.EditId, editId, StringComparison.Ordinal)
                || _pendingInspectorEdit.ItemId != startSnapshot.Id))
        {
            FinalizeInspectorSnapshotEdit();
        }

        if (nextSnapshot is not null)
        {
            ResetHistoryForUntrackedPersistentChange();
            if (!_sceneItemService.Update(nextSnapshot, out SceneItemSnapshot appliedSnapshot).IsApplied())
            {
                return;
            }

            TrackPersistentSceneRevision();
            if (_pendingInspectorEdit is null)
            {
                _pendingInspectorEdit = new PendingInspectorEdit(
                    editId,
                    title,
                    kind,
                    startSnapshot.Id,
                    startSnapshot,
                    appliedSnapshot,
                    CaptureCurrentSelectionIds());
            }
            else
            {
                _pendingInspectorEdit.LatestSnapshot = appliedSnapshot;
            }
        }

        if (recordImmediately)
        {
            FinalizeInspectorSnapshotEdit();
        }
    }

    public bool TryUndo()
        => TryReplayHistory(_historyManager.Undo);

    public bool TryRedo()
        => TryReplayHistory(_historyManager.Redo);

    public bool TryJumpToState(int stateIndex)
        => TryReplayHistory(() => _historyManager.TryJumpToState(stateIndex));

    public bool TryCreateObject(string title, ObjectKind kind, ObjectPlacementOverrides? overrides)
    {
        PrepareForMutation();
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

        PrepareForMutation();
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
        PrepareForMutation();
        if (!_sceneItemService.TryGetPlacedItem(itemId, out SceneItemSnapshot beforeSnapshot)
            || !_placementService.TryResolveFromPlayer(out SceneTransform placement))
        {
            return false;
        }

        var selectionIds = CaptureCurrentSelectionIds();
        SceneItemSnapshot requestedSnapshot = beforeSnapshot with
        {
            Transform = beforeSnapshot.Transform with
            {
                Position = placement.Position,
                RotationDegrees = placement.RotationDegrees,
            },
        };
        if (!_sceneItemService.Update(requestedSnapshot, out SceneItemSnapshot movedSnapshot).IsApplied())
        {
            return false;
        }

        return TryRecordCompletedAction(
            SceneHistoryKind.Move,
            "Move Item To Player",
            [beforeSnapshot],
            [movedSnapshot],
            selectionIds,
            selectionIds);
    }

    public bool TryApplySelectedSnapshotUpdate(SceneHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> selectedSnapshots, Func<ObjectSnapshot, ObjectSnapshot> updateFactory)
        => TryApplySceneItemUpdate(
            kind,
            title,
            selectedSnapshots,
            snapshot => updateFactory((ObjectSnapshot)snapshot));

    public bool TryChangeObjectWorld(ObjectSnapshot snapshot, SceneWorldInfo world)
    {
        if (!snapshot.CreatedIn.Scope.IsValid || world.Id == 0 || world.Id == snapshot.CreatedIn.WorldId)
        {
            return false;
        }

        PrepareForMutation();
        ObjectSnapshot requested = snapshot with
        {
            CreatedIn = snapshot.CreatedIn with { WorldId = world.Id, WorldName = world.Name },
        };
        SceneItemSnapshot applied = null!;
        SceneMutationStatus status = _sceneItemService.ApplyChanges(
            [new SceneItemSnapshotChange(snapshot, requested)],
            () => _sceneItemService.TryGetPlacedItem(snapshot.Id, out applied));
        if (!status.IsApplied())
        {
            return false;
        }

        IReadOnlyList<Guid> selectionIds = CaptureCurrentSelectionIds();
        return TryRecordCompletedAction(
            SceneHistoryKind.Organization,
            "Change Object World",
            [snapshot],
            [applied],
            selectionIds,
            selectionIds);
    }

    public bool TryApplySceneItemUpdate(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        Func<SceneItemSnapshot, SceneItemSnapshot> updateFactory)
    {
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        PrepareForMutation();
        var beforeSnapshots = new List<SceneItemSnapshot>(selectedSnapshots.Count);
        var requestedSnapshots = new List<SceneItemSnapshot>(selectedSnapshots.Count);
        foreach (SceneItemSnapshot snapshot in selectedSnapshots)
        {
            SceneItemSnapshot nextSnapshot = updateFactory(snapshot);
            if (Equals(snapshot, nextSnapshot))
            {
                continue;
            }

            beforeSnapshots.Add(snapshot);
            requestedSnapshots.Add(nextSnapshot);
        }

        if (requestedSnapshots.Count == 0
            || !_sceneItemService.UpdateMany(
                requestedSnapshots,
                out IReadOnlyList<SceneItemSnapshot> afterSnapshots).IsApplied())
        {
            return false;
        }

        var selectionIds = CaptureCurrentSelectionIds();
        return TryRecordCompletedAction(kind, title, beforeSnapshots, afterSnapshots, selectionIds, selectionIds);
    }

    public bool TryDuplicateSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
    {
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        PrepareForMutation();
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

    public bool TryRemoveSceneItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
    {
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        PrepareForMutation();
        var selectionBefore = CaptureCurrentSelectionIds();
        IReadOnlyList<SceneItemSnapshotChange> changes = selectedSnapshots
            .Select(static snapshot => new SceneItemSnapshotChange(snapshot, null))
            .ToList();
        if (!_sceneItemService.ApplyChanges(changes).IsApplied())
        {
            return false;
        }

        return TryRecordCompletedAction(
            SceneHistoryKind.Remove,
            selectedSnapshots.Count == 1 ? "Remove Item" : "Remove Items",
            selectedSnapshots,
            [],
            Array.Empty<Guid>(),
            selectionBefore);
    }

    public bool TryClearPlacedObjects(long persistentRevision)
    {
        PrepareForMutation();
        if (_sceneItemService.GetRevisions().Persistent != persistentRevision)
        {
            return false;
        }

        var persistedSnapshots = _sceneView.GetPlacedObjectSnapshots();
        if (_sceneItemService.GetRevisions().Persistent != persistentRevision || persistedSnapshots.Count == 0)
        {
            return false;
        }

        var selectionBefore = CaptureCurrentSelectionIds();
        IReadOnlyList<SceneItemSnapshotChange> changes = persistedSnapshots
            .Select(static snapshot => new SceneItemSnapshotChange(snapshot, null))
            .ToList();
        if (!_sceneItemService.ApplyChanges(changes).IsApplied())
        {
            return false;
        }

        return TryRecordCompletedAction(
            SceneHistoryKind.Clear,
            "Clear Placed Objects",
            persistedSnapshots,
            [],
            Array.Empty<Guid>(),
            selectionBefore);
    }

    public bool TryRecordCompletedAction(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
    {
        IReadOnlyList<SceneItemSnapshotChange> changes = SceneItemHistoryChanges.Build(
            beforeSnapshots,
            afterSnapshots);
        if (changes.Count == 0)
        {
            var kindLabel = SceneHistoryDescription.GetKindLabel(kind);
            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.LogError("scene history could not build replayable changes for {HistoryKind}", kindLabel);
            }
            else
            {
                _logger.LogError(
                    "scene history could not build replayable changes for {HistoryKind} ({HistoryTitle})",
                    kindLabel,
                    title.Trim());
            }

            return false;
        }

        RecordCompletedAction(
            new SelectionHistoryAction(
                new SceneItemHistoryAction(_sceneItemService, kind, title, changes),
                selectionAfterApply,
                selectionAfterRevert));
        return true;
    }

    public void RecordCompletedAction(ISceneHistoryAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // selection restoration runs synchronously while recording, so track first
        // to avoid treating the completed mutation as an external scene change
        TrackPersistentSceneRevision();
        _historyManager.RecordCompleted(action);
        TrackPersistentSceneRevision();
    }

    public void Dispose()
    {
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

    private void FinalizeInspectorSnapshotEdit()
    {
        if (_pendingInspectorEdit is not { } edit)
        {
            return;
        }

        if (ResetHistoryForUntrackedPersistentChange())
        {
            return;
        }

        _pendingInspectorEdit = null;
        IReadOnlyList<SceneItemSnapshotChange> changes = SceneItemHistoryChanges.Build(
            [edit.StartSnapshot],
            [edit.LatestSnapshot]);
        if (changes.Count > 0)
        {
            RecordCompletedAction(
                new SelectionHistoryAction(
                    new SceneItemHistoryAction(_sceneItemService, edit.Kind, edit.Title, changes),
                    edit.SelectionIds,
                    edit.SelectionIds));
        }
    }

    private bool TryReplayHistory(Func<bool> replay)
    {
        PrepareForMutation();

        bool replayed = replay();
        TrackPersistentSceneRevision();
        return replayed;
    }

    private bool ResetHistoryForUntrackedPersistentChange()
    {
        long persistentSceneRevision = _sceneItemService.GetRevisions().Persistent;
        if (_historyPersistentSceneRevision == persistentSceneRevision)
        {
            return false;
        }

        bool historyWasActive = _historyManager.UndoActionKind is not null
            || _historyManager.RedoActionKind is not null
            || _pendingInspectorEdit is not null;
        if (historyWasActive)
        {
            _pendingInspectorEdit = null;
            _historyManager.ClearHistory();
            _logger.LogDebug(
                "cleared scene history after persistent scene revision changed outside history from {TrackedRevision} to {CurrentRevision}",
                _historyPersistentSceneRevision,
                persistentSceneRevision);
        }

        _historyPersistentSceneRevision = persistentSceneRevision;
        return historyWasActive;
    }

    private void TrackPersistentSceneRevision()
        => _historyPersistentSceneRevision = _sceneItemService.GetRevisions().Persistent;
}
