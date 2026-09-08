using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Services;
using Intoner.Scene;
using Intoner.Services.Input;
using ObjectPasteDestination = Intoner.Objects.Api.ObjectPasteDestination;

namespace Intoner.Objects.UI;

internal sealed class EditorShortcutHandler
{
    private readonly ISceneHistoryManager    _sceneHistoryManager;
    private readonly IObjectClipboardService _objectClipboard;
    private readonly IShortcutService        _shortcuts;
    private readonly EditorSceneState        _sceneState;
    private readonly SceneEditorCommands     _sceneCommands;
    private readonly HistoryWorkspace        _history;

    public EditorShortcutHandler(
        ISceneHistoryManager sceneHistoryManager,
        IObjectClipboardService objectClipboard,
        IShortcutService shortcuts,
        EditorSceneState sceneState,
        SceneEditorCommands sceneCommands,
        HistoryWorkspace history)
    {
        _sceneHistoryManager = sceneHistoryManager;
        _objectClipboard     = objectClipboard;
        _shortcuts           = shortcuts;
        _sceneState          = sceneState;
        _sceneCommands       = sceneCommands;
        _history             = history;
    }

    internal void HandleEditorShortcuts()
    {
        if (ImGui.IsAnyItemActive())
        {
            _shortcuts.DeactivateAll();
            return;
        }

        bool hasCopyableSelection = _sceneState.HasSelectedClipboardObjects();
        _shortcuts.SetActive(EditorShortcuts.Copy, hasCopyableSelection);
        _shortcuts.SetActive(EditorShortcuts.Cut, hasCopyableSelection);
        _shortcuts.SetActive(EditorShortcuts.Paste, true);
        _shortcuts.SetActive(EditorShortcuts.Undo, _sceneHistoryManager.UndoActionKind is not null);
        _shortcuts.SetActive(EditorShortcuts.Redo, _sceneHistoryManager.RedoActionKind is not null);

        if (TryConsumeEditorShortcut(EditorShortcuts.Undo))
        {
            _ = _history.TryUndoHistory();
            return;
        }

        if (TryConsumeEditorShortcut(EditorShortcuts.Redo))
        {
            _ = _history.TryRedoHistory();
            return;
        }

        if (TryConsumeEditorShortcut(EditorShortcuts.Copy))
        {
            IReadOnlyList<ObjectSnapshot> snapshots = _sceneState.ResolveSelectedClipboardObjects();
            if (snapshots.Count > 0)
            {
                _ = _objectClipboard.CopyObjects(snapshots);
            }

            return;
        }

        if (TryConsumeEditorShortcut(EditorShortcuts.Cut))
        {
            IReadOnlyList<ObjectSnapshot> snapshots = _sceneState.ResolveSelectedClipboardObjects();
            if (snapshots.Count > 0)
            {
                _ = _sceneCommands.CutObjectsToClipboard(snapshots);
            }

            return;
        }

        if (TryConsumeEditorShortcut(EditorShortcuts.Paste))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(ObjectPasteDestination.KeepOrganization);
        }
    }

    private bool TryConsumeEditorShortcut(ShortcutDefinition shortcut)
    {
        if (!_shortcuts.IsPressed(shortcut))
        {
            return false;
        }

        _shortcuts.ClearPending();
        return true;
    }
}
