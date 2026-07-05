using System.Numerics;

namespace Intoner.Objects.Runtime;

internal readonly record struct ObjectSurfaceHit(
    Vector3 Point,
    Vector3 Normal,
    ulong Material = 0,
    nint ColliderAddress = 0,
    float Distance = 0f,
    ObjectSurfaceHitSource Source = ObjectSurfaceHitSource.Unknown,
    Guid TargetObjectId = default)
{
    public static ObjectSurfaceHit Empty { get; } = new(Vector3.Zero, Vector3.Zero);

    public bool HasCollider
        => ColliderAddress != 0;

    public bool HasObjectTarget
        => TargetObjectId != Guid.Empty
           && Source is ObjectSurfaceHitSource.ObjectBounds or ObjectSurfaceHitSource.ObjectGeometry;

    public bool HasMaterial(ulong materialMask)
        => (Material & materialMask) != 0;
}
