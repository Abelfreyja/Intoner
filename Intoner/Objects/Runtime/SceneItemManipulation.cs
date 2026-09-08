using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneItemManipulation(
    bool SupportsScale,
    Vector3 SurfaceAlignmentAxis,
    bool ForceSurfaceAlignment,
    float? GizmoAxisLength = null,
    bool SupportsSurfaceDrag = true)
{
    public static SceneItemManipulation Default { get; } = new(
        SupportsScale: false,
        SurfaceAlignmentAxis: Vector3.UnitY,
        ForceSurfaceAlignment: false,
        SupportsSurfaceDrag: false);
}

internal enum SceneSurfaceHitSource
{
    Unknown,
    Native,
    ItemBounds,
    ItemGeometry,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSurfaceHit(
    Vector3 Point,
    Vector3 Normal,
    ulong Material = 0,
    nint ColliderAddress = 0,
    float Distance = 0f,
    SceneSurfaceHitSource Source = SceneSurfaceHitSource.Unknown,
    Guid TargetItemId = default)
{
    public static SceneSurfaceHit Empty { get; } = new(Vector3.Zero, Vector3.Zero);

    public bool HasCollider
        => ColliderAddress != 0;

    public bool HasItemTarget
        => TargetItemId != Guid.Empty
           && Source is SceneSurfaceHitSource.ItemBounds or SceneSurfaceHitSource.ItemGeometry;

    public bool HasMaterial(ulong materialMask)
        => (Material & materialMask) != 0;
}

/// <summary> provides manipulation behavior for one scene item domain </summary>
internal abstract class SceneItemManipulationPolicy
{
    /// <summary> describes the gizmo behavior supported by one scene item </summary>
    public abstract SceneItemManipulation Describe(SceneItemSnapshot snapshot);

    /// <summary> resolves a domain specific surface hit before the shared world fallback runs </summary>
    public virtual bool TryResolveSurfaceHit(
        SceneItemSnapshot snapshot,
        SceneItemBoundsSnapshot? bounds,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        return false;
    }

    /// <summary> checks whether the item uses its authored origin on the resolved surface </summary>
    public virtual bool UsesSurfaceOrigin(SceneItemSnapshot snapshot, SceneSurfaceHit hit)
        => false;

    /// <summary> applies domain specific state associated with a surface drag </summary>
    public virtual SceneItemSnapshot ApplySurfaceState(SceneItemSnapshot snapshot, SceneSurfaceHit? hit)
        => snapshot;
}
