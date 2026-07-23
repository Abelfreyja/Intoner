using System.Numerics;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Models;

internal sealed record VfxModel : ObjectData
{
    public const float DefaultSpeed = 1f;
    public const float MinSpeed = 0f;
    public const float MaxSpeed = 4f;
    public const float MinFadeInSeconds = 0f;
    public const float MaxFadeInSeconds = 60f;
    public const int DefaultLoopIntervalSeconds = 5;
    public const int MinLoopIntervalSeconds = 1;
    public const int MaxLoopIntervalSeconds = 60;

    public string VfxPath { get; init; } = string.Empty;
    public Vector4 Color { get; init; } = Vector4.One;
    public float Speed { get; init; } = DefaultSpeed;
    public bool Paused { get; init; }
    public float FadeInSeconds { get; init; }
    public bool ReplayOnTransform { get; init; }
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

    public bool NeedsPlaybackState(VfxModel previousModel)
        => !ObjectMathUtility.IsNearlyEqual(previousModel.Speed, Speed)
           || previousModel.Paused != Paused;

    public static float ClampSpeed(float value)
        => float.IsFinite(value) ? Math.Clamp(value, MinSpeed, MaxSpeed) : DefaultSpeed;

    public static float ClampFadeInSeconds(float value)
        => float.IsFinite(value) ? Math.Clamp(value, MinFadeInSeconds, MaxFadeInSeconds) : MinFadeInSeconds;

    public static int ClampLoopIntervalSeconds(int value)
        => Math.Clamp(value, MinLoopIntervalSeconds, MaxLoopIntervalSeconds);
}

