using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private IReadOnlyList<SceneItemSnapshot> _pivotSelection = [];
    private const string PivotMenuId = "groupPivotMenu";
    private Guid? _pivotMenuGroupId;

    internal bool IsEditingGroupPivot(Guid groupId)
        => State.PivotEditGroupId == groupId;

    internal void ExitGroupPivotEditing()
    {
        CancelInteractions();
        State.SetPivotEditGroup(null);
        _pivotSelection = [];
    }

    private void ToggleGroupPivotEditing(SceneGroupSnapshot group)
    {
        if (group.Locked)
            return;
        CancelInteractions();
        _host.PrepareHistoryMutation();
        State.SetPivotEditGroup(IsEditingGroupPivot(group.Id) ? null : group.Id);
        if (State.PivotEditGroupId.HasValue && Mode == GizmoTransformMode.Scale)
            Mode = GizmoTransformMode.Translation;
    }

    private void NormalizePivotSelection(IReadOnlyList<SceneItemSnapshot> selectedItems)
    {
        if (State.PivotEditGroupId.HasValue
            && (SceneGroupUtility.ResolveSelectedGroup(selectedItems) is not { Locked: false } group || group.Id != State.PivotEditGroupId))
            ExitGroupPivotEditing();
    }

    private static float ResolvePivotRingRadius(float scale)
        => (GizmoConstants.CenterInteractionRadius + GizmoConstants.PivotRingGap) * scale;

    private static bool IsPivotRingHovered(in GizmoContext context, Vector2 mousePos, float scale)
        => SceneGroupUtility.ResolveSelectedGroup(context.SelectedSnapshots) is { Locked: false }
            && MathF.Abs(Vector2.Distance(mousePos, context.ScreenPos) - ResolvePivotRingRadius(scale))
                <= (GizmoConstants.PivotRingThickness * 0.5f + GizmoConstants.PivotRingHitPadding) * scale;

    private bool DrawGroupPivotHandle(in GizmoFrame frame, DrawBatch batch, float scale)
    {
        if (SceneGroupUtility.ResolveSelectedGroup(frame.Context.SelectedSnapshots) is not { } group)
            return false;

        bool hovered = frame.Interaction.Phase == GizmoInteractionPhase.HoverPivot;
        bool editing = IsEditingGroupPivot(group.Id);
        Vector4 color = editing || hovered ? GizmoHighlightColor : GizmoCenterColor;
        color.W *= ResolveGizmoHoverOpacity(frame.Interaction, hovered);
        if (group.Locked)
            color.W *= 0.4f;
        batch.AddScreenCircle(frame.Context.ScreenPos, ResolvePivotRingRadius(scale), color, GizmoConstants.PivotRingThickness * scale, 64);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            IntonerTooltip.DrawText(editing ? "Edit Group Transform" : "Edit Group Pivot");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                ToggleGroupPivotEditing(group);
        }

        bool open = hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        if (open)
            _pivotMenuGroupId = group.Id;
        using EditorContextMenu.PopupScope menu = EditorContextMenu.BeginDropdown(PivotMenuId, open, ImGui.GetIO().MousePos);
        if (menu)
        {
            if (_pivotMenuGroupId != group.Id)
                ImGui.CloseCurrentPopup();
            else if (EditorContextMenu.DrawItem(FontAwesomeIcon.Crosshairs, "Center Group Pivot", enabled: !group.Locked))
            {
                CancelInteractions();
                _ = _host.TryCenterGroupPivot(group);
            }
        }
        return hovered || menu;
    }
}
