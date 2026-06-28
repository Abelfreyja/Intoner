using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoSnapBasisUtility
{
    public static ObjectSnapBasis World { get; } = new(Vector3.Zero, Quaternion.Identity);

    private static ObjectSnapBasis IdentityLocal { get; } = new(Vector3.Zero, Quaternion.Identity);

    public static ObjectSnapBasis CreateLocal(Quaternion rotation)
        => !ObjectMathUtility.HasLength(rotation)
            ? IdentityLocal
            : new ObjectSnapBasis(Vector3.Zero, rotation);
}

