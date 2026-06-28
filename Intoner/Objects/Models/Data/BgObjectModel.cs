using System.Numerics;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Models;

internal sealed record BgObjectModel : ObjectData
{
    public string ModelPath { get; init; } = string.Empty;
    public float Transparency { get; init; }
    public Vector4 DyeColor { get; init; } = Vector4.One;
    public bool IsCoveredFromRain { get; init; }

    public bool NeedsVisualState(BgObjectModel? previousModel)
    {
        if (!ObjectMathUtility.IsNearlyZero(Transparency)
         || !ObjectMathUtility.IsNearlyEqual(DyeColor, Vector4.One)
         || IsCoveredFromRain)
        {
            return true;
        }

        return previousModel is not null
            && (!ObjectMathUtility.IsNearlyEqual(previousModel.Transparency, Transparency)
                || !ObjectMathUtility.IsNearlyEqual(previousModel.DyeColor, DyeColor)
                || previousModel.IsCoveredFromRain != IsCoveredFromRain);
    }
}

