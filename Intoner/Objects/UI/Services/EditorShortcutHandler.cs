using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Services;
using Intoner.Scene;
using Intoner.Services.Input;
using System.Runtime.InteropServices;
using ObjectPasteDestination = Intoner.Objects.Api.ObjectPasteDestination;

namespace Intoner.Objects.UI;

internal sealed class EditorShortcutHandler
{
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ShortcutContext(IReadOnlyList<SceneItemSnapshot> Items, bool HasCopyableSelection);

    private sealed record Command(
        ShortcutDefinition Shortcut,
        Func<ShortcutContext, bool> CanExecute,
        Action<ShortcutContext> Execute);

    private readonly IShortcutService _shortcuts;
    private readonly EditorSceneState _sceneState;
    private readonly Command[]        _commands;

    public EditorShortcutHandler(
        ISceneHistoryManager sceneHistoryManager,
        IObjectClipboardService objectClipboard,
        IShortcutService shortcuts,
        EditorSceneState sceneState,
        SceneEditorCommands sceneCommands,
        HistoryWorkspace history,
        Gizmo gizmo)
    {
        _shortcuts  = shortcuts;
        _sceneState = sceneState;
        _commands   =
        [
            new(EditorShortcuts.Undo, _ => sceneHistoryManager.UndoActionKind is not null, _ => history.TryUndoHistory()),
            new(EditorShortcuts.Redo, _ => sceneHistoryManager.RedoActionKind is not null, _ => history.TryRedoHistory()),
            new(EditorShortcuts.Copy, static context => context.HasCopyableSelection, context => objectClipboard.CopyObjects(context.Items)),
            new(EditorShortcuts.Cut, static context => context.HasCopyableSelection, context => sceneCommands.CutObjectsToClipboard(context.Items)),
            new(EditorShortcuts.Paste, static _ => true, _ => sceneCommands.PasteObjectsFromClipboard(ObjectPasteDestination.KeepOrganization)),
            new(EditorShortcuts.Group,
                static context => context.HasCopyableSelection && SceneEditorCommands.CanGroupItems(context.Items),
                context => sceneCommands.TryGroupItems(context.Items)),
            new(EditorShortcuts.Ungroup,
                static context => context.Items.Any(static item => item is SceneGroupSnapshot),
                context => sceneCommands.TryUngroupItems(context.Items)),
            new(EditorShortcuts.ToggleTransformSpace, _ => gizmo.CanToggleTransformSpace, _ => gizmo.ToggleTransformSpace()),
        ];
    }

    internal void HandleEditorShortcuts(bool commandsEnabled = true, bool sceneToolsEnabled = true)
    {
        bool heldControlsEnabled = sceneToolsEnabled && !ImGui.GetIO().WantTextInput;
        _shortcuts.SetActive(EditorShortcuts.InvertScaleAnchor, heldControlsEnabled);
        _shortcuts.SetActive(EditorShortcuts.InvertUniformScaling, heldControlsEnabled);

        if (!commandsEnabled || ImGui.IsAnyItemActive())
        {
            foreach (Command command in _commands)
            {
                _shortcuts.SetActive(command.Shortcut, false);
            }

            return;
        }

        bool hasCopyableSelection = _sceneState.HasSelectedClipboardObjects();
        IReadOnlyList<SceneItemSnapshot> items = _sceneState.ResolveSelectedCurrentItems();
        ShortcutContext context = new(items, hasCopyableSelection && items.Count > 0);
        foreach (Command command in _commands)
        {
            _shortcuts.SetActive(command.Shortcut, command.CanExecute(context));
        }

        foreach (Command command in _commands)
        {
            if (!_shortcuts.IsPressed(command.Shortcut))
            {
                continue;
            }

            _shortcuts.ClearPending();
            command.Execute(context);
            return;
        }
    }
}
