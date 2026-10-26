using Intoner.Scene;
using Intoner.Scene.Rendering;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Drawing;

/// <summary> keeps a screen overlay attached to a world point without changing its pixel dimensions </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ScreenAnchor(Vector3 WorldPosition, Vector2 SubmittedPosition)
{
    public bool TryResolveOffset(in SceneProjection projection, out Vector2 offset)
    {
        offset = default;
        if (!SceneViewportProjection.TryProjectWorldPointToViewport(
                projection.ViewProjection, WorldPosition,
                new Vector2(projection.Viewport.X, projection.Viewport.Y),
                new Vector2(projection.Viewport.Width, projection.Viewport.Height), out Vector2 position))
        {
            return false;
        }

        offset = position - SubmittedPosition;
        return true;
    }
}
