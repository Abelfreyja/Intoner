using Intoner.Objects.Filesystem.Configuration;

namespace Intoner.Objects.Rendering.Primitives;

internal static class PrimitiveAntiAlias
{
    private const float GeometryPaddingBias = 0.25f;
    private const float CapOverlapMultiplier = 0.5f;

    public static PrimitiveAntiAliasParameters ResolveParameters(int strength)
    {
        var transitionWidth = RenderingConfiguration.AntiAliasingToPixels(strength);
        if (transitionWidth <= 0f)
        {
            return PrimitiveAntiAliasParameters.Off;
        }

        return new PrimitiveAntiAliasParameters(
            transitionWidth + GeometryPaddingBias,
            transitionWidth,
            transitionWidth * CapOverlapMultiplier);
    }
}

internal readonly record struct PrimitiveAntiAliasParameters(
    float GeometryPadding,
    float TransitionWidth,
    float CapOverlap)
{
    public static readonly PrimitiveAntiAliasParameters Off = new(0f, 0f, 0f);
}

