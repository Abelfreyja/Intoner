using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class GizmoTranslationMath
{
    /// <summary> resolves the projected screen length of one gizmo axis from the current view </summary>
    public static float ResolveDragScreenLength(
        Matrix4x4 viewProjection,
        Vector3 pivotPosition,
        Vector2 screenPos,
        Vector2 viewportPos,
        Vector2 viewportSize,
        Vector3 axisWorldDirection,
        float axisWorldLength,
        float fallbackScreenLength)
    {
        var axisWorldEnd = pivotPosition + (axisWorldDirection * axisWorldLength);
        if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                viewProjection,
                axisWorldEnd,
                viewportPos,
                viewportSize,
                out var projectedScreenEnd))
        {
            return fallbackScreenLength;
        }

        var projectedLength = (projectedScreenEnd - screenPos).Length();
        return projectedLength > float.Epsilon
            ? projectedLength
            : fallbackScreenLength;
    }

    /// <summary> resolves a stable drag plane normal for one translation axis </summary>
    public static bool TryResolveDragPlaneNormal(
        Vector3 axisWorldDirection,
        Vector3? cameraViewDirection,
        Vector3? cameraRight,
        Vector3? cameraUp,
        out Vector3 planeNormal)
    {
        planeNormal = Vector3.Zero;
        if (!ObjectMathUtility.TryNormalize(axisWorldDirection, out var axisDirection))
        {
            return false;
        }

        if (TryResolveDragPlaneNormalFromCandidate(axisDirection, cameraViewDirection, out planeNormal)
            || TryResolveDragPlaneNormalFromCandidate(axisDirection, cameraRight, out planeNormal)
            || TryResolveDragPlaneNormalFromCandidate(axisDirection, cameraUp, out planeNormal))
        {
            return true;
        }

        var fallbackDirection = MathF.Abs(Vector3.Dot(axisDirection, Vector3.UnitY)) < 0.95f
            ? Vector3.UnitY
            : Vector3.UnitX;
        return TryResolveDragPlaneNormalFromCandidate(axisDirection, fallbackDirection, out planeNormal);
    }

    private static bool TryResolveDragPlaneNormalFromCandidate(
        Vector3 axisDirection,
        Vector3? candidateDirection,
        out Vector3 planeNormal)
    {
        planeNormal = Vector3.Zero;
        if (!candidateDirection.HasValue)
        {
            return false;
        }

        return ObjectSelectionTransformMath.TryProjectDirectionOntoPlane(candidateDirection.Value, axisDirection, out planeNormal);
    }
}

