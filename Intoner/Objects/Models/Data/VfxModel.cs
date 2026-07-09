using System.Numerics;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Models;

internal sealed record VfxModel : ObjectData
{
    public const int DefaultLoopIntervalSeconds = 5;
    public const int MinLoopIntervalSeconds = 1;
    public const int MaxLoopIntervalSeconds = 60;

    public string VfxPath { get; init; } = string.Empty;
    public Vector4 Color { get; init; } = Vector4.One;
    public bool Loop { get; init; }
    public int LoopIntervalSeconds { get; init; } = DefaultLoopIntervalSeconds;

    public bool NeedsVisualState(VfxModel? previousModel)
    {
        if (previousModel is null)
        {
            return !ObjectMathUtility.IsNearlyEqual(Color, Vector4.One);
        }

        return !ObjectMathUtility.IsNearlyEqual(previousModel.Color, Color);
    }

    public static int ClampLoopIntervalSeconds(int value)
        => Math.Clamp(value, MinLoopIntervalSeconds, MaxLoopIntervalSeconds);
}

