using Intoner.Objects.Models;
using Intoner.Scene;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

/// <summary> editable gizmo properties for the editor </summary>
internal sealed class GizmoSettings
{
    public static readonly SceneTransformSnapSettings DefaultTransformSnapSettings = new();

    public static readonly SceneBoundsInteractionSettings DefaultBoundsInteractionSettings =
        new(
            SelectionEnabled: true,
            BoundsEnabled: false,
            BoundsFilter: SceneBoundsCategory.All,
            ShowSelectedOnly: false,
            InactiveBoundsOpacity: 0.28f,
            SelectedBoundsOpacity: 0.68f);

    public BoundsOverlaySpace BoundsOverlaySpace { get; set; } = BoundsOverlaySpace.World;

    public GizmoTransformMode Mode { get; set; } = GizmoTransformMode.Translation;

    public bool SurfaceAlignToNormal { get; set; }

    public bool SurfaceItemTargetsEnabled { get; set; } = true;

    public SceneSurfaceTargetShape SurfaceTargetShape { get; set; } = SceneSurfaceTargetShape.Bounds;

    public SceneTransformSnapSettings TransformSnapSettings { get; set; } = DefaultTransformSnapSettings;

    public SceneBoundsInteractionSettings BoundsInteractionSettings { get; set; } = DefaultBoundsInteractionSettings;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneBoundsInteractionSettings(
    bool SelectionEnabled,
    bool BoundsEnabled,
    SceneBoundsCategory BoundsFilter,
    bool ShowSelectedOnly,
    float InactiveBoundsOpacity,
    float SelectedBoundsOpacity)
{
    public bool Includes(SceneBoundsCategory category)
        => (BoundsFilter & category) == category;

    public bool ShouldDraw(SceneBoundsCategory category, bool selected)
        => Includes(category) && (!ShowSelectedOnly || selected);
}

internal enum SceneSurfaceTargetShape
{
    Bounds,
    Geometry,
}
