using System.Numerics;

namespace Intoner.Scene;

internal sealed record SceneTransform
{
    public Vector3 Position { get; init; }
    public Vector3 RotationDegrees { get; init; }
    public Vector3 Scale { get; init; } = Vector3.One;
}

