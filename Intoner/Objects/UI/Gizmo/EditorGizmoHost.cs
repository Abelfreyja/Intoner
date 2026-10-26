using Intoner.Objects.Models;
using Intoner.Objects.UI.Services;
using Intoner.Scene;

namespace Intoner.Objects.UI;

internal sealed class EditorGizmoHost : IGizmoHost
{
    private readonly EditorSelectionService _editorSelection;
    private readonly ISceneItemService _sceneItemService;
    private readonly IHistoryCoordinator _historyCoordinator;

    public EditorGizmoHost(
        EditorSelectionService editorSelection,
        ISceneItemService sceneItemService,
        IHistoryCoordinator historyCoordinator)
    {
        _editorSelection = editorSelection;
        _sceneItemService = sceneItemService;
        _historyCoordinator = historyCoordinator;
    }

    int IGizmoHost.GetSelectionRevision()
        => _editorSelection.Revision;

    long IGizmoHost.GetSceneRevision()
    {
        SceneRevisions revisions = _sceneItemService.GetRevisions();
        return unchecked(revisions.Active + revisions.Bounds);
    }

    void IGizmoHost.PrepareHistoryMutation()
        => _historyCoordinator.CommitPendingInspectorEdits();

    bool IGizmoHost.TryCommitHistoryEdit(
        SceneEditSession edit,
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> afterSnapshots)
    {
        Guid[] selectionIds = [.. _editorSelection.SelectedItemIds];
        return _historyCoordinator.TryCommitSceneEdit(edit, kind, title, beforeSnapshots, afterSnapshots, selectionIds, selectionIds);
    }

    bool IGizmoHost.TryDuplicateSelectedItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
        => _historyCoordinator.TryDuplicateSceneItems(selectedSnapshots);

    bool IGizmoHost.TryRemoveSelectedItems(IReadOnlyList<SceneItemSnapshot> selectedSnapshots)
        => _historyCoordinator.TryRemoveSceneItems(selectedSnapshots);

    bool IGizmoHost.TryMoveItemToPlayerWithHistory(Guid itemId)
        => _historyCoordinator.TryMoveItemToPlayer(itemId);

    bool IGizmoHost.TryCenterGroupPivot(SceneGroupSnapshot group)
        => _historyCoordinator.TryCenterGroupPivot(group);

    bool IGizmoHost.TryApplySelectedSnapshotUpdateWithHistory(
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        Func<SceneItemSnapshot, SceneItemSnapshot> updateFactory)
        => _historyCoordinator.TryApplySceneItemUpdate(kind, title, selectedSnapshots, updateFactory);
}
