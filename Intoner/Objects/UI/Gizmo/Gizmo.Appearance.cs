using Intoner.Objects.Models;
using Intoner.Services.Configuration;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private float GizmoOpacity => _appearance.Opacity / 100f;

    private Vector4 GizmoHighlightColor => _appearance.Preset switch
    {
        GizmoColorPreset.ImGuizmo => new Vector4(1f, 128f / 255f, 16f / 255f, 138f / 255f),
        GizmoColorPreset.Custom => new Vector4(_appearance.Highlight.ToNormalizedVector3(), 1f),
        _ => EditorColors.GizmoDragActive,
    };

    private Vector4 GizmoInactiveColor => _appearance.Preset switch
    {
        GizmoColorPreset.ImGuizmo => new Vector4(0.6f),
        GizmoColorPreset.Custom => new Vector4(_appearance.Inactive.ToNormalizedVector3(), 0.25f),
        _ => EditorColors.GizmoDragSuppressed,
    };

    private Vector4 GizmoCenterColor => _appearance.Preset == GizmoColorPreset.Custom
        ? new Vector4(_appearance.Center.ToNormalizedVector3(), 1f)
        : Vector4.One;

    private Vector4 GetGizmoAxisBase(GizmoAxis axis)
        => (_appearance.Preset, axis) switch
        {
            (GizmoColorPreset.ImGuizmo, GizmoAxis.X) => new Vector4(170f / 255f, 0f, 0f, 1f),
            (GizmoColorPreset.ImGuizmo, GizmoAxis.Y) => new Vector4(0f, 170f / 255f, 0f, 1f),
            (GizmoColorPreset.ImGuizmo, GizmoAxis.Z) => new Vector4(0f, 0f, 170f / 255f, 1f),
            (GizmoColorPreset.Custom, GizmoAxis.X) => new Vector4(_appearance.XAxis.ToNormalizedVector3(), 1f),
            (GizmoColorPreset.Custom, GizmoAxis.Y) => new Vector4(_appearance.YAxis.ToNormalizedVector3(), 1f),
            (GizmoColorPreset.Custom, GizmoAxis.Z) => new Vector4(_appearance.ZAxis.ToNormalizedVector3(), 1f),
            (GizmoColorPreset.ImGuizmo or GizmoColorPreset.Custom, _) => GizmoCenterColor,
            _ => EditorColors.GizmoAxisBase(axis),
        };

    private Vector4 GetAxisColorVector(GizmoAxis axis, bool isActive, bool isHovered)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            return ThemeColors.Text;
        }

        Vector4 baseColor = GetGizmoAxisBase(axis);
        if (_appearance.Preset != GizmoColorPreset.Default)
        {
            return baseColor;
        }

        float intensity = (isActive, isHovered) switch
        {
            (true, _) => 1.35f,
            (_, true) => 1.15f,
            _ => 0.95f,
        };
        return ThemeColors.Color(
            MathF.Min(baseColor.X * intensity, 1f),
            MathF.Min(baseColor.Y * intensity, 1f),
            MathF.Min(baseColor.Z * intensity, 1f),
            0.95f);
    }

    private Vector4 GetAxisBackgroundColorVector(GizmoAxis axis, bool isActive)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            return ThemeColors.TextDisabled;
        }

        Vector4 baseColor = GetGizmoAxisBase(axis);
        return ThemeColors.Color(baseColor.X, baseColor.Y, baseColor.Z, isActive ? 0.45f : 0.20f);
    }

    private float ResolveGizmoAlpha(bool isFocused)
        => isFocused || _appearance.Preset != GizmoColorPreset.Default ? GizmoConstants.ActiveAlpha : GizmoConstants.IdleAlpha;

    private float ResolveGizmoHoverOpacity(in GizmoInteractionState interaction, bool isHovered)
        => _appearance.Preset == GizmoColorPreset.Default && interaction.IsHovering && !isHovered
            ? GizmoConstants.IdleAlpha / GizmoConstants.ActiveAlpha : 1f;

    private float ResolveGizmoHandleOpacity(in GizmoFrame frame, GizmoTransformMode operation, GizmoAxis axis)
        => ResolveGizmoHoverOpacity(frame.Interaction,
            frame.HoveredHandle.Operation == operation && frame.HoveredHandle.Axis == axis);

    private static float ClampGizmoAlpha(float value)
        => Math.Clamp(value, 0f, 1f);
}
