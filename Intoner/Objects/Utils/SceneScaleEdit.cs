using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Scene;

internal sealed class SceneScaleEdit
{
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct UniformBounds(float Offset, float Scaled);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BoundsProjection(Vector3 Center, Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ)
    {
        public float Resolve(Vector3 scale)
            => Vector3.Dot(Center, scale) - MathF.Abs(Vector3.Dot(AxisX, scale))
                - MathF.Abs(Vector3.Dot(AxisY, scale)) - MathF.Abs(Vector3.Dot(AxisZ, scale));
    }

    private readonly Vector3 _pivot;
    private readonly Vector3 _direction;
    private readonly UniformBounds[] _uniformBounds;
    private readonly float _anchorProjection;
    private readonly BoundsProjection _projection;
    private readonly bool _canAnchorIndividual;

    private SceneScaleEdit(bool canAnchorIndividual, Vector3 pivot, Vector3 direction,
        UniformBounds[] uniformBounds, float anchorProjection, BoundsProjection projection)
    {
        _canAnchorIndividual = canAnchorIndividual;
        _pivot = pivot;
        _direction = direction;
        _uniformBounds = uniformBounds;
        _anchorProjection = anchorProjection;
        _projection = projection;
    }

    public bool CanAnchor(bool uniform)
        => uniform ? _uniformBounds.Length > 0 : _canAnchorIndividual;

    public static bool TryCreate(IReadOnlyList<SceneItemSnapshot> snapshots, SceneItemBoundsLookup boundsLookup,
        Vector3 pivot, Vector3 direction, Func<SceneItemSnapshot, bool> supportsScale,
        [NotNullWhen(true)] out SceneScaleEdit? edit)
    {
        edit = null;

        if (snapshots.Count == 0 || !NumericsUtility.IsFinite(pivot)
            || !NumericsUtility.TryNormalize(direction, out direction)
            || !NumericsUtility.IsFinite(direction) || !NumericsUtility.HasLength(direction))
        {
            return false;
        }

        BoundsProjection projection = default;
        bool canAnchorIndividual = snapshots.Count == 1
            && boundsLookup.FindManipulationBounds(snapshots[0].Id)?.OrientedBounds is { } bounds
            && TryCaptureProjection(snapshots[0].Transform, bounds, direction, out projection);
        CaptureUniform(snapshots, boundsLookup, pivot, direction, supportsScale,
            out UniformBounds[] uniformBounds, out float anchorProjection);
        edit = new SceneScaleEdit(canAnchorIndividual, pivot, direction, uniformBounds, anchorProjection, projection);
        return true;
    }

    private static void CaptureUniform(IReadOnlyList<SceneItemSnapshot> snapshots, SceneItemBoundsLookup boundsLookup,
        Vector3 pivot, Vector3 direction, Func<SceneItemSnapshot, bool> supportsScale,
        out UniformBounds[] uniformBounds, out float anchorProjection)
    {
        uniformBounds = [];
        anchorProjection = float.PositiveInfinity;
        List<UniformBounds> captured = [];
        float scaledProjection = float.PositiveInfinity;
        float pivotProjection = Vector3.Dot(pivot, direction);

        if (!float.IsFinite(pivotProjection))
        {
            return;
        }

        foreach (SceneItemSnapshot snapshot in snapshots)
        {
            if (snapshot is SceneGroupSnapshot)
            {
                continue;
            }

            bool scaleSize = supportsScale(snapshot);
            SceneItemBoundsSnapshot? bounds = boundsLookup.FindManipulationBounds(snapshot.Id);

            if (bounds is null)
            {
                if (scaleSize)
                {
                    return;
                }

                continue;
            }

            if (!TryProjectBounds(bounds, direction, out float minimum)
                || !NumericsUtility.IsFinite(snapshot.Transform.Position))
            {
                return;
            }

            if (scaleSize)
            {
                scaledProjection = MathF.Min(scaledProjection, minimum);
            }
            else
            {
                float positionProjection = Vector3.Dot(snapshot.Transform.Position, direction);
                UniformBounds entry = new(minimum - positionProjection + pivotProjection, positionProjection - pivotProjection);

                if (!float.IsFinite(entry.Offset) || !float.IsFinite(entry.Scaled))
                {
                    return;
                }

                captured.Add(entry);
            }

            anchorProjection = MathF.Min(anchorProjection, minimum);
        }

        if (float.IsFinite(scaledProjection))
        {
            float distance = scaledProjection - pivotProjection;

            if (!float.IsFinite(distance))
            {
                return;
            }

            captured.Add(new UniformBounds(pivotProjection, distance));
        }

        if (captured.Count == 0)
        {
            return;
        }

        uniformBounds = [.. captured];
    }

    public bool TryApplyScale(SceneTransform start, Vector3 scale, bool uniform, bool anchored, out SceneTransform transform)
    {
        transform = start;

        if (anchored && !CanAnchor(uniform) || !NumericsUtility.IsFinite(scale)
            || scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
        {
            return false;
        }

        if (uniform)
        {
            float factor = scale.X / start.Scale.X;

            if (!TryResolveUniformOffset(factor, anchored, out Vector3 offset))
            {
                return false;
            }

            SceneTransform next = SceneTransformMath.ApplyUniformScale(start, _pivot, factor, true, offset) with { Scale = scale };

            if (!NumericsUtility.IsFinite(next.Position))
            {
                return false;
            }

            transform = next;
            return true;
        }

        Vector3 position = start.Position;

        if (anchored)
        {
            position += _direction * (_projection.Resolve(start.Scale) - _projection.Resolve(scale));
        }

        if (!NumericsUtility.IsFinite(position))
        {
            return false;
        }

        transform = start with { Position = position, Scale = scale };
        return true;
    }

    public bool TryResolveUniformOffset(float factor, bool anchored, out Vector3 offset)
    {
        offset = Vector3.Zero;

        if (anchored && !CanAnchor(true) || !float.IsFinite(factor) || factor <= 0f)
        {
            return false;
        }

        if (!anchored || factor.Equals(1f))
        {
            return true;
        }

        float nextBase = float.PositiveInfinity;
        foreach (UniformBounds bounds in _uniformBounds)
        {
            nextBase = MathF.Min(nextBase, bounds.Offset + bounds.Scaled * factor);
        }

        Vector3 correction = _direction * (_anchorProjection - nextBase);

        if (!NumericsUtility.IsFinite(correction))
        {
            return false;
        }

        offset = correction;
        return true;
    }

    private static bool TryProjectBounds(SceneItemBoundsSnapshot bounds, Vector3 direction, out float minimum)
    {
        minimum = 0f;
        Span<Vector3> corners = stackalloc Vector3[8];

        if (!ObjectShapeMath.IsValidBounds(new AxisAlignedBounds { Min = bounds.Min, Max = bounds.Max }))
        {
            return false;
        }

        if (bounds.OrientedBounds is { } oriented)
        {
            if (!ObjectShapeMath.IsValidBounds(new OrientedBounds { Transform = oriented.Transform, HalfExtents = oriented.HalfExtents }))
            {
                return false;
            }

            ObjectShapeMath.CopyOrientedBoxCorners(oriented, corners);
        }
        else
        {
            ObjectShapeMath.CopyAxisAlignedBoxCorners(bounds.Min, bounds.Max, corners);
        }

        minimum = float.PositiveInfinity;
        foreach (Vector3 corner in corners)
        {
            minimum = MathF.Min(minimum, Vector3.Dot(corner, direction));
        }

        return float.IsFinite(minimum);
    }

    private static bool TryCaptureProjection(SceneTransform transform, SceneOrientedBounds bounds, Vector3 direction,
        out BoundsProjection projection)
    {
        projection = default;

        if (!ObjectShapeMath.IsValidBounds(new OrientedBounds { Transform = bounds.Transform, HalfExtents = bounds.HalfExtents })
            || !Matrix4x4.Invert(SceneTransformMath.CreateWorldTransform(transform), out Matrix4x4 inverse))
        {
            return false;
        }

        Matrix4x4 local = bounds.Transform * inverse;
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(SceneTransformMath.CreateRotationQuaternion(transform.RotationDegrees));
        Vector3 normal = Vector3.TransformNormal(direction, Matrix4x4.Transpose(rotation));
        BoundsProjection captured = new(local.Translation * normal,
            new Vector3(local.M11, local.M12, local.M13) * bounds.HalfExtents.X * normal,
            new Vector3(local.M21, local.M22, local.M23) * bounds.HalfExtents.Y * normal,
            new Vector3(local.M31, local.M32, local.M33) * bounds.HalfExtents.Z * normal);

        if (!NumericsUtility.IsFinite(captured.Center) || !NumericsUtility.IsFinite(captured.AxisX)
            || !NumericsUtility.IsFinite(captured.AxisY) || !NumericsUtility.IsFinite(captured.AxisZ)
            || !float.IsFinite(captured.Resolve(transform.Scale)))
        {
            return false;
        }

        projection = captured;
        return true;
    }
}
