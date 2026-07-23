using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private sealed class FolderStateHistoryAction : ObjectHistoryActionBase
    {
        private readonly IObjectFolderService _objectFolderService;
        private readonly ObjectFolderSceneState _beforeState;
        private readonly ObjectFolderSceneState _afterState;

        public FolderStateHistoryAction(
            IObjectFolderService objectFolderService,
            ObjectHistoryKind kind,
            string title,
            ObjectFolderSceneState beforeState,
            ObjectFolderSceneState afterState)
            : base(title, kind)
        {
            _objectFolderService = objectFolderService;
            _beforeState = beforeState;
            _afterState = afterState;
        }

        protected override void ApplyCore()
        {
            if (!_objectFolderService.TryApplySceneState(_afterState))
            {
                throw new InvalidOperationException($"could not apply '{Title}' folder state");
            }
        }

        protected override void RevertCore()
        {
            if (!_objectFolderService.TryApplySceneState(_beforeState))
            {
                throw new InvalidOperationException($"could not revert '{Title}' folder state");
            }
        }
    }

    private sealed class CompositeHistoryAction : ObjectHistoryActionBase
    {
        private readonly IObjectHistoryAction[] _actions;

        public CompositeHistoryAction(
            ObjectHistoryKind kind,
            string title,
            IReadOnlyList<IObjectHistoryAction> actions)
            : base(title, kind)
        {
            _actions = [.. actions];
        }

        protected override void ApplyCore()
        {
            foreach (var action in _actions)
            {
                action.Apply();
            }
        }

        protected override void RevertCore()
        {
            for (var i = _actions.Length - 1; i >= 0; --i)
            {
                _actions[i].Revert();
            }
        }

        public override void Dispose()
        {
            foreach (var action in _actions)
            {
                action.Dispose();
            }
        }

        internal override void MarkRecordedApplied()
        {
            base.MarkRecordedApplied();

            foreach (var action in _actions)
            {
                if (action is ObjectHistoryActionBase historyAction)
                {
                    historyAction.MarkRecordedApplied();
                }
            }
        }
    }

    private Guid[] CaptureCurrentSelectionIds()
        => [.. _editorSelection.SelectedObjectIds];

    private void ApplyHistorySelection(IReadOnlyList<Guid>? selectionIds)
    {
        if (selectionIds is null)
        {
            return;
        }

        if (selectionIds.Count == 0)
        {
            HandleSelectionChanged(_editorSelection.TryClear());
            return;
        }

        HandleSelectionChanged(_editorSelection.TryReplaceSelection(selectionIds));
    }

    private void RefreshHistoryContext()
    {
        if (_historyCoordinator.RefreshContext(_sceneView.GetCurrentLocationContext(), CommitPendingHistory))
        {
            _placementValidationService.ClearCache();
            ResetHistoryWorkspaceState();
        }
    }

    private void CommitPendingHistory()
    {
        _historyCoordinator.CommitPendingInspectorEdits();
        _gizmo.CancelInteractions();
        _historyCoordinator.PrepareForMutation();
    }

    private void PrepareHistoryMutation()
        => _historyCoordinator.PrepareForMutation();

    private void ApplyInspectorSnapshotEdit(
        string editId,
        ObjectHistoryKind kind,
        string title,
        ObjectSnapshot startSnapshot,
        ObjectSnapshot? nextSnapshot,
        bool recordImmediately = false)
        => _historyCoordinator.ApplyInspectorSnapshotEdit(editId, kind, title, startSnapshot, nextSnapshot, recordImmediately);

    private bool TryUndoHistory()
    {
        CommitPendingHistory();
        if (!_historyCoordinator.TryUndo())
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    private bool TryRedoHistory()
    {
        CommitPendingHistory();
        if (!_historyCoordinator.TryRedo())
        {
            return false;
        }

        _focusCurrentHistoryEntry = true;
        return true;
    }

    private bool TryCreateObjectWithHistory(string title, ObjectKind kind, ObjectPlacementOverrides? overrides)
        => _historyCoordinator.TryCreateObject(title, kind, overrides);

    private bool TryImportObjectSnapshotWithHistory(ObjectSnapshot snapshot)
        => _historyCoordinator.TryImportObjectSnapshot(snapshot);

    private bool TryMoveObjectToPlayerWithHistory(Guid objectId)
        => _historyCoordinator.TryMoveObjectToPlayer(objectId);

    private bool TryApplySelectedSnapshotUpdateWithHistory(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        Func<ObjectSnapshot, ObjectSnapshot> updateFactory)
        => _historyCoordinator.TryApplySelectedSnapshotUpdate(kind, title, selectedSnapshots, updateFactory);

    private bool TryDuplicateSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
        => _historyCoordinator.TryDuplicateObjects(selectedSnapshots);

    private bool TryRemoveSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedSnapshots)
        => _historyCoordinator.TryRemoveObjects(selectedSnapshots);

    private bool TryClearPlacedObjectsWithHistory()
        => _historyCoordinator.TryClearPlacedObjects();

    private bool TryRecordCompletedHistoryAction(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
        => _historyCoordinator.TryRecordCompletedAction(
            kind,
            title,
            beforeSnapshots,
            afterSnapshots,
            selectionAfterApply,
            selectionAfterRevert);

    private bool TryCreateFolderWithHistory(string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.AddFolder(beforeState, sanitizedFolderPath);
        return TryApplyFolderStateWithHistory("Create Folder", beforeState, afterState);
    }

    private bool TryRenameFolderWithHistory(string sourceFolderPath, string nextFolderPath)
    {
        var sanitizedSourceFolderPath = ObjectFolderUtility.SanitizeFolderPath(sourceFolderPath);
        var sanitizedNextFolderPath = ObjectFolderUtility.SanitizeFolderPath(nextFolderPath);
        if (string.IsNullOrEmpty(sanitizedSourceFolderPath)
            || string.IsNullOrEmpty(sanitizedNextFolderPath)
            || string.Equals(sanitizedSourceFolderPath, sanitizedNextFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PrepareHistoryMutation();
        var beforeFolderState = _objectFolderService.CaptureSceneState();
        var afterFolderState = ObjectFolderSceneStateUtility.RenameFolder(beforeFolderState, sanitizedSourceFolderPath, sanitizedNextFolderPath);
        var beforeSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => string.Equals(snapshot.FolderPath, sanitizedSourceFolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        IReadOnlyList<ObjectSnapshot> afterSnapshots = [];
        if (beforeSnapshots.Count > 0)
        {
            var requestedSnapshots = beforeSnapshots
                .Select(snapshot => snapshot with { FolderPath = sanitizedNextFolderPath })
                .ToList();
            if (!_mutationService.TryUpdateMany(requestedSnapshots, out afterSnapshots))
            {
                return false;
            }
        }

        if (!TryApplyFolderState(beforeFolderState, afterFolderState, beforeSnapshots))
        {
            return false;
        }

        return TryRecordOrganizationHistoryAction("Rename Folder", beforeSnapshots, afterSnapshots, beforeFolderState, afterFolderState);
    }

    private bool TryDissolveFolderWithHistory(string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        PrepareHistoryMutation();
        var beforeFolderState = _objectFolderService.CaptureSceneState();
        var afterFolderState = ObjectFolderSceneStateUtility.RemoveFolder(beforeFolderState, sanitizedFolderPath);
        var beforeSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => string.Equals(snapshot.FolderPath, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        IReadOnlyList<ObjectSnapshot> afterSnapshots = [];
        if (beforeSnapshots.Count > 0)
        {
            var requestedSnapshots = beforeSnapshots
                .Select(snapshot => snapshot with { FolderPath = string.Empty })
                .ToList();
            if (!_mutationService.TryUpdateMany(requestedSnapshots, out afterSnapshots))
            {
                return false;
            }
        }

        if (!TryApplyFolderState(beforeFolderState, afterFolderState, beforeSnapshots))
        {
            return false;
        }

        return TryRecordOrganizationHistoryAction("Dissolve Folder", beforeSnapshots, afterSnapshots, beforeFolderState, afterFolderState);
    }

    private bool TrySetFolderColorWithHistory(string folderPath, string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.SetFolderColor(beforeState, sanitizedFolderPath, sanitizedColorValue);
        var title = string.IsNullOrEmpty(sanitizedColorValue)
            ? "Clear Folder Color"
            : "Set Folder Color";
        return TryApplyFolderStateWithHistory(title, beforeState, afterState);
    }

    private bool TryApplyFolderStateWithHistory(string title, ObjectFolderSceneState beforeState, ObjectFolderSceneState afterState)
    {
        if (ObjectFolderSceneStateUtility.StatesMatch(beforeState, afterState))
        {
            return false;
        }

        PrepareHistoryMutation();
        if (!_objectFolderService.TryApplySceneState(afterState))
        {
            return false;
        }

        return TryRecordOrganizationHistoryAction(title, [], [], beforeState, afterState);
    }

    private bool TryApplyFolderState(
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        IReadOnlyList<ObjectSnapshot> rollbackSnapshots)
    {
        if (ObjectFolderSceneStateUtility.StatesMatch(beforeFolderState, afterFolderState))
        {
            return true;
        }

        if (_objectFolderService.TryApplySceneState(afterFolderState))
        {
            return true;
        }

        if (rollbackSnapshots.Count > 0)
        {
            _ = _mutationService.TryUpdateMany(rollbackSnapshots, out _);
        }

        return false;
    }

    private bool TryRecordOrganizationHistoryAction(
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState)
    {
        List<IObjectHistoryAction> actions = [];
        var snapshotChanges = ObjectSnapshotHistoryChanges.Build(beforeSnapshots, afterSnapshots);
        if (snapshotChanges.Count > 0)
        {
            actions.Add(new ObjectSnapshotHistoryAction(_mutationService, ObjectHistoryKind.Organization, title, snapshotChanges));
        }

        if (!ObjectFolderSceneStateUtility.StatesMatch(beforeFolderState, afterFolderState))
        {
            actions.Add(new FolderStateHistoryAction(_objectFolderService, ObjectHistoryKind.Organization, title, beforeFolderState, afterFolderState));
        }

        if (actions.Count == 0)
        {
            return false;
        }

        _historyCoordinator.RecordCompletedAction(
            actions.Count == 1
                ? actions[0]
                : new CompositeHistoryAction(ObjectHistoryKind.Organization, title, actions));
        return true;
    }

    private void DrawToolbarHistoryTooltip(bool undo, Vector4 headingAccent)
    {
        var scale = ImGuiHelpers.GlobalScale;

        UiSharedService.DrawAccentTooltip(
            () =>
            {
                var heading = undo ? "Undo" : "Redo";
                using (ImRaii.PushColor(ImGuiCol.Text, headingAccent))
                {
                    ImGui.TextUnformatted(heading);
                }

                if (!TryGetToolbarHistoryEntry(undo, out var entry))
                {
                    ImGui.TextDisabled(undo
                        ? "No change is available to undo."
                        : "No change is available to redo.");
                    return;
                }

                var actionAccent = EditorColors.HistoryEntryAccent(entry.Kind!.Value);
                var headingDescription = undo ? "Current action" : "Next action";

                ImGui.TextDisabled(headingDescription);
                ImGui.Dummy(new Vector2(0f, 3f * scale));
                DrawToolbarHistoryKindBadge(entry.Kind!.Value);
                ImGui.SameLine(0f, 6f * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1f * scale));
                using (ImRaii.PushColor(ImGuiCol.Text, actionAccent))
                {
                    ImGui.TextUnformatted(ObjectHistoryDescription.GetActionTitle(entry.Kind.Value, entry.Title));
                }
            },
            headingAccent,
            new Vector2(15f, 12f),
            new Vector2(8f, 7f));
    }

    private bool TryGetToolbarHistoryEntry(bool undo, out ObjectHistoryEntry entry)
    {
        var entries = _objectHistoryManager.Entries;
        if (entries.Count == 0)
        {
            entry = default;
            return false;
        }

        var currentStateIndex = Math.Clamp(_objectHistoryManager.CurrentStateIndex, 0, entries.Count - 1);
        var targetStateIndex = undo ? currentStateIndex : currentStateIndex + 1;
        if (targetStateIndex <= 0 || targetStateIndex >= entries.Count)
        {
            entry = default;
            return false;
        }

        entry = entries[targetStateIndex];
        return entry.Kind.HasValue;
    }

    private void DrawToolbarHistoryKindBadge(ObjectHistoryKind kind)
    {
        var label = ObjectHistoryDescription.GetKindLabel(kind);
        var accent = EditorColors.HistoryEntryAccent(kind);
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(5f * scale, 1.5f * scale);
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(textSize.X + (padding.X * 2f), textSize.Y + (padding.Y * 2f));
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var textPos = new Vector2(
            min.X + padding.X,
            min.Y + ((size.Y - textSize.Y) * 0.5f));

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.18f)), 999f);
        drawList.AddRect(min, max, ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.68f)), 999f);
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), label);
        ImGui.Dummy(size);
    }
}
