using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Models;

internal sealed record ObjectBoundsSnapshot(
    Guid Id,
    string Name,
    ObjectKind Kind,
    Vector3 Min,
    Vector3 Max,
    OrientedBounds? LocalBounds,
    float? PlacementClearanceRadius,
    ObjectPlacementSurfaceSupport PlacementSurfaceSupport,
    ObjectOverlayShapeSnapshot? OverlayShape);

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
    SquarePyramid,
}

internal sealed record ObjectOverlayShapeSnapshot(
    ObjectOverlayShapeKind Kind,
    Matrix4x4 Transform,
    float Range,
    float AngleDegrees);

internal readonly record struct ObjectLocationScope(
    ushort WorldId,
    uint TerritoryId,
    uint DivisionId,
    uint WardId,
    uint HouseId,
    uint RoomId)
{
    public bool IsValid
        => WorldId != 0 && TerritoryId != 0;
}

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

