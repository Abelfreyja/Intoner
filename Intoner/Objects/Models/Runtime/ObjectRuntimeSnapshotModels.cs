using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Models;

internal sealed record ObjectBoundsSnapshot : SceneItemBoundsSnapshot
{
    public ObjectBoundsSnapshot(
        Guid id,
        string name,
        ObjectKind kind,
        Vector3 min,
        Vector3 max,
        OrientedBounds? localBounds,
        ObjectPlacementClearance? placementClearance,
        ObjectPlacementSurfaceSupport placementSurfaceSupport,
        IReadOnlyList<ObjectOverlayShapeSnapshot>? overlayShapes)
        : base(
            id,
            ResolveCategory(kind),
            min,
            max,
            localBounds is { } bounds
                ? new SceneOrientedBounds(bounds.Transform, bounds.HalfExtents)
                : null,
            kind is ObjectKind.BgObject or ObjectKind.Furniture)
    {
        Name = name;
        Kind = kind;
        LocalBounds = localBounds;
        PlacementClearance = placementClearance;
        PlacementSurfaceSupport = placementSurfaceSupport;
        OverlayShapes = overlayShapes;
    }

    public string Name { get; }
    public ObjectKind Kind { get; }
    public OrientedBounds? LocalBounds { get; }
    public ObjectPlacementClearance? PlacementClearance { get; }
    public ObjectPlacementSurfaceSupport PlacementSurfaceSupport { get; }
    public IReadOnlyList<ObjectOverlayShapeSnapshot>? OverlayShapes { get; init; }

    public bool HasSameContent(ObjectBoundsSnapshot other)
        => Id == other.Id
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && Kind == other.Kind
           && Min == other.Min
           && Max == other.Max
           && Equals(LocalBounds, other.LocalBounds)
           && PlacementClearance == other.PlacementClearance
           && PlacementSurfaceSupport == other.PlacementSurfaceSupport
           && OverlayShapesEqual(OverlayShapes, other.OverlayShapes);

    private static SceneBoundsCategory ResolveCategory(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => SceneBoundsCategory.BgObject,
            ObjectKind.Furniture => SceneBoundsCategory.Furniture,
            ObjectKind.Vfx => SceneBoundsCategory.Vfx,
            ObjectKind.Light => SceneBoundsCategory.Light,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool OverlayShapesEqual(
        IReadOnlyList<ObjectOverlayShapeSnapshot>? left,
        IReadOnlyList<ObjectOverlayShapeSnapshot>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; ++index)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectPlacementClearance(
    float Radius,
    float SnapAboveSurface,
    float SnapBelowSurface)
{
    public static bool TryCreate(float radius, out ObjectPlacementClearance clearance)
    {
        if (!IsValidRange(radius))
        {
            clearance = default;
            return false;
        }

        clearance = new ObjectPlacementClearance(radius, radius, radius);
        return true;
    }

    public bool IsValid
        => IsValidRange(Radius)
        && IsValidRange(SnapAboveSurface)
        && IsValidRange(SnapBelowSurface);

    private static bool IsValidRange(float value)
        => float.IsFinite(value) && value >= 0f;
}

[Flags]
internal enum ObjectPlacementSurfaceSupport
{
    None = 0,
    Tabletop = 1 << 0,
    Wall = 1 << 1,
}

internal enum ObjectOverlayShapeKind
{
    Sphere,
    Cone,
    Box,
}

internal sealed record ObjectOverlayShapeSnapshot(
    ObjectOverlayShapeKind Kind,
    Matrix4x4 Transform,
    float Extent,
    float AngleDegrees,
    float OpacityScale = 1f);

internal enum ObjectSceneSourceKind
{
    Standalone = 1,
    DefaultLayout = 2,
    TemporaryLayout = 3,
}

internal enum ObjectSceneLifetimeKind
{
    LocalPersistent = 1,
    RuntimeOnly = 2,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectSceneSource(
    ObjectSceneSourceKind Kind,
    Guid? LayoutId,
    string SourceKey)
{
    public static ObjectSceneSource CreateStandalone(Guid? layoutId = null)
        => new(ObjectSceneSourceKind.Standalone, layoutId, string.Empty);

    public static ObjectSceneSource CreateDefaultLayout(Guid layoutId)
        => new(ObjectSceneSourceKind.DefaultLayout, layoutId, layoutId.ToString("D"));

    public static ObjectSceneSource CreateTemporaryLayout(string sourceKey)
        => new(ObjectSceneSourceKind.TemporaryLayout, null, sourceKey);

    public ObjectSceneLifetimeKind Lifetime
        => Kind switch
        {
            ObjectSceneSourceKind.Standalone => ObjectSceneLifetimeKind.LocalPersistent,
            ObjectSceneSourceKind.DefaultLayout => ObjectSceneLifetimeKind.LocalPersistent,
            ObjectSceneSourceKind.TemporaryLayout => ObjectSceneLifetimeKind.RuntimeOnly,
            _ => ObjectSceneLifetimeKind.RuntimeOnly,
        };

    public bool IsLocalPersistent
        => Lifetime == ObjectSceneLifetimeKind.LocalPersistent;

    public bool IsRuntimeOnly
        => Lifetime == ObjectSceneLifetimeKind.RuntimeOnly;

    public bool UsesUserHousingPolicy
        => IsLocalPersistent;
}

internal enum ObjectRuntimeStateKind
{
    Active = 1,
    Inactive = 2,
    LocationMismatch = 3,
    LoadFailed = 4,
}

internal sealed record ObjectRuntimeStateSnapshot(
    Guid Id,
    ObjectRuntimeStateKind State,
    string? FailureCode);

