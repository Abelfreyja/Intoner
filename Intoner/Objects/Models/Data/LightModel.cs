using System.Numerics;

namespace Intoner.Objects.Models;

internal enum LightType : uint
{
    WorldLight = 1,
    AreaLight = 2,
    SpotLight = 3,
    FlatLight = 4,
}

internal enum LightFalloffType : uint
{
    Linear = 0,
    Quadratic = 1,
    Cubic = 2,
}

internal sealed record LightFlags
{
    public bool EnableMaterialReflection { get; init; } = true;
    public bool EnableDynamicLighting { get; init; }
    public bool EnableCharacterShadow { get; init; }
    public bool EnableObjectShadow { get; init; }
}

internal sealed record LightShape
{
    public float Range { get; init; } = 35f;
    public float Falloff { get; init; } = 1f;
    public float LightAngle { get; init; } = 45f;
    public float FalloffAngle { get; init; } = 0.5f;
    public Vector2 AngleDegrees { get; init; } = Vector2.Zero;
}

internal sealed record LightShadow
{
    public float CharacterShadowRange { get; init; } = 110f;
    public float ShadowPlaneNear { get; init; } = 0.01f;
    public float ShadowPlaneFar { get; init; } = 17f;
}

internal sealed record LightModel : ObjectData
{
    public Vector3 Color { get; init; } = new(20f, 20f, 20f);
    public LightType LightType { get; init; } = LightType.SpotLight;
    public LightFalloffType FalloffType { get; init; } = LightFalloffType.Quadratic;
    public LightFlags Flags { get; init; } = new();
    public float Intensity { get; init; } = 1f;
    public LightShape Shape { get; init; } = new();
    public LightShadow Shadow { get; init; } = new();
}

