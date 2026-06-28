using System.Numerics;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Models;

internal sealed record VfxModel : ObjectData
{
    public string VfxPath { get; init; } = string.Empty;
    public Vector4 Color { get; init; } = Vector4.One;

    public bool NeedsVisualState(VfxModel? previousModel)
    {
        if (previousModel is null)
        {
            return !ObjectMathUtility.IsNearlyEqual(Color, Vector4.One);
        }

        return !ObjectMathUtility.IsNearlyEqual(previousModel.Color, Color);
    }
}

