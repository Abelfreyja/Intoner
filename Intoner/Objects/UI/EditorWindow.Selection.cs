using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.Models;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const int VirtualKeyLeftButton = 0x01;

    private readonly record struct ObjectSelectionClick(Vector2 ViewportPos, Vector2 ViewportSize, Vector2 MousePos, bool ToggleSelection);

    private void HandleObjectSelectionInput(IReadOnlyList<ObjectSnapshot> activeSelectedObjects, IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        if (!BoundsInteractionSettings.SelectionEnabled
            || !TryGetObjectSelectionClick(out var click))
        {
            return;
        }

        if (IsObjectSelectionBlocked(activeSelectedObjects, boundsSnapshots, click.MousePos))
        {
            return;
        }

        TryApplySceneSelection(click);
    }

    private bool TryGetObjectSelectionClick(out ObjectSelectionClick click)
    {
        click = default;

        var viewport = ImGui.GetMainViewport();
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;

        var isLeftMouseDown = (UiSharedService.GetKeyState(VirtualKeyLeftButton) & 0x8000) != 0;
        var isLeftMouseClicked = isLeftMouseDown && !_objectSelectionLeftMouseWasDown;
        _objectSelectionLeftMouseWasDown = isLeftMouseDown;
        if (!isLeftMouseClicked)
        {
            return false;
        }

        if (mousePos.X < viewport.Pos.X
            || mousePos.X > viewport.Pos.X + viewport.Size.X
            || mousePos.Y < viewport.Pos.Y
            || mousePos.Y > viewport.Pos.Y + viewport.Size.Y)
        {
            return false;
        }

        click = new ObjectSelectionClick(viewport.Pos, viewport.Size, mousePos, io.KeyCtrl);
        return true;
    }

    private bool IsObjectSelectionBlocked(
        IReadOnlyList<ObjectSnapshot> activeSelectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => IsObjectSelectionBlockedByUi()
            || IsObjectSelectionBlockedByGizmo(activeSelectedObjects, boundsSnapshots, mousePos);

    private static bool IsObjectSelectionBlockedByUi()
    {
        var io = ImGui.GetIO();
        return ImGui.IsAnyItemActive() || io.WantCaptureMouse;
    }

    private bool IsObjectSelectionBlockedByGizmo(
        IReadOnlyList<ObjectSnapshot> activeSelectedObjects,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector2 mousePos)
        => _gizmo.IsSelectionBlocked(activeSelectedObjects, boundsSnapshots, mousePos);

    private void TryApplySceneSelection(ObjectSelectionClick click)
    {
        if (!_objectSelectionService.TrySelectActiveObject(click.ViewportPos, click.ViewportSize, click.MousePos, out var selectedSnapshot)
            || selectedSnapshot.Locked)
        {
            return;
        }

        ApplyObjectSelection(selectedSnapshot.Id, click.ToggleSelection);
    }

    private void ApplyObjectSelection(Guid objectId, bool toggleSelection)
        => HandleSelectionChanged(_editorSelection.TrySelect(objectId, toggleSelection));

    private void HandleSelectionChanged(bool changed)
    {
        if (!changed)
        {
            return;
        }

        _historyCoordinator.CommitPendingInspectorEdits();
        _gizmo.CancelInteractions();
    }
}

