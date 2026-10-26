using System.Numerics;

namespace Intoner.Objects.Models;

internal enum BgCollisionKind
{
    None,
    Mesh,
    Box,
    Sphere,
    Cylinder,
    Plane,
}

internal sealed record BgAnalyticShape(
    Vector3 Translation,
    Vector3 Rotation,
    Vector3 Scale,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    uint MaterialId,
    uint MaterialMask);

internal sealed record BgColliderDefinition(
    string ModelPath,
    string SourcePath,
    BgCollisionKind Kind,
    string PcbPath,
    ulong MaterialId,
    ulong MaterialMask,
    BgAnalyticShape? Shape);
