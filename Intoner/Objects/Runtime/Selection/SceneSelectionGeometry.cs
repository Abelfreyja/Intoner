using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Scene;

/// <summary> triangle geometry used by scene selection and ray queries </summary>
internal sealed class SceneSelectionGeometry
{
    private SceneGeometryAcceleration? _acceleration;
    private int _accelerationBuildStarted;

    public SceneSelectionGeometry(Vector3[] positions, int[] indices)
    {
        Positions = positions;
        Indices = indices;
        BoundsCenter = ComputeBoundsCenter(positions);
        BoundsRadius = ComputeBoundsRadius(positions, BoundsCenter);
    }

    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    public Vector3 BoundsCenter { get; }
    public float BoundsRadius { get; }

    internal bool TryGetAcceleration([NotNullWhen(true)] out SceneGeometryAcceleration? acceleration)
    {
        acceleration = Volatile.Read(ref _acceleration);
        return acceleration is not null;
    }

    internal void BuildAcceleration(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _accelerationBuildStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            SceneGeometryAcceleration? acceleration = SceneGeometryAcceleration.Build(this, cancellationToken);
            Volatile.Write(ref _acceleration, acceleration);
        }
        finally
        {
            Volatile.Write(ref _accelerationBuildStarted, 2);
        }
    }

    private static Vector3 ComputeBoundsCenter(Vector3[] positions)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        bool hasFinitePosition = false;
        foreach (Vector3 position in positions)
        {
            if (!NumericsUtility.IsFinite(position))
            {
                continue;
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
            hasFinitePosition = true;
        }

        return hasFinitePosition
            ? (min + max) * 0.5f
            : Vector3.Zero;
    }

    private static float ComputeBoundsRadius(Vector3[] positions, Vector3 center)
    {
        float radiusSquared = 0f;
        foreach (Vector3 position in positions)
        {
            if (!NumericsUtility.IsFinite(position))
            {
                continue;
            }

            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, position));
        }

        return MathF.Sqrt(radiusSquared);
    }
}

/// <summary> resolves model geometry required by the shared scene selection pass </summary>
internal interface ISceneSelectionGeometryProvider
{
    /// <summary> resolves triangle geometry for one model path </summary>
    bool TryGetGeometry(string modelPath, out SceneSelectionGeometry geometry);

    /// <summary> resolves triangle geometry and schedules acceleration for repeated raycasts </summary>
    bool TryGetRaycastGeometry(string modelPath, out SceneSelectionGeometry geometry);

    /// <summary> keeps one model geometry entry warm after a successful selection </summary>
    void Touch(string modelPath);
}

internal static class SceneSelectionPrimitiveGeometry
{
    private static readonly Lazy<SceneSelectionGeometry> UnitBoxGeometry = new(CreateBoxGeometry);
    private static readonly Lazy<SceneSelectionGeometry> UnitSphereGeometry = new(() => CreateSphereGeometry(14, 10));
    private static readonly Lazy<SceneSelectionGeometry> UnitConeGeometry = new(() => CreateConeGeometry(20));

    public static SceneSelectionGeometry Resolve(SceneSelectionPrimitiveKind primitiveKind)
        => primitiveKind switch
        {
            SceneSelectionPrimitiveKind.Box => UnitBoxGeometry.Value,
            SceneSelectionPrimitiveKind.Sphere => UnitSphereGeometry.Value,
            SceneSelectionPrimitiveKind.Cone => UnitConeGeometry.Value,
            _ => UnitSphereGeometry.Value,
        };

    private static SceneSelectionGeometry CreateBoxGeometry()
    {
        Vector3[] positions =
        [
            new(-0.5f, -0.5f, -0.5f),
            new(0.5f, -0.5f, -0.5f),
            new(0.5f, 0.5f, -0.5f),
            new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f),
            new(0.5f, -0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f),
            new(-0.5f, 0.5f, 0.5f),
        ];
        int[] indices =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7,
        ];
        return CreatePreparedGeometry(positions, indices);
    }

    private static SceneSelectionGeometry CreateSphereGeometry(int slices, int stacks)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var stack = 0; stack <= stacks; stack++)
        {
            var v = stack / (float)stacks;
            var phi = v * MathF.PI;
            var y = MathF.Cos(phi);
            var radius = MathF.Sin(phi);

            for (var slice = 0; slice <= slices; slice++)
            {
                var u = slice / (float)slices;
                var theta = u * MathF.PI * 2f;
                positions.Add(new Vector3(
                    radius * MathF.Cos(theta),
                    y,
                    radius * MathF.Sin(theta)));
            }
        }

        var rowLength = slices + 1;
        for (var stack = 0; stack < stacks; stack++)
        {
            for (var slice = 0; slice < slices; slice++)
            {
                var index0 = (stack * rowLength) + slice;
                var index1 = index0 + 1;
                var index2 = index0 + rowLength;
                var index3 = index2 + 1;

                if (stack != 0)
                {
                    indices.Add(index0);
                    indices.Add(index2);
                    indices.Add(index1);
                }

                if (stack != stacks - 1)
                {
                    indices.Add(index1);
                    indices.Add(index2);
                    indices.Add(index3);
                }
            }
        }

        return CreatePreparedGeometry(positions.ToArray(), indices.ToArray());
    }

    private static SceneSelectionGeometry CreateConeGeometry(int segments)
    {
        var positions = new List<Vector3>
        {
            Vector3.Zero,
        };
        var indices = new List<int>();

        for (var segment = 0; segment < segments; segment++)
        {
            var angle = (segment / (float)segments) * MathF.PI * 2f;
            positions.Add(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 1f));
        }

        positions.Add(new Vector3(0f, 0f, 1f));
        var baseCenterIndex = positions.Count - 1;

        for (var segment = 0; segment < segments; segment++)
        {
            var current = 1 + segment;
            var next = 1 + ((segment + 1) % segments);

            indices.Add(0);
            indices.Add(current);
            indices.Add(next);

            indices.Add(baseCenterIndex);
            indices.Add(next);
            indices.Add(current);
        }

        return CreatePreparedGeometry(positions.ToArray(), indices.ToArray());
    }

    private static SceneSelectionGeometry CreatePreparedGeometry(Vector3[] positions, int[] indices)
    {
        var geometry = new SceneSelectionGeometry(positions, indices);
        geometry.BuildAcceleration();
        return geometry;
    }
}
