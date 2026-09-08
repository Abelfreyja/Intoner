using Intoner.Objects.UI.Components;
using Intoner.Services.Input;

namespace Intoner.Objects.UI.Services;

internal sealed class EditorInteraction : IDisposable
{
    private readonly IHistoryCoordinator _history;
    private readonly Gizmo _gizmo;
    private readonly IShortcutService _shortcuts;

    public EditorSelectionService Selection { get; }
    public EditorDialog Dialog { get; } = new();

    public event Action? DialogOpening;

    public EditorInteraction(
        EditorSelectionService selection,
        IHistoryCoordinator history,
        Gizmo gizmo,
        IShortcutService shortcuts)
    {
        Selection = selection;
        _history = history;
        _gizmo = gizmo;
        _shortcuts = shortcuts;
        _history.ConnectSelectionHandlers(CaptureSelection, ApplySelection);
    }

    public void Dispose()
        => _history.DisconnectSelectionHandlers();

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
        _gizmo.CancelInteractions();
    }

    public void CommitPendingHistory()
    {
        _history.CommitPendingInspectorEdits();
        _gizmo.CancelInteractions();
        _history.PrepareForMutation();
    }

    public void OpenDialog(EditorDialog.Request request)
    {
        DialogOpening?.Invoke();
        _gizmo.CancelInteractions();
        _shortcuts.DeactivateAll();
        Dialog.Open(request);
    }
}
