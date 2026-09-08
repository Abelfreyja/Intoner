using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoSnapBasisUtility
{
    public static SceneSnapBasis World { get; } = new(Vector3.Zero, Quaternion.Identity);

    private static SceneSnapBasis IdentityLocal { get; } = new(Vector3.Zero, Quaternion.Identity);

    public static SceneSnapBasis CreateLocal(Quaternion rotation)
        => !NumericsUtility.HasLength(rotation)
            ? IdentityLocal
            : new SceneSnapBasis(Vector3.Zero, rotation);
}

