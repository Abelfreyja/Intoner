using Intoner.Objects.Models;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    int IGizmoHost.GetSelectionRevision()
        => _editorSelection.Revision;

    long IGizmoHost.GetSceneRevision()
        => _sceneView.GetSceneRevision();

    Guid[] IGizmoHost.CaptureCurrentSelectionIds()
        => CaptureCurrentSelectionIds();

    bool IGizmoHost.TryRecordCompletedHistoryAction(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
        => TryRecordCompletedHistoryAction(
            kind,
            title,
            beforeSnapshots,
            afterSnapshots,
            selectionAfterApply,
            selectionAfterRevert);

    bool IGizmoHost.TryDuplicateSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
        => TryDuplicateSelectedObjects(selectedSnapshots);

    bool IGizmoHost.TryRemoveSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
        => TryRemoveSelectedObjects(selectedSnapshots);

    bool IGizmoHost.TryMoveObjectToPlayerWithHistory(Guid objectId)
        => TryMoveObjectToPlayerWithHistory(objectId);

    bool IGizmoHost.TryApplySelectedSnapshotUpdateWithHistory(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        Func<ObjectSnapshot, ObjectSnapshot> updateFactory)
        => TryApplySelectedSnapshotUpdateWithHistory(kind, title, selectedSnapshots, updateFactory);
}

