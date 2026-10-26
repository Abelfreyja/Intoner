using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private const string ScaleOptionsPopupId = "##objectScaleOptions";
    private const string ScaleAnchorLabel = "Anchored Scaling";
    private const string UniformScalingDescription = "Scale all axes proportionally. Group and multiple object selections always scale uniformly.";
    private const string AnchoredScalingDescription = "Resize from one side while keeping the other side in place.";
    private const string AnchorEnabledHint = "to scale from the center";
    private const string AnchorDisabledHint = "to anchor scaling";
    private bool _scaleUniformRequired;
    private string _scaleUnavailableReason = string.Empty;

    private string UniformEnabledHint
        => _scaleUniformRequired ? "to scale one axis on single objects" : "to scale one axis";

    private string UniformDisabledHint
        => _scaleUniformRequired ? "to scale all axes on single objects" : "to scale all axes";

    private void DrawScaleOptionsPopup()
    {
        bool open = ImGui.IsItemClicked(ImGuiMouseButton.Right);

        if (!open && !ImGui.IsPopupOpen(ScaleOptionsPopupId))
        {
            return;
        }

        Vector4 accent = EditorColors.TransformModeAccent(GizmoTransformMode.Scale);
        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem(
            ScaleOptionsPopupId, open, 280f * ImGuiHelpers.GlobalScale, tooltipAccent: accent);

        if (!popup)
        {
            return;
        }

        IntonerTooltipContent.Header(FontAwesomeIcon.CompressArrowsAlt, "Scale", accentOverride: accent);

        bool uniformScaling = _gizmo.Settings.UniformScaling;

        if (EditorContextMenu.DrawToggleWithHint("uniformScaling", FontAwesomeIcon.Link,
                EditorShortcuts.InvertUniformScaling.Name, ref uniformScaling, accent,
                _shortcuts.GetGesture(EditorShortcuts.InvertUniformScaling), UniformEnabledHint, UniformDisabledHint,
                enabled: _gizmo.CanToggleUniformScaling, tooltip: UniformScalingDescription))
        {
            _gizmo.ToggleUniformScaling();
        }

        EditorContextMenu.DrawSeparator();
        bool anchoredScaling = _gizmo.Settings.AnchoredScaling;

        if (EditorContextMenu.DrawToggleWithHint("anchoredScaling", FontAwesomeIcon.Anchor, ScaleAnchorLabel,
                ref anchoredScaling, accent, _shortcuts.GetGesture(EditorShortcuts.InvertScaleAnchor),
                AnchorEnabledHint, AnchorDisabledHint,
                tooltip: AnchoredScalingDescription))
        {
            _gizmo.Settings.AnchoredScaling = anchoredScaling;
        }
    }

    private void DrawScaleToolbarTooltip()
    {
        string description = _scaleUnavailableReason;

        if (description.Length == 0 && _gizmo.Scaling.AnchorUnavailable)
        {
            description = "The last scale edit was not applied because physical bounds were unavailable.";
        }

        List<IntonerTooltipKeyHint> keyHints = new(3) { GizmoModeKeyHints[0] };

        if (IntonerTooltipKeyHint.HoldOverride(_shortcuts.GetGesture(EditorShortcuts.InvertScaleAnchor),
                _gizmo.Settings.AnchoredScaling, AnchorEnabledHint, AnchorDisabledHint) is { } anchorHint)
        {
            keyHints.Add(anchorHint);
        }

        if (IntonerTooltipKeyHint.HoldOverride(_shortcuts.GetGesture(EditorShortcuts.InvertUniformScaling),
                _gizmo.Settings.UniformScaling, UniformEnabledHint, UniformDisabledHint) is { } uniformHint)
        {
            keyHints.Add(uniformHint);
        }

        EditorToolbarTooltip.Draw(FontAwesomeIcon.CompressArrowsAlt, "Scale",
            EditorColors.TransformModeAccent(GizmoTransformMode.Scale),
            [new("Left click", "Toggle scale gizmo"), new("Right click", "Open options")],
            [new(FontAwesomeIcon.Link, EditorShortcuts.InvertUniformScaling.Name, _gizmo.Settings.UniformScaling),
                new(FontAwesomeIcon.Anchor, ScaleAnchorLabel, _gizmo.Settings.AnchoredScaling)],
            keyHints, description);
    }
}
