using System.Numerics;

namespace Intoner.Objects.Runtime;

internal readonly record struct PlacementSurfaceRaycastRequest(
    Guid ObjectId,
    Vector3 Origin,
    Vector3 Direction,
    float MaxDistance,
    ulong NativeMaterialMask = 0);
