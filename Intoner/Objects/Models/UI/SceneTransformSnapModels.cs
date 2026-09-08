using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

internal sealed record SceneTransformSnapSettings
{
    public bool PositionEnabled { get; init; }
    public bool PositionDragEnabled { get; init; } = true;
    public float PositionStep { get; init; } = 0.05f;

    public bool RotationEnabled { get; init; }
    public float RotationStepDegrees { get; init; } = 1f;

    public bool ScaleEnabled { get; init; }
    public float ScaleStep { get; init; } = 0.01f;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneSnapBasis(Vector3 Origin, Quaternion Rotation);

