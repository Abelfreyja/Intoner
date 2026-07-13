using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.UI.Services;

/// <summary> coordinates object editor undo and redo recording around editor mutations </summary>
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
    bool RefreshContext(ObjectCreationContext currentContext, Action commitPendingChanges);

    /// <summary> forces any pending inspector edits to become one history step </summary>
    void CommitPendingInspectorEdits();

    /// <summary> applies one inspector edit and records it when the edit is finished </summary>
    /// <param name="editId">stable editor local id for the edited field</param>
    /// <param name="kind">main history kind for the edit</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="startSnapshot">snapshot captured before the edit started</param>
    /// <param name="nextSnapshot">latest replacement snapshot to apply</param>
    /// <param name="recordImmediately">whether the edit should record immediately instead of waiting for deactivation</param>
    void ApplyInspectorSnapshotEdit(string editId, ObjectHistoryKind kind, string title, ObjectSnapshot startSnapshot, ObjectSnapshot? nextSnapshot, bool recordImmediately = false);

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

    /// <summary> tries to import one snapshot and record the change </summary>
    /// <param name="snapshot">the snapshot to import</param>
    /// <returns>true when the snapshot was imported and recorded</returns>
    bool TryImportObjectSnapshot(ObjectSnapshot snapshot);

    /// <summary> tries to move one object to the player and record the change </summary>
    /// <param name="objectId">the object id to move</param>
    /// <returns>true when the move succeeded and was recorded</returns>
    bool TryMoveObjectToPlayer(Guid objectId);

    /// <summary> tries to apply one selected object update batch and record the change </summary>
    /// <param name="kind">main history kind for the update</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="selectedSnapshots">current selected snapshots</param>
    /// <param name="updateFactory">builds the next snapshot for each selected object</param>
    /// <returns>true when the batch update succeeded and was recorded</returns>
    bool TryApplySelectedSnapshotUpdate(ObjectHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> selectedSnapshots, Func<ObjectSnapshot, ObjectSnapshot> updateFactory);

    /// <summary> tries to duplicate multiple selected objects and record the change </summary>
    /// <param name="selectedSnapshots">the selected snapshots to duplicate</param>
    /// <returns>true when the duplicate batch succeeded and was recorded</returns>
    bool TryDuplicateObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots);

    /// <summary> tries to remove multiple selected objects and record the change </summary>
    /// <param name="selectedSnapshots">the selected snapshots to remove</param>
    /// <returns>true when the remove batch succeeded and was recorded</returns>
    bool TryRemoveObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots);

    /// <summary> tries to clear all placed objects and record the change </summary>
    /// <returns>true when the clear succeeded and was recorded</returns>
    bool TryClearPlacedObjects();

    /// <summary> records one completed before and after snapshot change </summary>
    /// <param name="kind">main history kind for the change</param>
    /// <param name="title">display title for the history entry</param>
    /// <param name="beforeSnapshots">snapshots captured before the change</param>
    /// <param name="afterSnapshots">snapshots captured after the change</param>
    /// <param name="selectionAfterApply">optional selection to restore after apply or redo</param>
    /// <param name="selectionAfterRevert">optional selection to restore after revert or undo</param>
    /// <returns>true when a replayable history action was recorded</returns>
    bool TryRecordCompletedAction(ObjectHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> beforeSnapshots, IReadOnlyList<ObjectSnapshot> afterSnapshots, IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert);
}

internal sealed class HistoryCoordinator : IHistoryCoordinator
{
    private sealed class SelectionRestoreAction : ObjectHistoryActionBase
    {
        private readonly IObjectHistoryAction _action;

        public SelectionRestoreAction(
            IObjectHistoryAction action,
            IReadOnlyList<Guid>? selectionAfterApply,
            IReadOnlyList<Guid>? selectionAfterRevert)
            : base(action.Title, action.Kind)
        {
            _action = action;
            SelectionAfterApply = selectionAfterApply is not null ? [.. selectionAfterApply] : null;
            SelectionAfterRevert = selectionAfterRevert is not null ? [.. selectionAfterRevert] : null;
        }

        public IReadOnlyList<Guid>? SelectionAfterApply { get; }
        public IReadOnlyList<Guid>? SelectionAfterRevert { get; }

        protected override void ApplyCore()
            => _action.Apply();

        protected override void RevertCore()
            => _action.Revert();

        public override void Dispose()
            => _action.Dispose();

        internal override void MarkRecordedApplied()
        {
            base.MarkRecordedApplied();
            if (_action is ObjectHistoryActionBase historyAction)
            {
                historyAction.MarkRecordedApplied();
            }
        }
    }

    private sealed class PendingInspectorEdit
    {
        public PendingInspectorEdit(
            string title,
            ObjectHistoryKind kind,
            Guid objectId,
            ObjectSnapshot startSnapshot,
            ObjectSnapshot latestSnapshot,
            IReadOnlyList<Guid> selectionIds)
        {
            Title = title;
            Kind = kind;
            ObjectId = objectId;
            StartSnapshot = startSnapshot;
            LatestSnapshot = latestSnapshot;
            SelectionIds = [.. selectionIds];
        }

        public string Title { get; }
        public ObjectHistoryKind Kind { get; }
        public Guid ObjectId { get; }
        public ObjectSnapshot StartSnapshot { get; }
        public ObjectSnapshot LatestSnapshot { get; set; }
        public Guid[] SelectionIds { get; }
    }

    private readonly ILogger<HistoryCoordinator> _logger;
    private readonly IObjectHistoryManager _historyManager;
    private readonly IObjectMutationService _mutationService;
    private readonly IObjectSceneView _sceneView;
    private readonly Dictionary<string, PendingInspectorEdit> _pendingInspectorEdits = new(StringComparer.Ordinal);

    private Func<IReadOnlyList<Guid>>? _captureSelectionIds;
    private Action<IReadOnlyList<Guid>?>? _applySelectionIds;
    private ObjectCreationContext? _currentContext;
    private long _historyPersistentSceneRevision;

    public HistoryCoordinator(
        ILogger<HistoryCoordinator> logger,
        IObjectHistoryManager historyManager,
        IObjectMutationService mutationService,
        IObjectSceneView sceneView)
    {
        _logger = logger;
        _historyManager = historyManager;
        _mutationService = mutationService;
        _sceneView = sceneView;
        _historyPersistentSceneRevision = _sceneView.GetPersistentSceneRevision();

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

    public bool RefreshContext(ObjectCreationContext currentContext, Action commitPendingChanges)
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
        if (_pendingInspectorEdits.Count == 0)
        {
            return;
        }

        foreach (var editId in _pendingInspectorEdits.Keys.ToArray())
        {
            FinalizeInspectorSnapshotEdit(editId, force: true);
        }
    }

    public void ApplyInspectorSnapshotEdit(string editId, ObjectHistoryKind kind, string title, ObjectSnapshot startSnapshot, ObjectSnapshot? nextSnapshot, bool recordImmediately = false)
    {
        if (_pendingInspectorEdits.TryGetValue(editId, out var existingEdit) && existingEdit.ObjectId != startSnapshot.Id)
        {
            FinalizeInspectorSnapshotEdit(editId, force: true);
        }

        if (nextSnapshot is not null)
        {
            ResetHistoryForUntrackedPersistentChange();
            if (!_mutationService.TryUpdate(nextSnapshot, out var appliedSnapshot))
            {
                return;
            }

            TrackPersistentSceneRevision();
            if (!_pendingInspectorEdits.TryGetValue(editId, out var pendingEdit))
            {
                _pendingInspectorEdits.Add(
                    editId,
                    new PendingInspectorEdit(title, kind, startSnapshot.Id, startSnapshot, appliedSnapshot, CaptureCurrentSelectionIds()));
            }
            else
            {
                pendingEdit.LatestSnapshot = appliedSnapshot;
            }
        }

        FinalizeInspectorSnapshotEdit(editId, force: recordImmediately);
    }

    public bool TryUndo()
        => TryReplayHistory(_historyManager.Undo);

    public bool TryRedo()
        => TryReplayHistory(_historyManager.Redo);

    public bool TryJumpToState(int stateIndex)
        => TryReplayHistory(() => _historyManager.TryJumpToState(stateIndex));

    public bool TryCreateObject(string title, ObjectKind kind, ObjectPlacementOverrides? overrides)
    {
        ResetHistoryForUntrackedPersistentChange();
        var selectionBefore = CaptureCurrentSelectionIds();
        var createdId = _mutationService.CreateObjectAtPlayer(kind, out var createdSnapshot, overrides);
        if (!createdId.HasValue)
        {
            return false;
        }

        return TryRecordCompletedAction(ObjectHistoryKind.Create, title, [], [createdSnapshot], [createdId.Value], selectionBefore);
    }

    public bool TryImportObjectSnapshot(ObjectSnapshot snapshot)
    {
        ResetHistoryForUntrackedPersistentChange();
        var selectionBefore = CaptureCurrentSelectionIds();
        var importedId = _mutationService.ImportObjectSnapshot(snapshot, out var importedSnapshot);
        if (!importedId.HasValue)
        {
            return false;
        }

        return TryRecordCompletedAction(ObjectHistoryKind.Import, "Import Object", [], [importedSnapshot], [importedId.Value], selectionBefore);
    }

    public bool TryMoveObjectToPlayer(Guid objectId)
    {
        ResetHistoryForUntrackedPersistentChange();
        if (!_sceneView.TryGetSceneObjectSnapshot(objectId, out var beforeSnapshot)
            && !_sceneView.TryGetPersistedObjectSnapshot(objectId, out beforeSnapshot))
        {
            return false;
        }

        var selectionIds = CaptureCurrentSelectionIds();
        if (!_mutationService.TryMoveToPlayer(objectId, out var movedSnapshot))
        {
            return false;
        }

        return TryRecordCompletedAction(ObjectHistoryKind.Move, "Move Object To Player", [beforeSnapshot], [movedSnapshot], selectionIds, selectionIds);
    }

    public bool TryApplySelectedSnapshotUpdate(ObjectHistoryKind kind, string title, IReadOnlyList<ObjectSnapshot> selectedSnapshots, Func<ObjectSnapshot, ObjectSnapshot> updateFactory)
    {
        ResetHistoryForUntrackedPersistentChange();
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        var beforeSnapshots = new List<ObjectSnapshot>(selectedSnapshots.Count);
        var requestedSnapshots = new List<ObjectSnapshot>(selectedSnapshots.Count);
        foreach (var snapshot in selectedSnapshots)
        {
            var nextSnapshot = updateFactory(snapshot);
            if (Equals(snapshot, nextSnapshot))
            {
                continue;
            }

            beforeSnapshots.Add(snapshot);
            requestedSnapshots.Add(nextSnapshot);
        }

        if (requestedSnapshots.Count == 0
            || !_mutationService.TryUpdateMany(requestedSnapshots, out var afterSnapshots))
        {
            return false;
        }

        var selectionIds = CaptureCurrentSelectionIds();
        return TryRecordCompletedAction(kind, title, beforeSnapshots, afterSnapshots, selectionIds, selectionIds);
    }

    public bool TryDuplicateObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
    {
        ResetHistoryForUntrackedPersistentChange();
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        var selectionBefore = CaptureCurrentSelectionIds();
        var selectedIds = selectedSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToArray();
        if (!_mutationService.TryDuplicateMany(selectedIds, out var duplicateSnapshots)
            || duplicateSnapshots.Count == 0)
        {
            return false;
        }

        var selectionAfterApply = duplicateSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToArray();
        return TryRecordCompletedAction(
            ObjectHistoryKind.Create,
            duplicateSnapshots.Count == 1 ? "Duplicate Object" : "Duplicate Objects",
            [],
            duplicateSnapshots,
            selectionAfterApply,
            selectionBefore);
    }

    public bool TryRemoveObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
    {
        ResetHistoryForUntrackedPersistentChange();
        if (selectedSnapshots.Count == 0)
        {
            return false;
        }

        var selectionBefore = CaptureCurrentSelectionIds();
        var selectedIds = selectedSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToArray();
        if (!_mutationService.TryRemoveMany(selectedIds, out var removedSnapshots)
            || removedSnapshots.Count == 0)
        {
            return false;
        }

        return TryRecordCompletedAction(
            ObjectHistoryKind.Remove,
            removedSnapshots.Count == 1 ? "Remove Object" : "Remove Objects",
            removedSnapshots,
            [],
            Array.Empty<Guid>(),
            selectionBefore);
    }

    public bool TryClearPlacedObjects()
    {
        ResetHistoryForUntrackedPersistentChange();
        var persistedSnapshots = _sceneView.GetPlacedObjectSnapshots();
        if (persistedSnapshots.Count == 0)
        {
            return false;
        }

        var selectionBefore = CaptureCurrentSelectionIds();
        var persistedIds = persistedSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToArray();
        if (!_mutationService.TryRemoveMany(persistedIds, out var removedSnapshots)
            || removedSnapshots.Count == 0)
        {
            return false;
        }

        return TryRecordCompletedAction(ObjectHistoryKind.Clear, "Clear Placed Objects", removedSnapshots, [], Array.Empty<Guid>(), selectionBefore);
    }

    public bool TryRecordCompletedAction(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
    {
        var changes = ObjectSnapshotHistoryChanges.Build(beforeSnapshots, afterSnapshots);
        if (changes.Count == 0)
        {
            var kindLabel = ObjectHistoryDescription.GetKindLabel(kind);
            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.LogError("object history could not build replayable changes for {HistoryKind}", kindLabel);
            }
            else
            {
                _logger.LogError(
                    "object history could not build replayable changes for {HistoryKind} ({HistoryTitle})",
                    kindLabel,
                    title.Trim());
            }

            return false;
        }

        _historyManager.RecordCompleted(
            new SelectionRestoreAction(
                new ObjectSnapshotHistoryAction(_mutationService, kind, title, changes),
                selectionAfterApply,
                selectionAfterRevert));
        TrackPersistentSceneRevision();
        return true;
    }

    public void Dispose()
    {
        _historyManager.ActionApplied -= HandleHistoryActionApplied;
        _historyManager.ActionReverted -= HandleHistoryActionReverted;
        _pendingInspectorEdits.Clear();
        DisconnectSelectionHandlers();
    }

    private void HandleHistoryActionApplied(IObjectHistoryAction action)
    {
        if (action is SelectionRestoreAction selectionAction)
        {
            ApplyHistorySelection(selectionAction.SelectionAfterApply);
        }
    }

    private void HandleHistoryActionReverted(IObjectHistoryAction action)
    {
        if (action is SelectionRestoreAction selectionAction)
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

        var validObjectIds = _sceneView.GetPlacedObjectSnapshots()
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        var resolvedSelection = selectionIds
            .Where(validObjectIds.Contains)
            .ToArray();
        _applySelectionIds(resolvedSelection);
    }

    private IReadOnlyList<Guid> CaptureCurrentSelectionIds()
        => _captureSelectionIds?.Invoke() ?? Array.Empty<Guid>();

    private void FinalizeInspectorSnapshotEdit(string editId, bool force)
    {
        if (!_pendingInspectorEdits.TryGetValue(editId, out var edit)
            || (!force && !ImGui.IsItemDeactivatedAfterEdit()))
        {
            return;
        }

        if (ResetHistoryForUntrackedPersistentChange())
        {
            return;
        }

        _pendingInspectorEdits.Remove(editId);
        _ = TryRecordCompletedAction(
            edit.Kind,
            edit.Title,
            [edit.StartSnapshot],
            [edit.LatestSnapshot],
            edit.SelectionIds,
            edit.SelectionIds);
    }

    private bool TryReplayHistory(Func<bool> replay)
    {
        if (ResetHistoryForUntrackedPersistentChange())
        {
            return false;
        }

        bool replayed = replay();
        TrackPersistentSceneRevision();
        return replayed;
    }

    private bool ResetHistoryForUntrackedPersistentChange()
    {
        long persistentSceneRevision = _sceneView.GetPersistentSceneRevision();
        if (_historyPersistentSceneRevision == persistentSceneRevision)
        {
            return false;
        }

        bool historyWasActive = _historyManager.UndoActionKind is not null
            || _historyManager.RedoActionKind is not null
            || _pendingInspectorEdits.Count > 0;
        if (historyWasActive)
        {
            _pendingInspectorEdits.Clear();
            _historyManager.ClearHistory();
            _logger.LogDebug("cleared object history after an untracked persistent scene change");
        }

        _historyPersistentSceneRevision = persistentSceneRevision;
        return historyWasActive;
    }

    private void TrackPersistentSceneRevision()
        => _historyPersistentSceneRevision = _sceneView.GetPersistentSceneRevision();
}
