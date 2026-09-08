using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

[Flags]
internal enum SceneBoundsCategory
{
    None = 0,
    BgObject = 1 << 0,
    Furniture = 1 << 1,
    Vfx = 1 << 2,
    Light = 1 << 3,
    Display = 1 << 4,
    All = BgObject | Furniture | Vfx | Light | Display,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneOrientedBounds(Matrix4x4 Transform, Vector3 HalfExtents);

/// <summary> describes the world bounds exposed by one active scene item </summary>
internal record SceneItemBoundsSnapshot(
    Guid Id,
    SceneBoundsCategory Category,
    Vector3 Min,
    Vector3 Max,
    SceneOrientedBounds? OrientedBounds,
    bool IsManipulationBounds);
