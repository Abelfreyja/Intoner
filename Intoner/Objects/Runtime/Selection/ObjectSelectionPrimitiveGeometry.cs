using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectSelectionPrimitiveGeometry
{
    private static readonly Lazy<ObjectSelectionGeometry> UnitSphereGeometry = new(() => CreateSphereGeometry(14, 10));
    private static readonly Lazy<ObjectSelectionGeometry> UnitConeGeometry = new(() => CreateConeGeometry(20));
    private static readonly Lazy<ObjectSelectionGeometry> UnitPyramidGeometry = new(CreatePyramidGeometry);

    public static ObjectSelectionGeometry Resolve(ObjectSelectionPrimitiveKind primitiveKind)
        => primitiveKind switch
        {
            ObjectSelectionPrimitiveKind.Sphere => UnitSphereGeometry.Value,
            ObjectSelectionPrimitiveKind.Cone => UnitConeGeometry.Value,
            ObjectSelectionPrimitiveKind.Pyramid => UnitPyramidGeometry.Value,
            _ => UnitSphereGeometry.Value,
        };

    private static ObjectSelectionGeometry CreateSphereGeometry(int slices, int stacks)
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

        return new ObjectSelectionGeometry(positions.ToArray(), indices.ToArray());
    }

    private static ObjectSelectionGeometry CreateConeGeometry(int segments)
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

        return new ObjectSelectionGeometry(positions.ToArray(), indices.ToArray());
    }

    private static ObjectSelectionGeometry CreatePyramidGeometry()
    {
        Vector3[] positions =
        [
            Vector3.Zero,
            new(-1f, -1f, 1f),
            new(1f, -1f, 1f),
            new(1f, 1f, 1f),
            new(-1f, 1f, 1f),
        ];

        int[] indices =
        [
            0, 1, 2,
            0, 2, 3,
            0, 3, 4,
            0, 4, 1,
            1, 4, 3,
            1, 3, 2,
        ];

        return new ObjectSelectionGeometry(positions, indices);
    }
}

