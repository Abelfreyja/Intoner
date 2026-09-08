using Intoner.Objects.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

/// <summary> immutable flat hierarchy used to accelerate scene geometry raycasts </summary>
internal sealed class SceneGeometryAcceleration
{
    private const int LeafTriangleCount = 8;
    private const int MinimumTriangleCount = LeafTriangleCount * 2;
    private const int MaximumDepth = 64;
    private const float SplitEpsilon = 0.000001f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public Vector3 BoundsMin;
        public int First;
        public Vector3 BoundsMax;
        public int Count;

        public readonly bool IsLeaf
            => Count > 0;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BuildItem(int NodeIndex, int Depth);

    [StructLayout(LayoutKind.Sequential)]
    private struct TriangleBuildData
    {
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public Vector3 Centroid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public Vector3 Min;
        public Vector3 Max;

        public static Bounds Empty => new()
        {
            Min = new Vector3(float.PositiveInfinity),
            Max = new Vector3(float.NegativeInfinity),
        };

        public void Include(Vector3 min, Vector3 max)
        {
            Min = Vector3.Min(Min, min);
            Max = Vector3.Max(Max, max);
        }

        public void Include(in TriangleBuildData triangle)
            => Include(triangle.BoundsMin, triangle.BoundsMax);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TraversalEntry(int NodeIndex, float NearDistance);

    private readonly int[] _triangleOrder;
    private Node[] _nodes;

    private SceneGeometryAcceleration(TriangleBuildData[] buildData, int[] triangleOrder, CancellationToken cancellationToken)
    {
        _triangleOrder = triangleOrder;
        int estimatedLeafCount = (triangleOrder.Length + LeafTriangleCount - 1) / LeafTriangleCount;
        _nodes = new Node[Math.Max(1, checked(estimatedLeafCount * 2))];
        int nodeCount = 1;
        _nodes[0] = CreateLeaf(0, triangleOrder.Length, buildData);

        var pending = new Stack<BuildItem>();
        pending.Push(new BuildItem(0, 1));
        while (pending.TryPop(out BuildItem item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Node node = _nodes[item.NodeIndex];
            if (node.Count <= LeafTriangleCount
                || item.Depth >= MaximumDepth
                || !TryFindSplit(node, buildData, out int axis, out float splitValue))
            {
                continue;
            }

            int leftCount = Partition(node.First, node.Count, buildData, axis, splitValue);
            if (leftCount == 0 || leftCount == node.Count)
            {
                continue;
            }

            EnsureNodeCapacity(nodeCount + 2);
            int leftNodeIndex = nodeCount;
            nodeCount += 2;
            _nodes[leftNodeIndex] = CreateLeaf(node.First, leftCount, buildData);
            _nodes[leftNodeIndex + 1] = CreateLeaf(node.First + leftCount, node.Count - leftCount, buildData);
            _nodes[item.NodeIndex] = new Node
            {
                BoundsMin = node.BoundsMin,
                BoundsMax = node.BoundsMax,
                First = leftNodeIndex,
                Count = 0,
            };

            int childDepth = item.Depth + 1;
            pending.Push(new BuildItem(leftNodeIndex, childDepth));
            pending.Push(new BuildItem(leftNodeIndex + 1, childDepth));
        }

        if (nodeCount != _nodes.Length)
        {
            Array.Resize(ref _nodes, nodeCount);
        }
    }

    public static SceneGeometryAcceleration? Build(SceneSelectionGeometry geometry, CancellationToken cancellationToken = default)
    {
        TriangleBuildData[] buildData = BuildTriangleData(geometry, cancellationToken, out int[] triangleOrder);
        return triangleOrder.Length >= MinimumTriangleCount
            ? new SceneGeometryAcceleration(buildData, triangleOrder, cancellationToken)
            : null;
    }

    public bool TryRaycast(
        SceneSelectionGeometry geometry,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out int closestTriangle,
        out float closestDistance)
    {
        closestTriangle = -1;
        closestDistance = maxDistance;
        if (!TryIntersectBounds(
                rayOrigin,
                rayDirection,
                _nodes[0].BoundsMin,
                _nodes[0].BoundsMax,
                maxDistance,
                out float rootNearDistance))
        {
            return false;
        }

        Span<TraversalEntry> stack = stackalloc TraversalEntry[MaximumDepth];
        stack[0] = new TraversalEntry(0, rootNearDistance);
        int stackCount = 1;
        while (stackCount > 0)
        {
            TraversalEntry entry = stack[--stackCount];
            if (entry.NearDistance > closestDistance)
            {
                continue;
            }

            ref readonly Node node = ref _nodes[entry.NodeIndex];
            if (node.IsLeaf)
            {
                int end = node.First + node.Count;
                for (int index = node.First; index < end; index++)
                {
                    int triangleIndex = _triangleOrder[index];
                    if (!SceneGeometryRaycaster.TryRaycastTriangleDistance(
                            geometry.Positions,
                            geometry.Indices,
                            triangleIndex,
                            rayOrigin,
                            rayDirection,
                            out float distance)
                        || distance >= maxDistance
                        || distance > closestDistance
                        || (distance == closestDistance && triangleIndex >= closestTriangle))
                    {
                        continue;
                    }

                    closestDistance = distance;
                    closestTriangle = triangleIndex;
                }

                continue;
            }

            int leftIndex = node.First;
            int rightIndex = leftIndex + 1;
            ref readonly Node left = ref _nodes[leftIndex];
            ref readonly Node right = ref _nodes[rightIndex];
            bool intersectsLeft = TryIntersectBounds(
                rayOrigin,
                rayDirection,
                left.BoundsMin,
                left.BoundsMax,
                closestDistance,
                out float leftNearDistance);
            bool intersectsRight = TryIntersectBounds(
                rayOrigin,
                rayDirection,
                right.BoundsMin,
                right.BoundsMax,
                closestDistance,
                out float rightNearDistance);

            if (intersectsLeft && intersectsRight)
            {
                if (leftNearDistance <= rightNearDistance)
                {
                    stack[stackCount++] = new TraversalEntry(rightIndex, rightNearDistance);
                    stack[stackCount++] = new TraversalEntry(leftIndex, leftNearDistance);
                }
                else
                {
                    stack[stackCount++] = new TraversalEntry(leftIndex, leftNearDistance);
                    stack[stackCount++] = new TraversalEntry(rightIndex, rightNearDistance);
                }
            }
            else if (intersectsLeft)
            {
                stack[stackCount++] = new TraversalEntry(leftIndex, leftNearDistance);
            }
            else if (intersectsRight)
            {
                stack[stackCount++] = new TraversalEntry(rightIndex, rightNearDistance);
            }
        }

        return closestTriangle >= 0;
    }

    private static TriangleBuildData[] BuildTriangleData(
        SceneSelectionGeometry geometry,
        CancellationToken cancellationToken,
        out int[] triangleOrder)
    {
        int triangleCount = geometry.Indices.Length / 3;
        var buildData = new TriangleBuildData[triangleCount];
        triangleOrder = new int[triangleCount];
        int validTriangleCount = 0;
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            if ((triangleIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int indexOffset = triangleIndex * 3;
            int index0 = geometry.Indices[indexOffset];
            int index1 = geometry.Indices[indexOffset + 1];
            int index2 = geometry.Indices[indexOffset + 2];
            if ((uint)index0 >= (uint)geometry.Positions.Length
                || (uint)index1 >= (uint)geometry.Positions.Length
                || (uint)index2 >= (uint)geometry.Positions.Length)
            {
                continue;
            }

            Vector3 v0 = geometry.Positions[index0];
            Vector3 v1 = geometry.Positions[index1];
            Vector3 v2 = geometry.Positions[index2];
            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
            if (!NumericsUtility.IsFinite(v0)
                || !NumericsUtility.IsFinite(v1)
                || !NumericsUtility.IsFinite(v2)
                || !NumericsUtility.HasLength(normal))
            {
                continue;
            }

            buildData[triangleIndex] = new TriangleBuildData
            {
                BoundsMin = Vector3.Min(v0, Vector3.Min(v1, v2)),
                BoundsMax = Vector3.Max(v0, Vector3.Max(v1, v2)),
                Centroid = (v0 + v1 + v2) / 3f,
            };
            triangleOrder[validTriangleCount++] = triangleIndex;
        }

        Array.Resize(ref triangleOrder, validTriangleCount);
        return buildData;
    }

    private Node CreateLeaf(int first, int count, TriangleBuildData[] buildData)
    {
        Bounds bounds = Bounds.Empty;
        int end = first + count;
        for (int index = first; index < end; index++)
        {
            bounds.Include(buildData[_triangleOrder[index]]);
        }

        return new Node
        {
            BoundsMin = bounds.Min,
            BoundsMax = bounds.Max,
            First = first,
            Count = count,
        };
    }

    private bool TryFindSplit(
        Node node,
        TriangleBuildData[] buildData,
        out int axis,
        out float splitValue)
    {
        Bounds centroidBounds = Bounds.Empty;
        int end = node.First + node.Count;
        for (int index = node.First; index < end; index++)
        {
            Vector3 centroid = buildData[_triangleOrder[index]].Centroid;
            centroidBounds.Include(centroid, centroid);
        }

        Vector3 extent = centroidBounds.Max - centroidBounds.Min;
        axis = LargestAxis(extent);
        float axisExtent = GetComponent(extent, axis);
        splitValue = GetComponent(centroidBounds.Min, axis) + (axisExtent * 0.5f);
        return float.IsFinite(splitValue) && axisExtent > SplitEpsilon;
    }

    private int Partition(
        int first,
        int count,
        TriangleBuildData[] buildData,
        int axis,
        float splitValue)
    {
        int left = first;
        int right = first + count - 1;
        while (left <= right)
        {
            int triangleIndex = _triangleOrder[left];
            if (GetComponent(buildData[triangleIndex].Centroid, axis) < splitValue)
            {
                left++;
                continue;
            }

            (_triangleOrder[left], _triangleOrder[right]) = (_triangleOrder[right], _triangleOrder[left]);
            right--;
        }

        return left - first;
    }

    private void EnsureNodeCapacity(int required)
    {
        if (required <= _nodes.Length)
        {
            return;
        }

        int maximum = checked((_triangleOrder.Length * 2) - 1);
        int capacity = Math.Min(maximum, Math.Max(required, checked(_nodes.Length * 2)));
        Array.Resize(ref _nodes, capacity);
    }

    private static bool TryIntersectBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 boundsMin,
        Vector3 boundsMax,
        float maxDistance,
        out float nearDistance)
    {
        float near = 0f;
        float far = maxDistance;
        if (!TryIntersectAxis(rayOrigin.X, rayDirection.X, boundsMin.X, boundsMax.X, ref near, ref far)
            || !TryIntersectAxis(rayOrigin.Y, rayDirection.Y, boundsMin.Y, boundsMax.Y, ref near, ref far)
            || !TryIntersectAxis(rayOrigin.Z, rayDirection.Z, boundsMin.Z, boundsMax.Z, ref near, ref far))
        {
            nearDistance = 0f;
            return false;
        }

        nearDistance = near;
        return far >= near && far > SceneRaycastMath.MinimumHitDistance;
    }

    private static bool TryIntersectAxis(
        float origin,
        float direction,
        float min,
        float max,
        ref float near,
        ref float far)
    {
        if (MathF.Abs(direction) <= float.Epsilon)
        {
            return origin >= min && origin <= max;
        }

        float inverseDirection = 1f / direction;
        float first = (min - origin) * inverseDirection;
        float second = (max - origin) * inverseDirection;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        near = MathF.Max(near, first);
        far = MathF.Min(far, second);
        return far >= near;
    }

    private static int LargestAxis(Vector3 extent)
    {
        if (extent.Y > extent.X)
        {
            return extent.Z > extent.Y ? 2 : 1;
        }

        return extent.Z > extent.X ? 2 : 0;
    }

    private static float GetComponent(Vector3 value, int axis)
        => axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z,
        };
}
