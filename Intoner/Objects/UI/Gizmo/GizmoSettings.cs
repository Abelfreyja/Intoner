using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> editable gizmo properties for the editor </summary>
internal sealed class GizmoSettings
{
    public static readonly ObjectTransformSnapSettings DefaultTransformSnapSettings = new();

    public static readonly ObjectBoundsSelectedColors DefaultSelectedBoundsColors =
        new(
            BgObject: EditorColors.AccentOrange,
            Furniture: EditorColors.AccentBlue,
            Light: EditorColors.AccentGreen,
            Vfx: EditorColors.AccentYellow);

    public static readonly ObjectBoundsInteractionSettings DefaultBoundsInteractionSettings =
        new(
            SelectionEnabled: true,
            BoundsEnabled: false,
            BoundsFilter: ObjectKind.Light | ObjectKind.BgObject | ObjectKind.Furniture | ObjectKind.Vfx,
            ShowSelectedOnly: false,
            InactiveBoundsOpacity: 0.28f,
            SelectedBoundsOpacity: 0.68f,
            SelectedBoundsColors: DefaultSelectedBoundsColors);

    public BoundsOverlaySpace BoundsOverlaySpace { get; set; } = BoundsOverlaySpace.World;

    public GizmoTransformMode Mode { get; set; } = GizmoTransformMode.Translation;

    public bool SurfaceAlignToNormal { get; set; }

    public bool SurfaceObjectTargetsEnabled { get; set; } = true;

    public SurfaceObjectTargetShape SurfaceObjectTargetShape { get; set; } = SurfaceObjectTargetShape.Bounds;

    public ObjectTransformSnapSettings TransformSnapSettings { get; set; } = DefaultTransformSnapSettings;

    public ObjectBoundsInteractionSettings BoundsInteractionSettings { get; set; } = DefaultBoundsInteractionSettings;
}

internal readonly record struct ObjectBoundsInteractionSettings(
    bool SelectionEnabled,
    bool BoundsEnabled,
    ObjectKind BoundsFilter,
    bool ShowSelectedOnly,
    float InactiveBoundsOpacity,
    float SelectedBoundsOpacity,
    ObjectBoundsSelectedColors SelectedBoundsColors);

internal readonly record struct ObjectBoundsSelectedColors(
    Vector4 BgObject,
    Vector4 Furniture,
    Vector4 Light,
    Vector4 Vfx);

internal enum SurfaceObjectTargetShape
{
    Bounds,
    Geometry,
}

