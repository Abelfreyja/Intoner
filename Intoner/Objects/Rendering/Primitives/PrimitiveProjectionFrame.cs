using Intoner.Objects.Utils;
using SharpDX.Mathematics.Interop;
using System.Numerics;

namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct PrimitiveProjectionFrame(
    Matrix4x4 ViewProjection,
    Matrix4x4 ViewMatrix,
    float NearPlane,
    Matrix4x4 InverseProjection,
    bool ReverseDepth,
    bool ForwardPositive,
    RawViewportF Viewport)
{
    public static bool TryCaptureMainView(RawViewportF viewport, out PrimitiveProjectionFrame frame)
    {
        frame = default;
        if (!ObjectViewportProjectionUtility.TryGetMainRenderViewProjection(
                out var viewProjection,
                out var viewMatrix,
                out var projectionMatrix,
                out var nearPlane,
                out var reverseDepth))
        {
            return false;
        }

        if (!Matrix4x4.Invert(projectionMatrix, out var inverseProjection)
            || !ObjectViewportProjectionUtility.TryResolveForwardViewDepthSign(projectionMatrix, nearPlane, reverseDepth, out var forwardPositive))
        {
            return false;
        }

        frame = new PrimitiveProjectionFrame(viewProjection, viewMatrix, nearPlane, inverseProjection, reverseDepth, forwardPositive, viewport);
        return true;
    }
}

