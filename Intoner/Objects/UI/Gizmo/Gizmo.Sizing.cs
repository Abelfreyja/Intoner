namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private const float ObjectSizeReferenceWorldLength = 1f;
    private const float ObjectSizeMaxWorldLength = 4f;
    private const float ObjectSizeMaxScreenMultiplier = 1.7f;

    private static float ResolveObjectAwareScreenSize(float baseScreenSize, float objectWorldLength, float scale)
    {
        var scaledBase = baseScreenSize * scale;
        var scaledFloor = GizmoConstants.AxisMinScreenLength * scale;
        var scaledCeiling = GizmoConstants.AxisMaxScreenLength * scale;
        return Math.Clamp(scaledBase * ResolveObjectSizeScreenMultiplier(objectWorldLength), scaledFloor, scaledCeiling);
    }

    private static float ResolveObjectSizeScreenMultiplier(float objectWorldLength)
    {
        if (!float.IsFinite(objectWorldLength) || objectWorldLength <= ObjectSizeReferenceWorldLength)
        {
            return 1f;
        }

        var sizeT = Math.Clamp(
            (objectWorldLength - ObjectSizeReferenceWorldLength) / (ObjectSizeMaxWorldLength - ObjectSizeReferenceWorldLength),
            0f,
            1f);
        return 1f + (sizeT * (ObjectSizeMaxScreenMultiplier - 1f));
    }
}

