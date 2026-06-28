using System.Numerics;

namespace Intoner.Objects.Models;

internal interface ObjectDataPatch
{
    bool HasChanges { get; }
}

internal sealed record BgObjectModelPatch : ObjectDataPatch
{
    public string? ModelPath { get; init; }
    public float? Transparency { get; init; }
    public Vector4? DyeColor { get; init; }
    public bool? IsCoveredFromRain { get; init; }

    public bool HasChanges
        => ModelPath is not null
           || Transparency.HasValue
           || DyeColor.HasValue
           || IsCoveredFromRain.HasValue;
}

internal sealed record FurnitureColorPatch
{
    public byte? StainId { get; init; }
    public bool? UseCustomColor { get; init; }
    public Vector4? CustomColor { get; init; }

    public bool HasChanges
        => StainId.HasValue
           || UseCustomColor.HasValue
           || CustomColor.HasValue;
}

internal sealed record FurnitureModelPatch : ObjectDataPatch
{
    public string? SharedGroupPath { get; init; }
    public FurnitureColorPatch? Color { get; init; }
    public float? Transparency { get; init; }
    public ObjectOutlineColor? OutlineColor { get; init; }

    public bool HasChanges
        => SharedGroupPath is not null
           || Transparency.HasValue
           || OutlineColor.HasValue
           || Color?.HasChanges == true;
}

internal sealed record VfxModelPatch : ObjectDataPatch
{
    public string? VfxPath { get; init; }
    public Vector4? Color { get; init; }

    public bool HasChanges
        => VfxPath is not null
           || Color.HasValue;
}

internal sealed record LightFlagsPatch
{
    public bool? EnableMaterialReflection { get; init; }
    public bool? EnableDynamicLighting { get; init; }
    public bool? EnableCharacterShadow { get; init; }
    public bool? EnableObjectShadow { get; init; }

    public bool HasChanges
        => EnableMaterialReflection.HasValue
           || EnableDynamicLighting.HasValue
           || EnableCharacterShadow.HasValue
           || EnableObjectShadow.HasValue;
}

internal sealed record LightShapePatch
{
    public float? Range { get; init; }
    public float? Falloff { get; init; }
    public float? LightAngle { get; init; }
    public float? FalloffAngle { get; init; }
    public Vector2? AngleDegrees { get; init; }

    public bool HasChanges
        => Range.HasValue
           || Falloff.HasValue
           || LightAngle.HasValue
           || FalloffAngle.HasValue
           || AngleDegrees.HasValue;
}

internal sealed record LightShadowPatch
{
    public float? CharacterShadowRange { get; init; }
    public float? ShadowPlaneNear { get; init; }
    public float? ShadowPlaneFar { get; init; }

    public bool HasChanges
        => CharacterShadowRange.HasValue
           || ShadowPlaneNear.HasValue
           || ShadowPlaneFar.HasValue;
}

internal sealed record LightModelPatch : ObjectDataPatch
{
    public Vector3? Color { get; init; }
    public LightType? LightType { get; init; }
    public LightFalloffType? FalloffType { get; init; }
    public LightFlagsPatch? Flags { get; init; }
    public float? Intensity { get; init; }
    public LightShapePatch? Shape { get; init; }
    public LightShadowPatch? Shadow { get; init; }

    public bool HasChanges
        => Color.HasValue
           || LightType.HasValue
           || FalloffType.HasValue
           || Intensity.HasValue
           || Flags?.HasChanges == true
           || Shape?.HasChanges == true
           || Shadow?.HasChanges == true;
}

