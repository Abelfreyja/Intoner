namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private const float WorldSizeReferenceLength = 1f;
    private const float WorldSizeMaxLength = 4f;
    private const float WorldSizeMaxScreenMultiplier = 1.7f;

    private static float ResolveWorldScaledScreenSize(float baseScreenSize, float worldLength, float scale)
    {
        var scaledBase = baseScreenSize * scale;
        var scaledFloor = GizmoConstants.AxisMinScreenLength * scale;
        var scaledCeiling = GizmoConstants.AxisMaxScreenLength * scale;
        return Math.Clamp(scaledBase * ResolveWorldSizeScreenMultiplier(worldLength), scaledFloor, scaledCeiling);
    }

    private static float ResolveWorldSizeScreenMultiplier(float worldLength)
    {
        if (!float.IsFinite(worldLength) || worldLength <= WorldSizeReferenceLength)
        {
            return 1f;
        }

        var sizeT = Math.Clamp(
            (worldLength - WorldSizeReferenceLength) / (WorldSizeMaxLength - WorldSizeReferenceLength),
            0f,
            1f);
        return 1f + (sizeT * (WorldSizeMaxScreenMultiplier - 1f));
    }
}

