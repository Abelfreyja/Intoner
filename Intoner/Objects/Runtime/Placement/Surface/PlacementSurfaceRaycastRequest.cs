using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct PlacementSurfaceRaycastRequest(
    Guid ObjectId,
    Vector3 Origin,
    Vector3 Direction,
    float MaxDistance,
    ulong NativeMaterialMask = 0);
