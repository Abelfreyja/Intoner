using FFXIVClientStructs.FFXIV.Client.Game.Control;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectViewportProjectionUtility
{
    // native render view camera lookup indexes from the manager base, so these offsets include the view table offset
    private const int MainRenderViewIndex = (int)RenderManager.RenderViews.Main;
    private const int RenderViewStride = 0x190;
    private const int RenderViewFlagsOffset = 0x10;
    private const int RenderViewCameraOffset = 0x148;
    private const uint RenderViewEnabledFlag = 0x1;

    private const float DepthResolveMinEpsilon = 0.05f;
    private const float DepthResolveFarDistance = 100f;
    private const float DepthResolveTolerance = 0.0001f;

    public static unsafe bool TryGetActiveCameraProjection(out Matrix4x4 viewProjection, out Matrix4x4 viewMatrix, out float nearPlane)
    {
        viewProjection = default;
        viewMatrix = default;
        nearPlane = 0f;

        var control = Control.Instance();
        if (control == null)
        {
            return false;
        }

        var activeCamera = control->CameraManager.GetActiveCamera();
        var renderCamera = activeCamera != null ? activeCamera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
        {
            return false;
        }

        viewProjection = control->ViewProjectionMatrix;
        viewMatrix = activeCamera->SceneCamera.ViewMatrix;
        viewMatrix.M44 = 1f;
        nearPlane = renderCamera->NearPlane;
        return true;
    }

    public static bool TryGetEditorCameraProjection(out Matrix4x4 viewProjection, out Matrix4x4 viewMatrix, out float nearPlane)
    {
        if (TryGetMainRenderViewProjection(out viewProjection, out viewMatrix, out _, out nearPlane, out _))
        {
            return true;
        }

        return TryGetActiveCameraProjection(out viewProjection, out viewMatrix, out nearPlane);
    }

    public static unsafe bool TryGetMainRenderViewProjection(
        out Matrix4x4 viewProjection,
        out Matrix4x4 viewMatrix,
        out Matrix4x4 projectionMatrix,
        out float nearPlane,
        out bool reverseDepth)
    {
        viewProjection = default;
        viewMatrix = default;
        projectionMatrix = default;
        nearPlane = 0f;
        reverseDepth = false;

        var manager = RenderManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var viewAddress = (nint)manager + (MainRenderViewIndex * RenderViewStride);
        if ((*(uint*)(viewAddress + RenderViewFlagsOffset) & RenderViewEnabledFlag) == 0)
        {
            return false;
        }

        var camera = *(RenderCamera**)(viewAddress + RenderViewCameraOffset);
        if (camera == null)
        {
            return false;
        }

        viewMatrix = (Matrix4x4)camera->ViewMatrix;
        viewMatrix.M44 = 1f;
        projectionMatrix = (Matrix4x4)camera->ProjectionMatrix;
        viewProjection = Matrix4x4.Multiply(viewMatrix, projectionMatrix);
        nearPlane = camera->NearPlane;
        reverseDepth = !camera->StandardZ;
        return true;
    }

    public static bool TryResolveReverseDepth(
        Matrix4x4 viewProjection,
        Matrix4x4 viewMatrix,
        float nearPlane,
        out bool reverseDepth)
    {
        reverseDepth = false;
        if (!Matrix4x4.Invert(viewMatrix, out var inverseView))
        {
            return false;
        }

        var projection = inverseView * viewProjection;
        var nearDistance = MathF.Max(nearPlane + DepthResolveMinEpsilon, DepthResolveMinEpsilon);
        var farDistance = nearDistance + DepthResolveFarDistance;

        return TryResolveProjectedDepthOrder(projection, nearDistance, farDistance, 0f, 1f, out reverseDepth)
            || TryResolveProjectedDepthOrder(projection, -nearDistance, -farDistance, 0f, 1f, out reverseDepth)
            || TryResolveProjectedDepthOrder(projection, nearDistance, farDistance, -1f, 1f, out reverseDepth)
            || TryResolveProjectedDepthOrder(projection, -nearDistance, -farDistance, -1f, 1f, out reverseDepth);
    }

    public static bool TryResolveForwardViewDepthSign(
        Matrix4x4 projection,
        float nearPlane,
        bool reverseDepth,
        out bool forwardPositive)
    {
        var nearDistance = MathF.Max(nearPlane + DepthResolveMinEpsilon, DepthResolveMinEpsilon);
        var farDistance = nearDistance + DepthResolveFarDistance;
        if (TryProjectedDepthOrderMatches(projection, nearDistance, farDistance, reverseDepth))
        {
            forwardPositive = true;
            return true;
        }

        if (TryProjectedDepthOrderMatches(projection, -nearDistance, -farDistance, reverseDepth))
        {
            forwardPositive = false;
            return true;
        }

        forwardPositive = false;
        return false;
    }

    public static bool TryProjectWorldPointToViewport(
        Matrix4x4 viewProjection,
        Vector3 worldPoint,
        Vector2 viewportPos,
        Vector2 viewportSize,
        out Vector2 screenPoint)
    {
        screenPoint = default;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
        {
            return false;
        }

        if (!TryProjectWorldPointToNdc(viewProjection, worldPoint, out var ndc))
        {
            return false;
        }

        screenPoint = new Vector2(
            (ndc.X + 1f) * viewportSize.X * 0.5f,
            (1f - ndc.Y) * viewportSize.Y * 0.5f) + viewportPos;
        return ObjectMathUtility.IsFinite(screenPoint);
    }

    public static bool TryProjectWorldPointToNdc(
        Matrix4x4 viewProjection,
        Vector3 worldPoint,
        out Vector3 ndc)
    {
        ndc = default;
        var projected = Vector4.Transform(new Vector4(worldPoint, 1f), viewProjection);
        if (!float.IsFinite(projected.W) || projected.W <= float.Epsilon)
        {
            return false;
        }

        projected *= 1f / projected.W;
        ndc = new Vector3(projected.X, projected.Y, projected.Z);
        return ObjectMathUtility.IsFinite(ndc);
    }

    public static bool TryProjectWorldLineToViewport(
        Vector3 start,
        Vector3 end,
        Vector2 viewportPos,
        Vector2 viewportSize,
        out Vector2 screenStart,
        out Vector2 screenEnd)
    {
        screenStart = default;
        screenEnd = default;

        if (!TryGetActiveCameraProjection(out var viewProjection, out var viewMatrix, out var nearPlane))
        {
            return false;
        }

        return TryProjectWorldLineToViewport(
            viewProjection,
            viewMatrix,
            nearPlane,
            start,
            end,
            viewportPos,
            viewportSize,
            out screenStart,
            out screenEnd);
    }

    public static bool TryProjectWorldLineToViewport(
        Matrix4x4 viewProjection,
        Matrix4x4 viewMatrix,
        float nearPlane,
        Vector3 start,
        Vector3 end,
        Vector2 viewportPos,
        Vector2 viewportSize,
        out Vector2 screenStart,
        out Vector2 screenEnd)
    {
        screenStart = default;
        screenEnd = default;

        if (!TryClipWorldLineToNearPlane(viewMatrix, nearPlane, ref start, ref end))
        {
            return false;
        }

        return TryProjectWorldPointToViewport(viewProjection, start, viewportPos, viewportSize, out screenStart)
            && TryProjectWorldPointToViewport(viewProjection, end, viewportPos, viewportSize, out screenEnd);
    }

    public static bool TryClipWorldLineToNearPlane(Matrix4x4 viewMatrix, float nearPlane, ref Vector3 start, ref Vector3 end)
    {
        var nearClipPlane = new Vector4(viewMatrix.M13, viewMatrix.M23, viewMatrix.M33, viewMatrix.M43 + nearPlane);
        return TryClipLineToPlane(nearClipPlane, ref start, ref end);
    }

    private static bool TryClipLineToPlane(Vector4 plane, ref Vector3 start, ref Vector3 end)
    {
        var startDot = Vector4.Dot(new Vector4(start, 1f), plane);
        var endDot = Vector4.Dot(new Vector4(end, 1f), plane);
        var startVisible = startDot < 0f;
        var endVisible = endDot < 0f;
        if (startVisible && endVisible)
        {
            return true;
        }

        if (!startVisible && !endVisible)
        {
            return false;
        }

        var segment = end - start;
        var denominator = Vector3.Dot(segment, new Vector3(plane.X, plane.Y, plane.Z));
        if (MathF.Abs(denominator) < float.Epsilon)
        {
            return false;
        }

        var t = -startDot / denominator;
        var clippedPoint = start + (segment * t);
        if (startVisible)
        {
            end = clippedPoint;
        }
        else
        {
            start = clippedPoint;
        }

        return true;
    }

    private static bool TryResolveProjectedDepthOrder(
        Matrix4x4 projection,
        float nearViewSpaceDepth,
        float farViewSpaceDepth,
        float minDepth,
        float maxDepth,
        out bool reverseDepth)
    {
        reverseDepth = false;
        if (!TryProjectDepth(projection, nearViewSpaceDepth, out var nearDepth)
            || !TryProjectDepth(projection, farViewSpaceDepth, out var farDepth))
        {
            return false;
        }

        if (!IsDepthInRange(nearDepth, minDepth, maxDepth)
            || !IsDepthInRange(farDepth, minDepth, maxDepth)
            || MathF.Abs(nearDepth - farDepth) < DepthResolveTolerance)
        {
            return false;
        }

        reverseDepth = nearDepth > farDepth;
        return true;
    }

    private static bool TryProjectedDepthOrderMatches(
        Matrix4x4 projection,
        float nearViewSpaceDepth,
        float farViewSpaceDepth,
        bool reverseDepth)
    {
        return TryProjectDepth(projection, nearViewSpaceDepth, out var nearDepth)
            && TryProjectDepth(projection, farViewSpaceDepth, out var farDepth)
            && IsDepthInRange(nearDepth, 0f, 1f)
            && IsDepthInRange(farDepth, 0f, 1f)
            && MathF.Abs(nearDepth - farDepth) >= DepthResolveTolerance
            && (nearDepth > farDepth) == reverseDepth;
    }

    private static bool TryProjectDepth(Matrix4x4 projection, float viewSpaceDepth, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(0f, 0f, viewSpaceDepth, 1f), projection);
        if (MathF.Abs(clip.W) < float.Epsilon)
        {
            depth = 0f;
            return false;
        }

        depth = clip.Z / clip.W;
        return float.IsFinite(depth);
    }

    private static bool IsDepthInRange(float depth, float minDepth, float maxDepth)
        => float.IsFinite(depth)
            && depth >= minDepth - DepthResolveTolerance
            && depth <= maxDepth + DepthResolveTolerance;
}

