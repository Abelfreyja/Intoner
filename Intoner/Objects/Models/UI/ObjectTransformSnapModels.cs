using System.Numerics;

namespace Intoner.Objects.Models;

internal sealed record ObjectTransformSnapSettings
{
    public bool PositionEnabled { get; init; }
    public bool PositionDragEnabled { get; init; } = true;
    public float PositionStep { get; init; } = 0.05f;

    public bool RotationEnabled { get; init; }
    public float RotationStepDegrees { get; init; } = 1f;

    public bool ScaleEnabled { get; init; }
    public float ScaleStep { get; init; } = 0.01f;
}

internal readonly record struct ObjectSnapBasis(Vector3 Origin, Quaternion Rotation);

