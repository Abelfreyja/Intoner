using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoAxisUtility
{
    public const int AxisCount = 3;

    public static GizmoAxis FromIndex(int index)
        => index switch
        {
            0 => GizmoAxis.X,
            1 => GizmoAxis.Y,
            2 => GizmoAxis.Z,
            _ => GizmoAxis.None,
        };

    public static int ToIndex(GizmoAxis axis)
        => axis switch
        {
            GizmoAxis.X => 0,
            GizmoAxis.Y => 1,
            GizmoAxis.Z => 2,
            _ => -1,
        };

    public static Vector3 ToUnitVector(GizmoAxis axis)
        => axis switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            _ => Vector3.Zero,
        };
}

