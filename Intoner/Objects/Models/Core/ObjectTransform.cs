using System.Numerics;

namespace Intoner.Objects.Models;

internal sealed record ObjectTransform
{
    public Vector3 Position { get; init; }
    public Vector3 RotationDegrees { get; init; }
    public Vector3 Scale { get; init; } = Vector3.One;
}

