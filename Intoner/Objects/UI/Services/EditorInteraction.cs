using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Scene;
using Intoner.Services.Input;

namespace Intoner.Objects.UI.Services;

internal sealed class EditorInteraction : IDisposable
{
    private readonly IHistoryCoordinator _history;
    private readonly Gizmo _gizmo;
    private readonly IShortcutService _shortcuts;
    private readonly IObjectWorkspaceHistory _workspaceHistory;
    private readonly ISceneLocationService _locations;

    public EditorSelectionService Selection { get; }
    public EditorDialog Dialog { get; } = new();

    public event Action? DialogOpening;

    public EditorInteraction(
        EditorSelectionService selection,
        IHistoryCoordinator history,
        Gizmo gizmo,
        IShortcutService shortcuts,
        IObjectWorkspaceHistory workspaceHistory,
        ISceneLocationService locations)
    {
        Selection = selection;
        _history = history;
        _gizmo = gizmo;
        _shortcuts = shortcuts;
        _workspaceHistory = workspaceHistory;
        _locations = locations;
        _workspaceHistory.MutationStarting += CommitPendingHistory;
        _locations.TransitionStarted += CancelLocationInteractions;
        _history.ConnectSelectionHandlers(CaptureSelection, ApplySelection);
    }

    public void Dispose()
    {
        _workspaceHistory.MutationStarting -= CommitPendingHistory;
        _locations.TransitionStarted -= CancelLocationInteractions;
        _history.DisconnectSelectionHandlers();
    }

    private void CancelLocationInteractions()
    {
        _history.CommitPendingInspectorEdits();
        _gizmo.CancelPreviewInteractions();
    }

    public Guid[] CaptureSelection()
        => [.. Selection.SelectedItemIds];

    public void ApplySelection(IReadOnlyList<Guid>? itemIds)
    {
        if (itemIds is null)
        {
            return;
        }

        SelectionChanged(itemIds.Count == 0
            ? Selection.TryClear()
            : Selection.TryReplaceSelection(itemIds));
    }

    public void SelectionChanged(bool changed)
    {
        if (!changed)
        {
            return;
        }

        _history.CommitPendingInspectorEdits();
        _gizmo.ExitGroupPivotEditing();
    }

    public void CommitPendingHistory()
    {
        _history.CommitPendingInspectorEdits();
        _gizmo.CancelInteractions();
    }

    public bool TryUndoHistory()
        => ReplayHistory(_history.TryUndo);

    public bool TryRedoHistory()
        => ReplayHistory(_history.TryRedo);

    public bool TryJumpToHistoryState(int stateIndex, IReadOnlyList<SceneHistoryEntry> entries)
        => ReplayHistory(() => _history.TryJumpToState(stateIndex, entries));

    private bool ReplayHistory(Func<bool> replay)
    {
        CommitPendingHistory();
        return replay();
    }

    public void OpenDialog(EditorDialog.Request request)
    {
        DialogOpening?.Invoke();
        _gizmo.CancelInteractions();
        _shortcuts.DeactivateAll();
        Dialog.Open(request);
    }
}
